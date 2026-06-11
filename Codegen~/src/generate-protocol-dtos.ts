import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { writeFileSync, mkdirSync } from "node:fs";
import { Parser, fromFile } from "@asyncapi/parser";
import {
  CSharpGenerator,
  type CSharpPreset,
  type OutputModel,
} from "@asyncapi/modelina";

// ---- Paths ----
const here = dirname(fileURLToPath(import.meta.url));
const specPath = resolve(here, "../schemas/convai-asyncapi.yml");
const protocolDir = resolve(here, "../../Runtime/Core/Protocol");
const roundTripDir = resolve(here, "../round-trip");

// ---- Load and preprocess spec ----

// @asyncapi/parser resolves all $refs into the document, so each payload is
// self-contained by the time we hand it to Modelina. (Earlier we did this
// inlining by hand; the parser is the same tool the JS SDK uses for codegen.)
const parser = new Parser();
const { document, diagnostics } = await fromFile(parser, specPath).parse();
for (const d of diagnostics) {
  if (d.severity <= 1) {
    const where = d.path?.join(".") ?? "";
    process.stderr.write(
      `[severity=${d.severity}] ${d.message}${where ? ` (${where})` : ""}\n`,
    );
  }
}
if (!document) {
  process.stderr.write(`Failed to parse ${specPath}\n`);
  process.exit(1);
}
const spec = document.json() as Record<string, unknown>;

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

// The parser-resolved document has shared subtrees (and cyclical $refs for
// recursive schemas like DynamicVariableNestedValueType), so a naive recursion
// either loops forever or rewrites the same node multiple times. Each
// preprocessing pass guards itself with a WeakSet of already-visited objects.

// Rewrite Fern's `x-fern-type: literal<"foo">` to vanilla JSON Schema
// { type: "string", const: "foo" } and mark the property required.
function substituteFernLiterals(node: unknown, visited: WeakSet<object>): void {
  if (Array.isArray(node)) {
    if (visited.has(node)) return;
    visited.add(node);
    for (const item of node) substituteFernLiterals(item, visited);
    return;
  }
  if (!isObject(node)) return;
  if (visited.has(node)) return;
  visited.add(node);

  if (isObject(node.properties)) {
    const required = new Set<string>(
      Array.isArray(node.required) ? (node.required as string[]) : [],
    );
    for (const [propName, propSchema] of Object.entries(node.properties)) {
      if (!isObject(propSchema)) continue;
      const fernType = propSchema["x-fern-type"];
      if (typeof fernType !== "string") continue;
      const match = fernType.match(/^literal<"(.+)">$/);
      if (!match) continue;
      propSchema.type = "string";
      propSchema.const = match[1];
      delete propSchema["x-fern-type"];
      required.add(propName);
    }
    if (required.size > 0) node.required = [...required];
  }

  for (const value of Object.values(node)) substituteFernLiterals(value, visited);
}

substituteFernLiterals(spec, new WeakSet());

// Modelina generates a C# `enum` (with extension methods) for any string field
// that has an `enum:` constraint. That changes the public API and requires a
// JsonStringEnumConverter to round-trip correctly. Strip the constraint so
// these fields stay plain `string` — server-side validation is the source of
// truth for allowed values anyway.
function stripStringEnums(node: unknown, visited: WeakSet<object>): void {
  if (Array.isArray(node)) {
    if (visited.has(node)) return;
    visited.add(node);
    for (const item of node) stripStringEnums(item, visited);
    return;
  }
  if (!isObject(node)) return;
  if (visited.has(node)) return;
  visited.add(node);
  if (node.type === "string" && Array.isArray(node.enum)) delete node.enum;
  for (const value of Object.values(node)) stripStringEnums(value, visited);
}

stripStringEnums(spec, new WeakSet());

// The parser inlines $refs via shared object references, which produces true
// cycles for recursive schemas (e.g. DynamicVariableNestedValueType). Modelina
// stringifies the input internally, so any cycle in the payload kills it.
// Deep-clone each payload, replacing cyclic back-references with `{}` so
// Modelina sees a free-form object at the cycle boundary.
function breakCycles(node: unknown, ancestors: Set<object>): unknown {
  if (Array.isArray(node)) {
    if (ancestors.has(node)) return [];
    ancestors.add(node);
    const cloned = node.map((item) => breakCycles(item, ancestors));
    ancestors.delete(node);
    return cloned;
  }
  if (!isObject(node)) return node;
  if (ancestors.has(node)) return {};
  ancestors.add(node);
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(node)) out[k] = breakCycles(v, ancestors);
  ancestors.delete(node);
  return out;
}

// ---- Collect payloads per direction ----

type Direction = "incoming" | "outgoing";

interface Payload {
  name: string;
  schema: Record<string, unknown>;
}

interface RawMessage {
  name?: string;
  payload?: Record<string, unknown>;
}

interface ChannelOp {
  message?: { oneOf?: RawMessage[] } | RawMessage;
}

interface Channel {
  publish?: ChannelOp;
  subscribe?: ChannelOp;
}

function collectPayloads(direction: Direction): Payload[] {
  const channels = (spec as { channels: Record<string, Channel> }).channels;
  const channel = Object.values(channels)[0];
  // AsyncAPI 2.x publisher's PoV: publish = client → server (outgoing).
  const op = channel?.[direction === "outgoing" ? "publish" : "subscribe"];
  const msg = op?.message;
  if (!msg) return [];

  // After parser resolution, `oneOf` holds the inlined message objects (each
  // with `.payload` already inlined). Single-message channels skip `oneOf`.
  const messages: RawMessage[] =
    "oneOf" in msg && msg.oneOf
      ? msg.oneOf
      : [msg as RawMessage];

  const results: Payload[] = [];
  for (const message of messages) {
    const payload = message.payload;
    if (!payload) continue;
    // The parser stamps `x-parser-schema-id` on each schema using the
    // components.schemas key — that's the name we want for the model.
    const schemaId = payload["x-parser-schema-id"];
    const name =
      typeof schemaId === "string" && !schemaId.startsWith("<")
        ? schemaId
        : (message.name ?? "");
    if (!name) continue;
    // Break cycles (see breakCycles comment) and inject $id so Modelina names
    // the root model. Nested types are named from their containing property by
    // Modelina's default constraint.
    const safe = breakCycles(payload, new Set()) as Record<string, unknown>;
    results.push({ name, schema: { ...safe, $id: name } });
  }
  return results;
}

const incoming = collectPayloads("incoming");
const outgoing = collectPayloads("outgoing");

console.log(
  `Incoming (${incoming.length}): ${incoming.map((p) => p.name).join(", ")}`,
);
console.log(
  `Outgoing (${outgoing.length}): ${outgoing.map((p) => p.name).join(", ")}`,
);

// ---- Helpers ----

function toPascalCase(s: string): string {
  return s
    .split(/[_\s-]+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join("");
}

const PRIMITIVE_TYPES = new Set([
  "string",
  "int",
  "long",
  "short",
  "byte",
  "float",
  "double",
  "decimal",
  "bool",
  "char",
]);

function isPrimitive(csType: string): boolean {
  const base = csType.replace(/\?$/, "");
  return PRIMITIVE_TYPES.has(base);
}

function isCollection(csType: string): boolean {
  return csType.startsWith("List<") || csType.startsWith("Dictionary<");
}

// ---- Custom Modelina preset ----

// Modelina's default class renderer emits `public T name { get; set; }` without
// JSON attributes; the bundled JsonSerializerPreset emits per-class
// JsonConverter<T> classes, which is more invasive than we want.
// Override the property renderer to emit [JsonPropertyName] + a standard
// auto-property, with init-only literal default for `const` properties.
const protocolPreset: CSharpPreset = {
  class: {
    self({ renderer, content }) {
      renderer.dependencyManager.addDependency(
        "using System.Text.Json.Serialization;",
      );
      return content;
    },
    property({ renderer, property }) {
      const jsonName = property.unconstrainedPropertyName;
      let propName = toPascalCase(jsonName);
      // C# CS0542: a property cannot share its enclosing type's name.
      // `renderer.model` is protected, but it's the ConstrainedObjectModel for
      // the current class; cast to access `.name` from the preset.
      const enclosingName = (renderer as unknown as { model: { name: string } })
        .model.name;
      if (propName === enclosingName) propName = `${propName}Data`;

      const csType = property.property.type;
      const isRequired = property.required;
      const attr = `[JsonPropertyName("${jsonName}")]`;

      // const literal → init-only string property with the literal default.
      const constOpt = property.property.options.const;
      if (constOpt && constOpt.value !== undefined) {
        // Modelina stores the const value as a JSON-encoded string token,
        // e.g. '"foo"'. Unwrap if needed.
        const raw = constOpt.value;
        const literal =
          typeof raw === "string" && raw.startsWith('"') && raw.endsWith('"')
            ? JSON.parse(raw)
            : String(raw);
        return `${attr}\npublic string ${propName} { get; init; } = "${literal}";`;
      }

      // Nullability: optional non-primitive → append `?`; required reference
      // → suppress CS8618 via `= null!`.
      const nullableSuffix =
        !isRequired && !csType.endsWith("?") && !isPrimitive(csType) ? "?" : "";
      const renderedType = `${csType}${nullableSuffix}`;

      let initializer = "";
      if (isRequired) {
        if (csType === "string") initializer = ' = ""';
        else if (csType === "int" || csType === "long") initializer = " = 0";
        else if (csType === "float" || csType === "double")
          initializer = " = 0";
        else if (csType === "bool") initializer = " = false";
        else if (isCollection(csType)) initializer = " = new()";
        else if (!isPrimitive(csType)) initializer = " = null!";
      }

      const semicolon = initializer ? ";" : "";
      return `${attr}\npublic ${renderedType} ${propName} { get; set; }${initializer}${semicolon}`;
    },
  },
};

// ---- Generate ----

interface GenerateResult {
  rootName: string;
  models: OutputModel[];
}

const generator = new CSharpGenerator({
  presets: [protocolPreset],
  collectionType: "List",
  autoImplementedProperties: true,
  handleNullable: true,
  processorOptions: {
    interpreter: {
      ignoreAdditionalProperties: true,
    },
  },
});

async function generateForPayloads(
  payloads: Payload[],
): Promise<GenerateResult[]> {
  const results: GenerateResult[] = [];
  for (const { name, schema } of payloads) {
    const models = await generator.generate(schema);
    const root = models.find((m) => m.modelName === name) ?? models[0];
    results.push({ rootName: root?.modelName ?? name, models });
  }
  return results;
}

const incomingResults = await generateForPayloads(incoming);
const outgoingResults = await generateForPayloads(outgoing);

// ---- Aggregate into files ----

function dedupeModels(
  results: GenerateResult[],
): { allModels: OutputModel[]; topLevel: Set<string> } {
  const allModels = new Map<string, OutputModel>();
  const topLevel = new Set<string>();
  for (const { rootName, models } of results) {
    topLevel.add(rootName);
    for (const m of models) {
      if (!allModels.has(m.modelName)) allModels.set(m.modelName, m);
    }
  }
  return { allModels: [...allModels.values()], topLevel };
}

function indent(s: string, n = 1): string {
  return s
    .split("\n")
    .map((line) => (line ? "    ".repeat(n) + line : line))
    .join("\n");
}

function emitFile(baseClass: string, results: GenerateResult[]): string {
  const { allModels, topLevel } = dedupeModels(results);

  const usings = new Set<string>(["using System.Collections.Generic;"]);
  for (const m of allModels) {
    for (const dep of m.dependencies) {
      if (dep.trim().startsWith("using ")) usings.add(dep.trim());
    }
  }

  const classBodies = allModels.map((m) => {
    let body = m.result;
    if (topLevel.has(m.modelName)) {
      // Modelina emits `public partial class Name {` — inject the base class.
      body = body.replace(
        new RegExp(`public (partial )?class ${m.modelName}\\b(?! :)`),
        `public class ${m.modelName} : ${baseClass}`,
      );
    } else {
      // Strip `partial` from nested types too, for consistency.
      body = body.replace(
        new RegExp(`public partial class ${m.modelName}\\b`),
        `public class ${m.modelName}`,
      );
    }
    return indent(body);
  });

  return [
    "// <auto-generated />",
    "// Source: Codegen~/schemas/convai-asyncapi.yml",
    "// Generator: pnpm --dir Codegen~ run generate",
    "",
    "#nullable enable",
    "",
    ...[...usings].sort(),
    "",
    "namespace ElevenLabs.Protocol",
    "{",
    `    public abstract class ${baseClass}`,
    "    {",
    "    }",
    "",
    classBodies.join("\n\n"),
    "}",
    "",
  ].join("\n");
}

mkdirSync(protocolDir, { recursive: true });
const incomingPath = resolve(protocolDir, "IncomingSocketEvent.cs");
const outgoingPath = resolve(protocolDir, "OutgoingSocketEvent.cs");
writeFileSync(incomingPath, emitFile("IncomingSocketEvent", incomingResults));
writeFileSync(outgoingPath, emitFile("OutgoingSocketEvent", outgoingResults));
console.log(`Wrote ${incomingPath}`);
console.log(`Wrote ${outgoingPath}`);

// ---- Round-trip program ----

mkdirSync(roundTripDir, { recursive: true });
const roundTripPath = resolve(roundTripDir, "Program.cs");
writeFileSync(
  roundTripPath,
  [
    "// <auto-generated />",
    "// Source: Codegen~/schemas/convai-asyncapi.yml",
    "// Generator: pnpm --dir Codegen~ run generate",
    "// Run with: dotnet run --project Codegen~/round-trip/",
    "//",
    "// AOT/IL2CPP note: the DTOs use System.Text.Json [JsonPropertyName] attributes.",
    "// Source-generated serialization (JsonSerializerContext) — required for IL2CPP —",
    "// is deferred until Unity can compile the package (task 3.4 in plan-b.md).",
    "",
    "using System.Text.Json;",
    "using ElevenLabs.Protocol;",
    "",
    "static string Roundtrip<T>(T instance) where T : notnull",
    "{",
    "    var json = JsonSerializer.Serialize(instance);",
    "    var back = JsonSerializer.Deserialize<T>(json)",
    '        ?? throw new InvalidOperationException($"Deserialize<{typeof(T).Name}> returned null");',
    "    var roundTripped = JsonSerializer.Serialize(back);",
    "    if (json != roundTripped)",
    "        throw new InvalidOperationException(",
    '            $"Round-trip mismatch for {typeof(T).Name}:\\n  before: {json}\\n  after:  {roundTripped}");',
    "    return json;",
    "}",
    "",
    "// --- IncomingSocketEvent subtypes ---",
    ...incoming.map((p) => `Console.WriteLine(Roundtrip(new ${p.name}()));`),
    "",
    "// --- OutgoingSocketEvent subtypes ---",
    ...outgoing.map((p) => `Console.WriteLine(Roundtrip(new ${p.name}()));`),
    "",
    'Console.WriteLine("All round-trips passed.");',
    "",
  ].join("\n"),
);
console.log(`Wrote ${roundTripPath}`);

const info = (spec as { info?: { title?: string; version?: string } }).info;
console.log(`Done — ${info?.title ?? "<no title>"} v${info?.version ?? "?"}`);

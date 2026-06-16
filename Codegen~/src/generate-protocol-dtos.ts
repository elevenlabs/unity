import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { writeFileSync, mkdirSync } from "node:fs";
import { Parser, fromFile } from "@asyncapi/parser";
import {
  CSharpGenerator,
  ConstrainedObjectModel,
  ConstrainedObjectPropertyModel,
  ConstrainedReferenceModel,
  type OutputModel,
} from "@asyncapi/modelina";
import {
  isPrimitive,
  makeProtocolPreset,
  toPascalCase,
} from "./protocol-preset.ts";

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

  for (const value of Object.values(node))
    substituteFernLiterals(value, visited);
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
    "oneOf" in msg && msg.oneOf ? msg.oneOf : [msg as RawMessage];

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

// ---- Generate ----

interface GenerateResult {
  rootName: string;
  models: OutputModel[];
}

async function generateForPayloads(
  payloads: Payload[],
  baseClass: string,
): Promise<GenerateResult[]> {
  const topLevelNames = new Set(payloads.map((p) => p.name));
  const generator = new CSharpGenerator({
    presets: [makeProtocolPreset(baseClass, topLevelNames)],
    collectionType: "List",
    autoImplementedProperties: true,
    handleNullable: true,
    processorOptions: {
      interpreter: {
        ignoreAdditionalProperties: true,
      },
    },
  });
  const results: GenerateResult[] = [];
  for (const { name, schema } of payloads) {
    const models = await generator.generate(schema);
    const root = models.find((m) => m.modelName === name) ?? models[0];
    results.push({ rootName: root?.modelName ?? name, models });
  }
  return results;
}

const incomingResults = await generateForPayloads(
  incoming,
  "IncomingSocketEvent",
);
const outgoingResults = await generateForPayloads(
  outgoing,
  "OutgoingSocketEvent",
);

// ---- Aggregate into files ----

function dedupeModels(results: GenerateResult[]): OutputModel[] {
  const allModels = new Map<string, OutputModel>();
  for (const { models } of results) {
    for (const m of models) {
      if (!allModels.has(m.modelName)) allModels.set(m.modelName, m);
    }
  }
  return [...allModels.values()];
}

function indent(s: string, n = 1): string {
  return s
    .split("\n")
    .map((line) => (line ? "    ".repeat(n) + line : line))
    .join("\n");
}

function emitFile(baseClass: string, results: GenerateResult[]): string {
  const allModels = dedupeModels(results);

  const usings = new Set<string>(["using System.Collections.Generic;"]);
  for (const m of allModels) {
    for (const dep of m.dependencies) {
      if (dep.trim().startsWith("using ")) usings.add(dep.trim());
    }
  }

  const classBodies = allModels.map((m) => indent(m.result));

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
const incomingPath = resolve(protocolDir, "IncomingSocketEvent.g.cs");
const outgoingPath = resolve(protocolDir, "OutgoingSocketEvent.g.cs");
writeFileSync(incomingPath, emitFile("IncomingSocketEvent", incomingResults));
writeFileSync(outgoingPath, emitFile("OutgoingSocketEvent", outgoingResults));
console.log(`Wrote ${incomingPath}`);
console.log(`Wrote ${outgoingPath}`);

// ---- Per-event dispatcher (sibling of the DTO classes) ----

// Emits an abstract base class that fans an IncomingSocketEvent out to one
// strongly-typed event per wire payload (plus an OnUnhandled fallback for
// types the server adds ahead of an SDK refresh). Conversation inherits and
// calls Dispatch(...) from its message router; combining related events
// (e.g. agent_tool_response{,_full_payload}) and side effects (auto-pong,
// end_call shortcuts) stay in Conversation, so this stays a strict 1:1 fan-out.
function emitIncomingDispatcher(payloads: Payload[]): string {
  const names = payloads.map((p) => p.name);
  const eventDecls = names.map(
    (n) => `        public event System.Action<${n}>? On${n};`,
  );
  const cases = names.map(
    (n) =>
      `                case ${n} e:\n                    On${n}?.Invoke(e);\n                    break;`,
  );
  return [
    "// <auto-generated />",
    "// Source: Codegen~/schemas/convai-asyncapi.yml",
    "// Generator: pnpm --dir Codegen~ run generate",
    "",
    "#nullable enable",
    "",
    "namespace ElevenLabs.Protocol",
    "{",
    "    public abstract class IncomingEventDispatcher",
    "    {",
    eventDecls.join("\n\n"),
    "",
    "        // Wire types the server may add ahead of an SDK refresh land here",
    "        // (mirrors @elevenlabs/client BaseConversation's onDebug arm).",
    "        public event System.Action<IncomingSocketEvent>? OnUnhandled;",
    "",
    "        protected void Dispatch(IncomingSocketEvent evt)",
    "        {",
    "            switch (evt)",
    "            {",
    cases.join("\n"),
    "                default:",
    "                    OnUnhandled?.Invoke(evt);",
    "                    break;",
    "            }",
    "        }",
    "    }",
    "}",
    "",
  ].join("\n");
}

const dispatcherPath = resolve(protocolDir, "IncomingEventDispatcher.g.cs");
writeFileSync(dispatcherPath, emitIncomingDispatcher(incoming));
console.log(`Wrote ${dispatcherPath}`);

// ---- Per-event args records (sibling of the DTO + dispatcher files) ----

// Emits one flat `record {WireType}Args` per incoming wire payload plus a
// `IncomingEventArgsExtensions.ToArgs(this WireType e)` extension. Conversation
// subscribes to the dispatcher's wire-typed events and re-raises idiomatic
// user-facing events whose payloads are these args records — the codegen
// provides building blocks; combining, suppression, side effects, and event
// naming stay hand-written in Conversation.
//
// Transformation rule (per the Phase 3 spec in plan-b.md):
//  - Strip the redundant `type` property.
//  - If exactly one property remains and it's a reference to a nested object,
//    flatten that inner object's fields onto the args record. Otherwise copy
//    the non-`type` properties as-is.
//  - Field names verbatim from the inner type (no renaming heuristic) —
//    Conversation can rename at its own surface.
//  - Nested complex types (e.g. AudioEventAlignment) are reused from the wire
//    DTO file by name, not re-emitted.
//  - `ToArgs()` uses named arguments so spec-driven field reordering doesn't
//    silently miswire constructor positions.
//
// Implementation: introspects each top-level wire payload's already-emitted
// Modelina ConstrainedObjectModel; reuses its property names, C# types,
// nullability rules, and reference targets. No re-running Modelina against a
// transformed schema, no hand-rolled JSON-Schema → C# mapping — that keeps
// the args output zero-drift from the DTO output by construction.

// Two wire types currently hit the same wrapper/envelope name-clash bug as
// the DTO output (3.4 in plan-b.md): the generated wrapper class collides
// with the inner type and renames the property to `…Data`. Until that bug is
// fixed at the wire layer, the args generator skips these and Conversation
// hand-writes their mapping.
const SKIPPED_ARGS = new Set([
  "ClientToolCall",
  "AgentToolResponseFullPayload",
]);

interface ArgsField {
  name: string;
  csType: string;
}

interface ArgsRecord {
  wireType: string;
  recordName: string;
  fields: ArgsField[];
  // C# property name of the inner wrapper on the wire DTO, e.g.
  // "AgentResponseEvent" for AgentResponse. Undefined when not flattened.
  innerProperty?: string;
}

function csPropName(
  enclosing: string,
  p: ConstrainedObjectPropertyModel,
): string {
  let name = toPascalCase(p.unconstrainedPropertyName);
  if (name === enclosing) name = `${name}Data`;
  return name;
}

function csPropType(p: ConstrainedObjectPropertyModel): string {
  const baseType = p.property.type;
  if (!p.required && !baseType.endsWith("?") && !isPrimitive(baseType)) {
    return `${baseType}?`;
  }
  return baseType;
}

function buildArgsRecord(wireModel: OutputModel): ArgsRecord | null {
  const obj = wireModel.model;
  if (!(obj instanceof ConstrainedObjectModel)) return null;
  const wireType = obj.name;
  if (SKIPPED_ARGS.has(wireType)) return null;

  const nonType = Object.values(obj.properties).filter(
    (p) => p.unconstrainedPropertyName !== "type",
  );

  // Flatten when the only remaining property points to a nested object DTO.
  if (nonType.length === 1) {
    const wrapper = nonType[0];
    const wrapperProp = wrapper.property;
    if (wrapperProp instanceof ConstrainedReferenceModel) {
      const inner = wrapperProp.ref;
      if (inner instanceof ConstrainedObjectModel) {
        const innerCsName = csPropName(wireType, wrapper);
        const fields: ArgsField[] = Object.values(inner.properties).map(
          (ip) => ({
            name: csPropName(inner.name, ip),
            csType: csPropType(ip),
          }),
        );
        return {
          wireType,
          recordName: `${wireType}Args`,
          fields,
          innerProperty: innerCsName,
        };
      }
    }
  }

  // Otherwise copy the non-type properties as-is.
  const fields: ArgsField[] = nonType.map((p) => ({
    name: csPropName(wireType, p),
    csType: csPropType(p),
  }));
  return { wireType, recordName: `${wireType}Args`, fields };
}

function emitIncomingArgs(
  payloads: Payload[],
  results: GenerateResult[],
): string {
  const modelByName = new Map<string, OutputModel>();
  for (const { models } of results) {
    for (const m of models)
      if (!modelByName.has(m.modelName)) modelByName.set(m.modelName, m);
  }

  const records: ArgsRecord[] = [];
  for (const { name } of payloads) {
    const wireModel = modelByName.get(name);
    if (!wireModel) continue;
    const rec = buildArgsRecord(wireModel);
    if (rec) records.push(rec);
  }

  const recordDecls = records.map((rec) => {
    const params = rec.fields.map((f) => `${f.csType} ${f.name}`).join(", ");
    return `    public record ${rec.recordName}(${params});`;
  });

  const extensionMethods = records.map((rec) => {
    const args = rec.fields
      .map((f) => {
        const access = rec.innerProperty
          ? `e.${rec.innerProperty}.${f.name}`
          : `e.${f.name}`;
        return `${f.name}: ${access}`;
      })
      .join(", ");
    return [
      `        public static ${rec.recordName} ToArgs(this ${rec.wireType} e) =>`,
      `            new(${args});`,
    ].join("\n");
  });

  return [
    "// <auto-generated />",
    "// Source: Codegen~/schemas/convai-asyncapi.yml",
    "// Generator: pnpm --dir Codegen~ run generate",
    "",
    "#nullable enable",
    "",
    "namespace ElevenLabs.Protocol",
    "{",
    recordDecls.join("\n\n"),
    "",
    "    public static class IncomingEventArgsExtensions",
    "    {",
    extensionMethods.join("\n\n"),
    "    }",
    "}",
    "",
  ].join("\n");
}

const argsPath = resolve(protocolDir, "IncomingEventArgs.g.cs");
writeFileSync(argsPath, emitIncomingArgs(incoming, incomingResults));
console.log(`Wrote ${argsPath}`);

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
    "// AOT/IL2CPP note: the DTOs use Newtonsoft.Json [JsonProperty] attributes.",
    "// The Unity package consumer pulls in `com.unity.nuget.newtonsoft-json` (v3.x,",
    "// wrapping Newtonsoft.Json 13.0.1) which ships an AOT-friendly build for",
    "// IL2CPP/WebGL out of the box — no source-generator dance required.",
    "",
    "using Newtonsoft.Json;",
    "using ElevenLabs.Protocol;",
    "",
    "static string Roundtrip<T>(T instance) where T : notnull",
    "{",
    "    var json = JsonConvert.SerializeObject(instance);",
    "    var back = JsonConvert.DeserializeObject<T>(json)",
    '        ?? throw new InvalidOperationException($"Deserialize<{typeof(T).Name}> returned null");',
    "    var roundTripped = JsonConvert.SerializeObject(back);",
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

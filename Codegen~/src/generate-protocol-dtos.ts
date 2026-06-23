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
  sanitizeCSType,
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

/**
 * In-place rewrites every Fern `x-fern-type: literal<"foo">` discriminator into
 * vanilla JSON Schema (`{ type: "string", const: "foo" }`) and marks the
 * property required. `visited` is a cycle guard — pass a fresh `WeakSet` at
 * each top-level call.
 */
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

/**
 * In-place strips `enum:` from every string field in the schema tree so
 * Modelina emits plain `string` properties instead of generating C# enums.
 * `visited` is a cycle guard — pass a fresh `WeakSet` at each top-level call.
 */
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

/**
 * Returns a deep clone of `node` with cyclic back-references replaced by `{}`,
 * so the result is safe to pass to Modelina (which `JSON.stringify`s its input
 * and chokes on cycles). `ancestors` is a cycle guard — pass a fresh `Set` at
 * each top-level call.
 */
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

/**
 * In-place renames inline nested-object properties whose PascalCased name
 * would collide with `wrapperName` (e.g. `ClientToolCall` with inner
 * `client_tool_call`), by injecting `$id: '{wrapperName}Event'`. Without this,
 * Modelina's name-keyed deduplication collapses the inner type onto the
 * wrapper and produces a self-referential property.
 */
function disambiguateNestedTypeNames(
  schema: Record<string, unknown>,
  wrapperName: string,
): void {
  const properties = schema.properties;
  if (!isObject(properties)) return;
  for (const [propName, propSchema] of Object.entries(properties)) {
    if (!isObject(propSchema)) continue;
    if (propSchema.type !== "object") continue;
    if (typeof propSchema.$id === "string") continue; // already named
    if (toPascalCase(propName) === wrapperName) {
      propSchema.$id = `${wrapperName}Event`;
    }
  }
}

// ---- Collect payloads per direction ----

type Direction = "incoming" | "outgoing";

interface Payload {
  name: string;
  schema: Record<string, unknown>;
  /**
   * AsyncAPI message-level `description:` (verbatim), used by emitters to
   * attach `<summary>` XML doc comments. Undefined when the spec leaves the
   * message bare — emitters fall back to no summary in that case.
   */
  description?: string;
  /**
   * Wire-level `type` discriminator (e.g. `"agent_response"`). Extracted from
   * the schema's `properties.type.const` after Fern-literal preprocessing.
   * Used by `emitIncomingSocketEventConverter` to switch on the discriminator
   * when deserialising polymorphic incoming events. Undefined when the schema
   * has no `type` field (e.g. `UserAudioChunk` on the outgoing side).
   */
  typeDiscriminator?: string;
}

interface RawMessage {
  name?: string;
  description?: string;
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
    disambiguateNestedTypeNames(safe, name);
    const description =
      typeof message.description === "string" && message.description.trim()
        ? message.description.trim()
        : undefined;
    // The Fern-literal preprocess pass replaced `x-fern-type: literal<"foo">`
    // with `{ type: string, const: "foo" }` on the `type` property — extract
    // that const so the polymorphic converter generator can switch on it.
    const typeDiscriminator = extractTypeDiscriminator(safe);
    results.push({
      name,
      schema: { ...safe, $id: name },
      description,
      typeDiscriminator,
    });
  }
  return results;
}

/**
 * Returns the const string on `properties.type` if present, else undefined.
 * Walked from the wire-payload root (e.g. `AudioResponse`) — does not recurse
 * into nested payload objects since those have their own discriminator slot
 * separate from the envelope.
 */
function extractTypeDiscriminator(schema: unknown): string | undefined {
  if (!isObject(schema)) return undefined;
  const props = schema.properties;
  if (!isObject(props)) return undefined;
  const typeProp = props.type;
  if (!isObject(typeProp)) return undefined;
  const value = typeProp.const;
  return typeof value === "string" ? value : undefined;
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

/**
 * Escapes `<`, `>`, and `&` for safe inclusion inside a C# `///` XML doc
 * comment.
 */
function escapeXmlDocText(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

/**
 * Renders `description` as a single-line C# `/// <summary>...</summary>`,
 * indented `indentLevel` levels of 4 spaces. Returns `null` when `description`
 * is undefined so callers can omit the doc block entirely instead of emitting
 * an empty summary.
 */
function renderSummaryDoc(
  description: string | undefined,
  indentLevel: number,
): string | null {
  if (!description) return null;
  const prefix = "    ".repeat(indentLevel);
  return `${prefix}/// <summary>${escapeXmlDocText(description)}</summary>`;
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

/**
 * Returns the source for `IncomingEventDispatcher.g.cs` — a sealed helper
 * class with one `internal event System.Action<T>` per incoming wire payload
 * plus an `OnUnhandled` fallback, and an `internal void Dispatch(IncomingSocketEvent)`
 * switch that fans an instance into the matching event. `Conversation` owns
 * one as a field, subscribes internally, and re-raises into its args-typed
 * user-facing surface. The wire types stay out of `Conversation`'s public DX.
 */
function emitIncomingDispatcher(payloads: Payload[]): string {
  const eventDecls = payloads.map((p) => {
    const summary = renderSummaryDoc(p.description, 2);
    const decl = `        internal event System.Action<${p.name}>? On${p.name};`;
    return summary ? `${summary}\n${decl}` : decl;
  });
  const cases = payloads.map(
    ({ name }) =>
      `                case ${name} e:\n                    On${name}?.Invoke(e);\n                    break;`,
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
    "    /// <summary>",
    '    /// Internal fan-out from a parsed <see cref="IncomingSocketEvent"/> to one',
    '    /// strongly-typed event per wire payload. <see cref="ElevenLabs.Agents.Conversation"/>',
    "    /// owns one as a private field, subscribes to these events in its",
    "    /// constructor, and translates each wire payload into its args-typed",
    "    /// user-facing event surface.",
    "    /// </summary>",
    "    public sealed class IncomingEventDispatcher",
    "    {",
    eventDecls.join("\n\n"),
    "",
    "        /// <summary>",
    "        /// Wire types the server may add ahead of an SDK refresh land here",
    "        /// (mirrors <c>@elevenlabs/client</c> <c>BaseConversation</c>'s",
    "        /// <c>onDebug</c> arm).",
    "        /// </summary>",
    "        internal event System.Action<IncomingSocketEvent>? OnUnhandled;",
    "",
    "        internal void Dispatch(IncomingSocketEvent evt)",
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

// ---- Polymorphic JSON converter for IncomingSocketEvent ----

/**
 * Returns the source for `IncomingSocketEventConverter.g.cs` — a Newtonsoft
 * `JsonConverter<IncomingSocketEvent>` that reads the wire-level `type`
 * discriminator and instantiates the matching concrete payload class. Unknown
 * discriminators fall through to `UnknownIncomingEvent` so the dispatcher's
 * `OnUnhandled` arm still fires for server-added types ahead of an SDK refresh.
 * Serialisation is not supported — `CanWrite` returns `false`; outgoing events
 * serialise through their own `[JsonProperty("type")]` initialisers.
 */
function emitIncomingSocketEventConverter(payloads: Payload[]): string {
  const cases = payloads
    .filter((p) => p.typeDiscriminator !== undefined)
    .map(
      (p) =>
        `                "${p.typeDiscriminator}" => obj.ToObject<${p.name}>(serializer),`,
    );
  return [
    "// <auto-generated />",
    "// Source: Codegen~/schemas/convai-asyncapi.yml",
    "// Generator: pnpm --dir Codegen~ run generate",
    "",
    "#nullable enable",
    "",
    "using System;",
    "using Newtonsoft.Json;",
    "using Newtonsoft.Json.Linq;",
    "",
    "namespace ElevenLabs.Protocol",
    "{",
    "    /// <summary>",
    '    /// Polymorphic deserialiser for <see cref="IncomingSocketEvent"/>. Reads',
    "    /// the wire-level <c>type</c> discriminator and instantiates the matching",
    '    /// concrete payload class. Unknown discriminators fall through to <see cref="UnknownIncomingEvent"/>',
    '    /// so <see cref="IncomingEventDispatcher.OnUnhandled"/> still fires for',
    "    /// server-added types ahead of an SDK refresh.",
    "    /// </summary>",
    "    /// <remarks>",
    "    /// Read-only — <c>CanWrite</c> returns <c>false</c>. Outgoing events serialise",
    '    /// through their own <c>[JsonProperty("type")]</c> initialisers on the',
    '    /// <see cref="OutgoingSocketEvent"/> subclasses.',
    "    /// </remarks>",
    "    public sealed class IncomingSocketEventConverter : JsonConverter",
    "    {",
    "        public override bool CanWrite => false;",
    "",
    "        // Exact-match the abstract base only — never the concrete subtypes.",
    "        // The non-generic JsonConverter base lets us write this; the",
    "        // generic JsonConverter<T> base seals CanConvert to",
    "        // `typeof(T).IsAssignableFrom(objectType)`, which would match every",
    "        // subtype and re-enter this converter via the obj.ToObject<TConcrete>",
    "        // calls below, recursing until the stack overflows (silent Unity",
    "        // crash, no diagnostic).",
    "        public override bool CanConvert(Type objectType)",
    "        {",
    "            return objectType == typeof(IncomingSocketEvent);",
    "        }",
    "",
    "        public override object? ReadJson(",
    "            JsonReader reader,",
    "            Type objectType,",
    "            object? existingValue,",
    "            JsonSerializer serializer",
    "        )",
    "        {",
    "            JObject obj = JObject.Load(reader);",
    '            string? type = obj.Value<string>("type");',
    "            return type switch",
    "            {",
    cases.join("\n"),
    "                _ => new UnknownIncomingEvent(type, obj.ToString(Formatting.None)),",
    "            };",
    "        }",
    "",
    "        public override void WriteJson(",
    "            JsonWriter writer,",
    "            object? value,",
    "            JsonSerializer serializer",
    "        )",
    "        {",
    "            throw new NotSupportedException(",
    '                "IncomingSocketEventConverter is read-only. Outgoing events serialise via OutgoingSocketEvent subclasses\' own [JsonProperty(\\"type\\")] initialisers."',
    "            );",
    "        }",
    "    }",
    "}",
    "",
  ].join("\n");
}

const converterPath = resolve(protocolDir, "IncomingSocketEventConverter.g.cs");
writeFileSync(converterPath, emitIncomingSocketEventConverter(incoming));
console.log(`Wrote ${converterPath}`);

// ---- Per-event args records (sibling of the DTO + dispatcher files) ----

interface ArgsField {
  name: string;
  csType: string;
}

interface ArgsRecord {
  wireType: string;
  recordName: string;
  fields: ArgsField[];
  /**
   * AsyncAPI message description, used as the record's <summary>. Undefined
   * when the spec leaves it bare.
   */
  description?: string;
  /**
   * C# property name of the inner wrapper on the wire DTO, e.g.
   * "AgentResponseEvent" for AgentResponse. Undefined when not flattened.
   */
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
  // Sanitize first so the args records match what the wire-DTO renderer
  // emits — Modelina's `dynamic` becomes `JToken` (see protocol-preset
  // for the Unity / Mono rationale). Without this, the args record types
  // would diverge from the underlying wire types and the `ToArgs(...)`
  // extension methods wouldn't typecheck.
  const baseType = sanitizeCSType(p.property.type);
  if (!p.required && !baseType.endsWith("?") && !isPrimitive(baseType)) {
    return `${baseType}?`;
  }
  return baseType;
}

function buildArgsRecord(
  wireModel: OutputModel,
  description: string | undefined,
): ArgsRecord | null {
  const obj = wireModel.model;
  if (!(obj instanceof ConstrainedObjectModel)) return null;
  const wireType = obj.name;

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
          description,
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
  return {
    wireType,
    recordName: `${wireType}Args`,
    fields,
    description,
  };
}

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

/**
 * Returns the source for `IncomingEventArgs.g.cs` — one flat
 * `record {WireType}Args` per incoming wire payload, plus an
 * `IncomingEventArgsExtensions.ToArgs(this WireType e)` extension per record.
 * `results` is the Modelina output from `generateForPayloads(incoming, …)`,
 * read so the args records reuse the wire DTOs' property names and types.
 */
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
  for (const { name, description } of payloads) {
    const wireModel = modelByName.get(name);
    if (!wireModel) continue;
    const rec = buildArgsRecord(wireModel, description);
    if (rec) records.push(rec);
  }

  const recordDecls = records.map((rec) => {
    const params = rec.fields.map((f) => `${f.csType} ${f.name}`).join(", ");
    const summary = renderSummaryDoc(rec.description, 1);
    const decl = `    public record ${rec.recordName}(${params});`;
    return summary ? `${summary}\n${decl}` : decl;
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
    "using System.Collections.Generic;",
    // Unconditional — sibling DTO file does the same; some args records
    // forward `JToken` fields from the wire DTOs (see sanitizeCSType).
    "using Newtonsoft.Json.Linq;",
    "",
    "namespace ElevenLabs.Protocol",
    "{",
    recordDecls.join("\n\n"),
    "",
    "    /// <summary>",
    "    /// <c>ToArgs</c> extensions that translate each wire-typed",
    '    /// <see cref="IncomingSocketEvent"/> into the flat args record exposed on',
    '    /// the <see cref="ElevenLabs.Agents.Conversation"/> user-facing event surface.',
    "    /// </summary>",
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

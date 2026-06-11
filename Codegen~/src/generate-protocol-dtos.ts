import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { writeFileSync, mkdirSync } from "node:fs";
import { Parser, fromFile } from "@asyncapi/parser";

const here = dirname(fileURLToPath(import.meta.url));
const specPath = resolve(here, "../schemas/convai-asyncapi.yml");
const outputDir = resolve(here, "../../Runtime/Core/Protocol");

// ---- Local types for traversing the parser-resolved spec ----
// document.json() returns the spec with all $refs inlined by @asyncapi/parser.

interface RawSchema {
  type?: string | string[];
  properties?: Record<string, RawSchema>;
  required?: string[];
  enum?: unknown[];
  items?: RawSchema;
  additionalProperties?: boolean | RawSchema;
  anyOf?: RawSchema[];
  $ref?: string; // may remain for circular references
  nullable?: boolean;
  const?: unknown;
  "x-fern-type"?: string;
  "x-parser-schema-id"?: string;
}

interface RawMessage {
  name?: string;
  payload?: RawSchema;
}

interface RawSpec {
  channels: Record<
    string,
    {
      publish?: { message: { oneOf?: RawMessage[] } | RawMessage };
      subscribe?: { message: { oneOf?: RawMessage[] } | RawMessage };
    }
  >;
}

// ---- Helpers ----

function toPascalCase(snakeCase: string): string {
  return snakeCase
    .split("_")
    .map((s) => s.charAt(0).toUpperCase() + s.slice(1))
    .join("");
}

// C# forbids a property with the same name as its containing class.
function csPropName(propName: string, className: string): string {
  const pascal = toPascalCase(propName);
  return pascal === className ? `${pascal}Data` : pascal;
}

function getLiteralValue(xFernType: string): string | null {
  const m = xFernType.match(/^literal<"(.+)">$/);
  return m ? m[1] : null;
}

function getMessagePairs(
  spec: RawSpec,
  direction: "publish" | "subscribe",
): Array<{ name: string; schema: RawSchema }> {
  const channel = Object.values(spec.channels)[0];
  const op = channel?.[direction];
  if (!op?.message) return [];

  const messages: RawMessage[] =
    "oneOf" in op.message
      ? (op.message.oneOf ?? [])
      : [op.message as RawMessage];

  return messages.flatMap((msg) => {
    const schema = msg.payload as RawSchema | undefined;
    if (!schema) return [];
    const schemaId = schema["x-parser-schema-id"];
    const name =
      schemaId && !schemaId.startsWith("<") ? schemaId : (msg.name ?? "");
    if (!name) return [];
    return [{ name, schema }];
  });
}

// ---- C# type resolution ----

interface TypeResult {
  csType: string;
  /** Nested class declarations to splice into the parent class body. */
  extraDecls: string[];
}

// Returns a safe nested class name, avoiding collision with the parent class name.
// (C# forbids a nested type with the same name as the enclosing type.)
function nestedClassName(propName: string, parentClassName: string): string {
  const pascal = toPascalCase(propName);
  return pascal === parentClassName ? `${pascal}Payload` : pascal;
}

function resolveItemType(
  propName: string,
  items: RawSchema | undefined,
  nestLevel: number,
  parentClassName: string,
): TypeResult {
  if (!items) return { csType: "object", extraDecls: [] };
  // $ref remaining after inlining = circular ref; treat as object
  if (items.$ref) return { csType: "object", extraDecls: [] };
  if (items.anyOf) return { csType: "object", extraDecls: [] };
  const t = Array.isArray(items.type) ? items.type[0] : items.type;
  if (t === "string") return { csType: "string", extraDecls: [] };
  if (t === "integer") return { csType: "int", extraDecls: [] };
  if (t === "number") return { csType: "float", extraDecls: [] };
  if (t === "boolean") return { csType: "bool", extraDecls: [] };
  if (t === "object" && items.properties) {
    const itemClass = `${nestedClassName(propName, parentClassName)}Item`;
    const decl = emitClass(itemClass, items, null, nestLevel);
    return { csType: itemClass, extraDecls: [decl] };
  }
  return { csType: "object", extraDecls: [] };
}

function resolveType(
  propName: string,
  schema: RawSchema,
  isRequired: boolean,
  nestLevel: number,
  parentClassName: string,
): TypeResult {
  const q = !isRequired || schema.nullable ? "?" : "";

  // $ref remaining after inlining = circular ref; treat as object
  if (schema.$ref) return { csType: `object${q}`, extraDecls: [] };
  if (schema.anyOf) return { csType: `object${q}`, extraDecls: [] };
  if (schema.const !== undefined)
    return { csType: `string${q}`, extraDecls: [] };

  const t = Array.isArray(schema.type)
    ? schema.type[0]
    : (schema.type ?? "object");

  if (t === "string") return { csType: `string${q}`, extraDecls: [] };
  if (t === "integer") return { csType: `int${q}`, extraDecls: [] };
  if (t === "number") return { csType: `float${q}`, extraDecls: [] };
  if (t === "boolean") return { csType: `bool${q}`, extraDecls: [] };

  if (t === "array") {
    const itemResult = resolveItemType(
      propName,
      schema.items,
      nestLevel,
      parentClassName,
    );
    return {
      csType: `List<${itemResult.csType}>${q}`,
      extraDecls: itemResult.extraDecls,
    };
  }

  if (t === "object") {
    if (
      schema.additionalProperties === true ||
      (schema.additionalProperties &&
        typeof schema.additionalProperties === "object")
    ) {
      return { csType: `Dictionary<string, object>${q}`, extraDecls: [] };
    }
    if (schema.properties) {
      const nested = nestedClassName(propName, parentClassName);
      const decl = emitClass(nested, schema, null, nestLevel);
      return { csType: `${nested}${q}`, extraDecls: [decl] };
    }
    return { csType: `object${q}`, extraDecls: [] };
  }

  return { csType: `object${q}`, extraDecls: [] };
}

function csDefault(csType: string, isRequired: boolean): string {
  if (!isRequired) return "";
  const base = csType.endsWith("?") ? csType.slice(0, -1) : csType;
  if (base === "string") return ' = ""';
  if (base === "int" || base === "float") return " = 0";
  if (base === "bool") return " = false";
  if (base.startsWith("List<")) return " = new()";
  if (base.startsWith("Dictionary<")) return " = new()";
  return " = null!"; // required reference type
}

// ---- Class emitter ----

function emitClass(
  className: string,
  schema: RawSchema,
  baseClass: string | null,
  indentLevel: number,
  direction?: "incoming" | "outgoing",
): string {
  const i = (n: number) => "    ".repeat(n);
  const lines: string[] = [];
  const deferred: string[] = [];

  lines.push(
    `${i(indentLevel)}public class ${className}${baseClass ? ` : ${baseClass}` : ""}`,
  );
  lines.push(`${i(indentLevel)}{`);

  const props = schema.properties ?? {};
  const requiredSet = new Set(schema.required ?? []);

  for (const [propName, propSchema] of Object.entries(props)) {
    const isRequired = requiredSet.has(propName);
    const fernType = propSchema["x-fern-type"] as string | undefined;

    if (fernType) {
      const literal = getLiteralValue(fernType);
      if (literal) {
        if (direction === "incoming") continue; // Type is inherited from the base class
        lines.push(`${i(indentLevel + 1)}[JsonPropertyName("${propName}")]`);
        lines.push(
          `${i(indentLevel + 1)}public string ${csPropName(propName, className)} { get; init; } = "${literal}";`,
        );
        lines.push("");
        continue;
      }
    }

    const result = resolveType(
      propName,
      propSchema,
      isRequired,
      indentLevel + 1,
      className,
    );
    const def = csDefault(result.csType, isRequired);
    lines.push(`${i(indentLevel + 1)}[JsonPropertyName("${propName}")]`);
    lines.push(
      `${i(indentLevel + 1)}public ${result.csType} ${csPropName(propName, className)} { get; set; }${def};`,
    );
    lines.push("");
    deferred.push(...result.extraDecls);
  }

  while (lines.at(-1) === "") lines.pop();

  for (const decl of deferred) {
    lines.push("");
    lines.push(decl);
  }

  lines.push(`${i(indentLevel)}}`);
  return lines.join("\n");
}

// ---- File emitter ----

function emitFile(
  baseClass: string,
  pairs: Array<{ name: string; schema: RawSchema }>,
  direction: "incoming" | "outgoing",
): string {
  const parts: string[] = [
    "// <auto-generated />",
    "// Source: Codegen~/schemas/convai-asyncapi.yml",
    "// Generator: pnpm --dir Codegen~ run generate",
    "",
    "#nullable enable",
    "",
    "using System.Collections.Generic;",
    "using System.Text.Json.Serialization;",
    "",
    "namespace ElevenLabs.Protocol",
    "{",
    `    public abstract class ${baseClass}`,
    "    {",
  ];

  if (direction === "incoming") {
    parts.push(`        [JsonPropertyName("type")]`);
    parts.push(`        public string Type { get; set; } = "";`);
  }

  parts.push("    }");

  for (const { name, schema } of pairs) {
    parts.push("");
    parts.push(emitClass(name, schema, baseClass, 1, direction));
  }

  parts.push("}");
  return parts.join("\n") + "\n";
}

// ---- Main ----

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

const rawSpec = document.json() as unknown as RawSpec;

const incomingPairs = getMessagePairs(rawSpec, "subscribe");
const outgoingPairs = getMessagePairs(rawSpec, "publish");

console.log(
  `Incoming (${incomingPairs.length}): ${incomingPairs.map((p) => p.name).join(", ")}`,
);
console.log(
  `Outgoing (${outgoingPairs.length}): ${outgoingPairs.map((p) => p.name).join(", ")}`,
);

mkdirSync(outputDir, { recursive: true });

const incomingPath = resolve(outputDir, "IncomingSocketEvent.cs");
writeFileSync(
  incomingPath,
  emitFile("IncomingSocketEvent", incomingPairs, "incoming"),
);
console.log(`Wrote ${incomingPath}`);

const outgoingPath = resolve(outputDir, "OutgoingSocketEvent.cs");
writeFileSync(
  outgoingPath,
  emitFile("OutgoingSocketEvent", outgoingPairs, "outgoing"),
);
console.log(`Wrote ${outgoingPath}`);

const info = document.info();
console.log(`Done — ${info.title()} v${info.version()}`);

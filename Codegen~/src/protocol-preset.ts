// Modelina preset that emits our Convai DTOs (System.Text.Json attributes,
// init-only string literals for const properties, top-level event types
// extended from a shared discriminated-union base class).
//
// Extracted from generate-protocol-dtos.ts so the property/class renderers
// and their helpers can be unit-tested without booting the full generator
// pipeline. See protocol-preset.test.ts for branch coverage.

import {
  type CSharpPreset,
  type ConstrainedObjectPropertyModel,
} from "@asyncapi/modelina";

// ---- Type helpers ----

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

export function isPrimitive(csType: string): boolean {
  return PRIMITIVE_TYPES.has(csType.replace(/\?$/, ""));
}

export function isCollection(csType: string): boolean {
  return csType.startsWith("List<") || csType.startsWith("Dictionary<");
}

export function toPascalCase(s: string): string {
  return s
    .split(/[_\s-]+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join("");
}

// ---- C# literal helpers ----

// Escape a string for embedding inside a regular (non-verbatim) C# string
// literal. Backslash MUST be escaped first; reversing the order would
// double-escape the backslash added by the quote substitution.
export function escapeCSharpString(s: string): string {
  return s.replace(/\\/g, "\\\\").replace(/"/g, '\\"');
}

// Modelina stores `const` values as JSON-encoded tokens (e.g. `'"foo"'` for the
// string "foo"). Our spec only emits string consts (via Fern's
// `x-fern-type: literal<"...">` rewrite); a non-string const reaching this
// point means the spec or the codegen grew a case we never designed for, so
// fail loudly rather than silently emit `public string Name = "true";`.
export function unwrapStringConst(raw: unknown): string {
  if (typeof raw !== "string" || !raw.startsWith('"') || !raw.endsWith('"')) {
    throw new Error(
      `Unsupported const value shape (only quoted string consts are supported): ${JSON.stringify(raw)}`,
    );
  }
  const parsed: unknown = JSON.parse(raw);
  if (typeof parsed !== "string") {
    throw new Error(
      `Expected string const, decoded ${typeof parsed}: ${JSON.stringify(parsed)}`,
    );
  }
  return parsed;
}

// Modelina (with `handleNullable: true`) already appends `?` to optional
// primitives before this preset sees the property, so we only suffix
// reference types and collections here.
export function pickNullabilitySuffix({
  csType,
  isRequired,
}: {
  csType: string;
  isRequired: boolean;
}): string {
  if (isRequired) return "";
  if (csType.endsWith("?")) return "";
  if (isPrimitive(csType)) return "";
  return "?";
}

// Required reference types get `= null!` to silence CS8618; required primitives
// get their natural zero value. Anything we don't recognize returns "" and the
// caller emits a bare auto-property — safer than guessing.
export function pickRequiredInitializer(csType: string): string {
  if (csType === "string") return ' = ""';
  if (csType === "int" || csType === "long") return " = 0";
  if (csType === "float" || csType === "double") return " = 0";
  if (csType === "bool") return " = false";
  if (isCollection(csType)) return " = new()";
  if (!isPrimitive(csType)) return " = null!";
  return "";
}

// ---- Renderers ----

export function renderProtocolProperty({
  property,
  enclosingName,
}: {
  property: ConstrainedObjectPropertyModel;
  enclosingName: string;
}): string {
  const jsonName = property.unconstrainedPropertyName;
  // C# CS0542: a property cannot share its enclosing type's name.
  const pascal = toPascalCase(jsonName);
  const propName = pascal === enclosingName ? `${pascal}Data` : pascal;

  const csType = property.property.type;
  const isRequired = property.required;
  const attr = `[JsonPropertyName("${jsonName}")]`;

  const constOpt = property.property.options.const;
  if (constOpt && constOpt.value !== undefined) {
    const literal = escapeCSharpString(unwrapStringConst(constOpt.value));
    return `${attr}\npublic string ${propName} { get; init; } = "${literal}";`;
  }

  const renderedType = `${csType}${pickNullabilitySuffix({ csType, isRequired })}`;
  const initializer = isRequired ? pickRequiredInitializer(csType) : "";
  const semicolon = initializer ? ";" : "";
  return `${attr}\npublic ${renderedType} ${propName} { get; set; }${initializer}${semicolon}`;
}

export function renderProtocolClassHeader({
  content,
  modelName,
  baseClass,
  isTopLevel,
}: {
  content: string;
  modelName: string;
  baseClass: string;
  isTopLevel: boolean;
}): string {
  const suffix = isTopLevel ? ` : ${baseClass}` : "";
  const pattern = new RegExp(`public partial class ${modelName}\\b`);
  if (!pattern.test(content)) {
    throw new Error(
      `Expected \`public partial class ${modelName}\` in Modelina output but did not find it — Modelina template may have changed.`,
    );
  }
  return content.replace(pattern, `public class ${modelName}${suffix}`);
}

// ---- Preset ----

// Modelina's default class renderer emits `public T name { get; set; }`
// without JSON attributes; the bundled JsonSerializerPreset emits per-class
// JsonConverter<T> classes, which is more invasive than we want.
// The property hook emits [JsonPropertyName] + a standard auto-property,
// with init-only literal defaults for `const` properties. The class hook
// strips Modelina's default `partial` and, for top-level event types,
// injects the discriminated-union base class.
export function makeProtocolPreset(
  baseClass: string,
  topLevelNames: Set<string>,
): CSharpPreset {
  return {
    class: {
      self({ renderer, model, content }) {
        renderer.dependencyManager.addDependency(
          "using System.Text.Json.Serialization;",
        );
        return renderProtocolClassHeader({
          content,
          modelName: model.name,
          baseClass,
          isTopLevel: topLevelNames.has(model.name),
        });
      },
      property({ renderer, property }) {
        // `renderer.model` is protected, but it's the ConstrainedObjectModel
        // for the current class; cast to read `.name` from the preset.
        const enclosingName = (
          renderer as unknown as { model: { name: string } }
        ).model.name;
        return renderProtocolProperty({ property, enclosingName });
      },
    },
  };
}

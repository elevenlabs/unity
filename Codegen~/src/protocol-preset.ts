// Modelina preset that emits our Convai DTOs (Newtonsoft.Json attributes,
// init-only string literals for const properties, top-level event types
// extended from a shared discriminated-union base class).
//
// Newtonsoft.Json was chosen over System.Text.Json because Unity 6 LTS does
// not ship System.Text.Json in its scripting BCL; pulling it in via NuGet
// invites IL2CPP/AOT trip-hazards on WebGL. The Unity-blessed package
// `com.unity.nuget.newtonsoft-json` (v3.x, wrapping Newtonsoft.Json 13.0.1)
// supports init-only setters and ships an AOT-friendly build out of the box.
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

/**
 * Escapes backslashes and double-quotes so `s` is safe to embed inside a
 * regular (non-verbatim) C# string literal.
 */
export function escapeCSharpString(s: string): string {
  return s.replace(/\\/g, "\\\\").replace(/"/g, '\\"');
}

/**
 * Decodes Modelina's JSON-encoded `const` token (e.g. `'"foo"'`) back into
 * the underlying string. Throws when `raw` isn't a quoted string — only
 * string consts are supported (any other shape is a spec/codegen surprise we
 * want to surface, not silently emit as a bogus `public string Name = "true";`).
 */
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

/**
 * Returns the nullability suffix to render on a property: `?` for optional
 * reference types and collections, empty string otherwise. Optional primitives
 * return empty because Modelina (with `handleNullable: true`) has already
 * appended `?` to their `csType`.
 */
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

/**
 * Returns the property initializer to emit for a required property of `csType`:
 * `= null!` for reference types (silences CS8618), the natural zero literal for
 * known primitives, `= new()` for collections, empty string for anything
 * unrecognised (caller emits a bare auto-property — safer than guessing).
 */
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

/**
 * Renders a single C# property declaration: `[JsonProperty("...")]` attribute
 * + auto-property with the appropriate nullability suffix and required-property
 * initializer. `const` properties emit as `{ get; init; } = "literal";`.
 * Collides with `enclosingName`? Suffix the property name with `Data` (C# CS0542).
 */
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
  const attr = `[JsonProperty("${jsonName}")]`;

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

/**
 * Strips Modelina's default `partial` from the emitted class header and,
 * for top-level event types, appends `: baseClass`. Throws if Modelina's
 * output no longer matches the expected `public partial class {modelName}`
 * shape (which would mean its template changed).
 */
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
// JsonConverter<T> classes, which is more invasive than we want — so this
// preset wires its own property + class hooks instead.

/**
 * Builds the CSharp preset that customises Modelina's output for our protocol
 * DTOs: properties get `[JsonProperty]` + a standard auto-property (with
 * init-only literal defaults for `const` schemas); class headers drop
 * Modelina's default `partial` and, when the model name is in
 * `topLevelNames`, append `: baseClass` to inject the discriminated-union
 * base.
 */
export function makeProtocolPreset(
  baseClass: string,
  topLevelNames: Set<string>,
): CSharpPreset {
  return {
    class: {
      self({ renderer, model, content }) {
        renderer.dependencyManager.addDependency("using Newtonsoft.Json;");
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

// Unit tests for the protocol-preset module. Uses Node's built-in test runner
// (`node --test`) so the codegen pipeline keeps zero runtime dependencies.

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import {
  escapeCSharpString,
  isCollection,
  isPrimitive,
  pickNullabilitySuffix,
  pickRequiredInitializer,
  renderProtocolClassHeader,
  renderProtocolProperty,
  toPascalCase,
  unwrapStringConst,
} from "./protocol-preset.ts";

// The renderer reads a narrow subset of ConstrainedObjectPropertyModel — this
// helper builds the minimum object it needs without dragging in Modelina's
// constructors (which expect a full constrained model tree).
function mockProperty({
  jsonName,
  csType,
  required,
  constValue,
}: {
  jsonName: string;
  csType: string;
  required: boolean;
  constValue?: unknown;
}): Parameters<typeof renderProtocolProperty>[0]["property"] {
  return {
    unconstrainedPropertyName: jsonName,
    required,
    property: {
      type: csType,
      options: {
        const:
          constValue === undefined
            ? undefined
            : { value: constValue as string },
      },
    },
  } as unknown as Parameters<typeof renderProtocolProperty>[0]["property"];
}

describe("toPascalCase", () => {
  it("upper-cases a single word", () =>
    assert.equal(toPascalCase("foo"), "Foo"));
  it("splits on underscores", () =>
    assert.equal(toPascalCase("foo_bar_baz"), "FooBarBaz"));
  it("splits on hyphens and spaces", () =>
    assert.equal(toPascalCase("foo-bar baz"), "FooBarBaz"));
  it("returns empty for empty input", () => assert.equal(toPascalCase(""), ""));
});

describe("isPrimitive", () => {
  for (const t of [
    "string",
    "int",
    "long",
    "bool",
    "float",
    "double",
    "byte",
    "decimal",
    "char",
    "short",
  ]) {
    it(`recognizes ${t}`, () => assert.equal(isPrimitive(t), true));
  }
  it("strips trailing `?`", () => assert.equal(isPrimitive("int?"), true));
  it("rejects user types", () => assert.equal(isPrimitive("MyClass"), false));
  it("rejects collections", () =>
    assert.equal(isPrimitive("List<string>"), false));
});

describe("isCollection", () => {
  it("recognizes List<>", () => assert.equal(isCollection("List<int>"), true));
  it("recognizes Dictionary<>", () =>
    assert.equal(isCollection("Dictionary<string, int>"), true));
  it("rejects primitives", () => assert.equal(isCollection("string"), false));
  it("rejects references", () => assert.equal(isCollection("MyClass"), false));
});

describe("escapeCSharpString", () => {
  it("passes through plain ascii", () =>
    assert.equal(escapeCSharpString("foo bar"), "foo bar"));
  it("escapes quotes", () =>
    assert.equal(escapeCSharpString('he said "hi"'), 'he said \\"hi\\"'));
  it("escapes backslashes", () =>
    assert.equal(escapeCSharpString("a\\b"), "a\\\\b"));
  it("escapes backslash before quote so the substitution order does not double-escape", () =>
    // Input: `\"` (2 chars). Expected: `\\\"` (4 chars).
    assert.equal(escapeCSharpString('\\"'), '\\\\\\"'));
  it("returns empty for empty input", () =>
    assert.equal(escapeCSharpString(""), ""));
});

describe("unwrapStringConst", () => {
  it("unwraps simple quoted strings", () =>
    assert.equal(unwrapStringConst('"foo"'), "foo"));
  it("decodes embedded escape sequences", () =>
    assert.equal(unwrapStringConst('"a\\"b"'), 'a"b'));
  it("throws on non-string raw", () =>
    assert.throws(() => unwrapStringConst(42), /Unsupported const value/));
  it("throws on unquoted string", () =>
    assert.throws(() => unwrapStringConst("foo"), /Unsupported const value/));
  it("throws on null", () =>
    assert.throws(() => unwrapStringConst(null), /Unsupported const value/));
});

describe("pickNullabilitySuffix", () => {
  it("required primitive → empty", () =>
    assert.equal(
      pickNullabilitySuffix({ csType: "int", isRequired: true }),
      "",
    ));
  it("required reference → empty", () =>
    assert.equal(
      pickNullabilitySuffix({ csType: "Foo", isRequired: true }),
      "",
    ));
  it("optional primitive → empty (Modelina already appended `?`)", () =>
    assert.equal(
      pickNullabilitySuffix({ csType: "int", isRequired: false }),
      "",
    ));
  it("optional reference → `?`", () =>
    assert.equal(
      pickNullabilitySuffix({ csType: "Foo", isRequired: false }),
      "?",
    ));
  it("already-nullable type → empty (no double `?`)", () =>
    assert.equal(
      pickNullabilitySuffix({ csType: "Foo?", isRequired: false }),
      "",
    ));
  it("optional collection → `?`", () =>
    assert.equal(
      pickNullabilitySuffix({ csType: "List<int>", isRequired: false }),
      "?",
    ));
});

describe("pickRequiredInitializer", () => {
  it("string → empty string literal", () =>
    assert.equal(pickRequiredInitializer("string"), ' = ""'));
  it("int → 0", () => assert.equal(pickRequiredInitializer("int"), " = 0"));
  it("long → 0", () => assert.equal(pickRequiredInitializer("long"), " = 0"));
  it("float → 0", () => assert.equal(pickRequiredInitializer("float"), " = 0"));
  it("double → 0", () =>
    assert.equal(pickRequiredInitializer("double"), " = 0"));
  it("bool → false", () =>
    assert.equal(pickRequiredInitializer("bool"), " = false"));
  it("List<> → new()", () =>
    assert.equal(pickRequiredInitializer("List<int>"), " = new()"));
  it("Dictionary<> → new()", () =>
    assert.equal(
      pickRequiredInitializer("Dictionary<string, int>"),
      " = new()",
    ));
  it("reference type → `= null!`", () =>
    assert.equal(pickRequiredInitializer("MyClass"), " = null!"));
  it("uncovered primitive (byte) → empty (no zero literal)", () =>
    assert.equal(pickRequiredInitializer("byte"), ""));
});

describe("renderProtocolProperty", () => {
  it("emits required string with empty-string initializer", () => {
    const out = renderProtocolProperty({
      property: mockProperty({
        jsonName: "user_id",
        csType: "string",
        required: true,
      }),
      enclosingName: "Event",
    });
    assert.equal(
      out,
      `[JsonProperty("user_id")]\npublic string UserId { get; set; } = "";`,
    );
  });

  it("appends `?` for optional reference types without an initializer", () => {
    const out = renderProtocolProperty({
      property: mockProperty({
        jsonName: "extra",
        csType: "Foo",
        required: false,
      }),
      enclosingName: "Event",
    });
    assert.equal(
      out,
      `[JsonProperty("extra")]\npublic Foo? Extra { get; set; }`,
    );
  });

  it("emits `= null!` suppression for required reference types", () => {
    const out = renderProtocolProperty({
      property: mockProperty({
        jsonName: "inner",
        csType: "Foo",
        required: true,
      }),
      enclosingName: "Event",
    });
    assert.equal(
      out,
      `[JsonProperty("inner")]\npublic Foo Inner { get; set; } = null!;`,
    );
  });

  it("emits init-only string for a const literal", () => {
    const out = renderProtocolProperty({
      property: mockProperty({
        jsonName: "type",
        csType: "string",
        required: true,
        constValue: '"agent_response"',
      }),
      enclosingName: "AgentResponseEvent",
    });
    assert.equal(
      out,
      `[JsonProperty("type")]\npublic string Type { get; init; } = "agent_response";`,
    );
  });

  it("escapes embedded quotes in const literals (bug fix)", () => {
    // Raw Modelina token for the decoded string `agent_"weird"`.
    const out = renderProtocolProperty({
      property: mockProperty({
        jsonName: "type",
        csType: "string",
        required: true,
        constValue: '"agent_\\"weird\\""',
      }),
      enclosingName: "Event",
    });
    assert.equal(
      out,
      `[JsonProperty("type")]\npublic string Type { get; init; } = "agent_\\"weird\\"";`,
    );
  });

  it("throws on a non-string const value (bug fix)", () => {
    assert.throws(
      () =>
        renderProtocolProperty({
          property: mockProperty({
            jsonName: "code",
            csType: "string",
            required: true,
            // Raw value is a number, not a JSON-encoded string — would
            // silently produce `public string Code = "42";` previously.
            constValue: 42,
          }),
          enclosingName: "Event",
        }),
      /Unsupported const value/,
    );
  });

  it("renames properties that collide with the enclosing class to `${name}Data`", () => {
    const out = renderProtocolProperty({
      property: mockProperty({
        jsonName: "agent_response",
        csType: "Foo",
        required: true,
      }),
      enclosingName: "AgentResponse",
    });
    // toPascalCase("agent_response") === "AgentResponse" === enclosing.
    assert.match(out, /public Foo AgentResponseData \{/);
  });
});

describe("renderProtocolClassHeader", () => {
  it("strips `partial` and appends the base class for top-level types", () => {
    const out = renderProtocolClassHeader({
      content: "public partial class AgentResponse { }",
      modelName: "AgentResponse",
      baseClass: "IncomingSocketEvent",
      isTopLevel: true,
    });
    assert.equal(out, "public class AgentResponse : IncomingSocketEvent { }");
  });

  it("strips `partial` without a suffix for nested types", () => {
    const out = renderProtocolClassHeader({
      content: "public partial class NestedThing { }",
      modelName: "NestedThing",
      baseClass: "IncomingSocketEvent",
      isTopLevel: false,
    });
    assert.equal(out, "public class NestedThing { }");
  });

  it("throws if Modelina output is missing the expected declaration (hardening)", () => {
    assert.throws(
      () =>
        renderProtocolClassHeader({
          // `sealed` instead of `partial` — simulates a future Modelina change.
          content: "public sealed class AgentResponse { }",
          modelName: "AgentResponse",
          baseClass: "IncomingSocketEvent",
          isTopLevel: true,
        }),
      /Modelina template may have changed/,
    );
  });

  it("respects word boundaries (does not corrupt prefix-matching types)", () => {
    const out = renderProtocolClassHeader({
      content:
        "public partial class Agent { }\npublic partial class AgentResponse { }",
      modelName: "Agent",
      baseClass: "Base",
      isTopLevel: false,
    });
    // Only `Agent` should be transformed; `AgentResponse` left intact.
    assert.equal(
      out,
      "public class Agent { }\npublic partial class AgentResponse { }",
    );
  });
});

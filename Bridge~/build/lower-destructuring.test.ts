import { describe, expect, it } from "vitest";
import { parseAst } from "rolldown/parseAst";
import { transformDestructuring } from "./lower-destructuring.js";

type AstNode = {
  type: string;
  start: number;
  end: number;
  [key: string]: unknown;
};

function transform(code: string): string | null {
  return transformDestructuring(code, parseAst(code) as unknown as AstNode);
}

describe("lowerDestructuring — transformDestructuring", () => {
  it("lowers a renamed object-destructuring with multiple bindings", () => {
    // This is the actual production failure shape — see WebSocketConnection.create
    // in @elevenlabs/client and Docs~/unity-issues/emscripten-jsdce-destructuring.md.
    const code = `const { name: source, version } = sourceInfo;`;
    expect(transform(code)).toBe(
      `const source = sourceInfo.name, version = sourceInfo.version;`,
    );
  });

  it("lowers a shorthand object-destructuring", () => {
    const code = `const { x, y } = obj;`;
    expect(transform(code)).toBe(`const x = obj.x, y = obj.y;`);
  });

  it("lowers an array-destructuring", () => {
    const code = `const [a, b, c] = arr;`;
    expect(transform(code)).toBe(`const a = arr[0], b = arr[1], c = arr[2];`);
  });

  it("preserves holes in array-destructuring", () => {
    const code = `const [, x, , y] = arr;`;
    expect(transform(code)).toBe(`const x = arr[1], y = arr[3];`);
  });

  it("lowers let and var, not just const", () => {
    expect(transform(`let { x } = obj;`)).toBe(`let x = obj.x;`);
    expect(transform(`var { x } = obj;`)).toBe(`var x = obj.x;`);
  });

  it("quotes keys that aren't valid identifiers", () => {
    // Hyphenated property names need bracket access.
    const code = `const { "with-dash": a, "k2": b } = obj;`;
    expect(transform(code)).toBe(`const a = obj["with-dash"], b = obj.k2;`);
  });

  // ---------- skip cases: leave the source untouched -----------------------

  it("skips when the RHS is a call expression (terser sees side effects)", () => {
    const code = `const { x } = sideEffect();`;
    expect(transform(code)).toBeNull();
  });

  it("skips when the RHS is a member expression (avoid duplicated access)", () => {
    // We could safely lower `const { x } = obj.foo` to `const x = obj.foo.x`
    // because the chain has no observable side effects in practice, but a
    // multi-binding rewrite (`const x = obj.foo.x, y = obj.foo.y`) would walk
    // the chain twice. Easier to be conservative — the production sites we
    // care about (sourceInfo destructurings) all have Identifier RHS.
    const code = `const { x } = obj.foo;`;
    expect(transform(code)).toBeNull();
  });

  it("skips object-destructuring with default values", () => {
    const code = `const { x = 5 } = obj;`;
    expect(transform(code)).toBeNull();
  });

  it("skips object-destructuring with rest", () => {
    const code = `const { x, ...rest } = obj;`;
    expect(transform(code)).toBeNull();
  });

  it("skips object-destructuring with computed keys", () => {
    const code = `const { [k]: v } = obj;`;
    expect(transform(code)).toBeNull();
  });

  it("skips object-destructuring with nested patterns", () => {
    const code = `const { a: { b } } = obj;`;
    expect(transform(code)).toBeNull();
  });

  it("skips array-destructuring with rest", () => {
    const code = `const [first, ...rest] = arr;`;
    expect(transform(code)).toBeNull();
  });

  it("skips array-destructuring with defaults", () => {
    const code = `const [a = 1] = arr;`;
    expect(transform(code)).toBeNull();
  });

  it("skips multi-declarator `const` (rare and not worth the assembly cost)", () => {
    const code = `const { x } = a, { y } = b;`;
    expect(transform(code)).toBeNull();
  });

  it("skips `for ({a} of arr)` and `for ([a] of arr)` (init is null)", () => {
    expect(transform(`for (const { x } of arr) { void x; }`)).toBeNull();
    expect(transform(`for (const [x] of arr) { void x; }`)).toBeNull();
  });

  it("leaves plain identifier declarations untouched", () => {
    const code = `const x = obj.a;`;
    expect(transform(code)).toBeNull();
  });

  it("returns null when no destructuring is present", () => {
    expect(transform(`const x = 1 + 2; function f() { return x; }`)).toBeNull();
  });

  it("rewrites multiple destructurings in one pass, right-to-left safe", () => {
    const code = [
      `const { a } = first;`,
      `function g(){ const [x, y] = arr; return x + y; }`,
      `const { p: q, r } = second;`,
    ].join("\n");
    const result = transform(code);
    expect(result).not.toBeNull();
    expect(result).toContain(`const a = first.a;`);
    expect(result).toContain(`const x = arr[0], y = arr[1];`);
    expect(result).toContain(`const q = second.p, r = second.r;`);
  });
});

import { describe, expect, it } from "vitest";
import { parseAst } from "rolldown/parseAst";
import { transformDyncalls } from "./substitute-make-dyncall.js";

type AstNode = {
  type: string;
  start: number;
  end: number;
  [key: string]: unknown;
};

function transform(code: string): string | null {
  if (!code.includes("dynCall_")) return null;
  return transformDyncalls(code, parseAst(code) as unknown as AstNode);
}

describe("substituteMakeDyncall — transformDyncalls", () => {
  it("rewrites dynCall_viii with plain int args", () => {
    const code = `dynCall_viii(EL_SettlePtr, promiseId, statusCode, payloadPtr);`;
    const result = transform(code);
    expect(result).toContain(
      `{{{ makeDynCall('viii', 'EL_SettlePtr') }}}(promiseId, statusCode, payloadPtr)`,
    );
  });

  it("rewrites dynCall_vii with a nested JSON.stringify argument", () => {
    const code = `dynCall_vii(EL_CallbackPtr, handle, JSON.stringify(arg));`;
    const result = transform(code);
    expect(result).toContain(
      `{{{ makeDynCall('vii', 'EL_CallbackPtr') }}}(handle, JSON.stringify(arg))`,
    );
  });

  it("rewrites a multi-line call preserving all arguments verbatim", () => {
    const code = [
      "dynCall_viii(",
      "  EL_SettlePtr,",
      "  promiseId,",
      "  statusCode,",
      "  payloadPtr",
      ");",
    ].join("\n");
    const result = transform(code);
    expect(result).not.toBeNull();
    expect(result).toContain(`{{{ makeDynCall('viii', 'EL_SettlePtr') }}}`);
    expect(result).toContain("promiseId");
    expect(result).toContain("statusCode");
    expect(result).toContain("payloadPtr");
  });

  it("rewrites a zero-rest-args call (function pointer is the only argument)", () => {
    const code = `dynCall_v(EL_SomePtr);`;
    const result = transform(code);
    expect(result).toBe(`{{{ makeDynCall('v', 'EL_SomePtr') }}}();`);
  });

  it("leaves the call unchanged when the first arg is not an EL_ identifier", () => {
    const code = `dynCall_viii(someOtherVar, a, b, c);`;
    const result = transform(code);
    expect(result).toBeNull();
  });

  it("leaves non-dynCall functions unchanged", () => {
    const code = `someFunction(EL_Ptr, a, b);`;
    const result = transform(code);
    expect(result).toBeNull();
  });

  it("returns null when no dynCall_ call is present", () => {
    const code = `const x = 1 + 2;`;
    const result = transform(code);
    expect(result).toBeNull();
  });

  it("rewrites multiple dynCall sites in one pass", () => {
    const code = [
      "dynCall_viii(EL_SettlePtr, a, b, c);",
      "dynCall_vii(EL_CallbackPtr, x, y);",
    ].join("\n");
    const result = transform(code);
    expect(result).not.toBeNull();
    expect(result).toContain(`{{{ makeDynCall('viii', 'EL_SettlePtr') }}}`);
    expect(result).toContain(`{{{ makeDynCall('vii', 'EL_CallbackPtr') }}}`);
  });
});

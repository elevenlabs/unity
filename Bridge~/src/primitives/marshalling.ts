// Argument rehydration and return encoding for the bridge wire protocol.
//
// Rehydration converts a JSON-decoded value into the live JS value it
// represents. Three marker shapes are recognised:
//
//   { $ref: handle } → object from EL_Objects registry
//   { $fn: handle }  → function from EL_Functions registry
//   { $cb: handle }  → closure that fires EL_InvokeCallback(handle, payload)
//
// Plain objects and arrays are walked recursively. All other values pass through.
//
// Return encoding converts a JS return value to a JSON-serialisable shape for
// the C# decoder. The returnShape hint (passed per call by the dispatcher) tells
// it whether to treat the return as a handle, a function ref, void, or a plain
// JSON value.
//
// Recursion uses self-reference (EL_Rehydrate inside its own body), not a
// module-private helper, because Emscripten's library loader extracts each
// `$EL_*` entry separately and module-private helpers do not survive. The
// self-reference resolves at runtime to the hoisted EL_Rehydrate global.

import type { ReturnShape } from "./registries";

export const $EL_Rehydrate__deps = [
  "$EL_Objects",
  "$EL_Functions",
  "$EL_InvokeCallback",
];
export function $EL_Rehydrate(value: unknown): unknown {
  if (value === null || typeof value !== "object") return value;
  const obj = value as Record<string, unknown>;
  if ("$ref" in obj) return EL_Objects[obj.$ref as number];
  if ("$fn" in obj) return EL_Functions[obj.$fn as number];
  if ("$cb" in obj) {
    const handle = obj.$cb as number;
    return (arg: unknown) => EL_InvokeCallback(handle, JSON.stringify(arg));
  }
  if (Array.isArray(value)) return value.map((v) => EL_Rehydrate(v));
  return Object.fromEntries(
    Object.entries(obj).map(([k, v]) => [k, EL_Rehydrate(v)]),
  );
}

export const $EL_EncodeReturn__deps = [
  "$EL_AllocateObject",
  "$EL_AllocateFunction",
];
export function $EL_EncodeReturn(value: unknown, shape: ReturnShape): unknown {
  if (shape === "object") return { $ref: EL_AllocateObject(value) };
  if (shape === "function")
    return {
      $fn: EL_AllocateFunction(value as (...args: unknown[]) => unknown),
    };
  if (shape === "void") return null;
  return value;
}

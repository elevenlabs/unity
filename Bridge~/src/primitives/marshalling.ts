// Argument rehydration and return encoding for the bridge wire protocol.
//
// Rehydration converts a JSON-decoded value into the live JS value it
// represents. Three marker shapes are recognised:
//
//   { $ref: handle } → object from _EL_Objects registry
//   { $fn: handle }  → function from _EL_Functions registry
//   { $cb: handle }  → closure that fires _EL_InvokeCallback(handle, payload)
//
// Plain objects and arrays are walked recursively. All other values pass through.
//
// Return encoding converts a JS return value to a JSON-serialisable shape for
// the C# decoder. The returnShape hint (registered per factory/method) tells the
// dispatcher whether to treat the return as a handle, a function ref, void, or
// a plain JSON value.

import type { ReturnShape } from "./registries";

// Private recursive helper so the exported wrapper can reference _EL_* globals
// while recursion stays local (avoids the global-self-reference pattern).
function rehydrateImpl(value: unknown): unknown {
  if (value === null || typeof value !== "object") return value;
  const obj = value as Record<string, unknown>;
  if ("$ref" in obj) return _EL_Objects[obj.$ref as number];
  if ("$fn" in obj) return _EL_Functions[obj.$fn as number];
  if ("$cb" in obj) {
    const handle = obj.$cb as number;
    return (arg: unknown) => _EL_InvokeCallback(handle, JSON.stringify(arg));
  }
  if (Array.isArray(value)) return value.map(rehydrateImpl);
  return Object.fromEntries(
    Object.entries(obj).map(([k, v]) => [k, rehydrateImpl(v)]),
  );
}

export const $EL_Rehydrate__deps = [
  "$EL_Objects",
  "$EL_Functions",
  "$EL_InvokeCallback",
];
export function $EL_Rehydrate(value: unknown): unknown {
  return rehydrateImpl(value);
}

export const $EL_EncodeReturn__deps = [
  "$EL_AllocateObject",
  "$EL_AllocateFunction",
];
export function $EL_EncodeReturn(value: unknown, shape: ReturnShape): unknown {
  if (shape === "object") return { $ref: _EL_AllocateObject(value) };
  if (shape === "function")
    return {
      $fn: _EL_AllocateFunction(value as (...args: unknown[]) => unknown),
    };
  if (shape === "void") return null;
  return value;
}

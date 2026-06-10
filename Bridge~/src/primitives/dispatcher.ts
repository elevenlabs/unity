// Dispatcher — the eight EL_* DllImport entry points.
//
// Each wraps its body in try/catch. Async entry points route errors through
// the settle channel ($EL_Settle); sync entry points return a sentinel-prefixed
// heap string ("!err:<message>"). Argument rehydration and return encoding are
// delegated to $EL_Rehydrate / $EL_EncodeReturn.
//
// Every call entry point takes a `returnShape` int parameter from C#. The C#
// generic `<T>` resolution maps directly to a numeric code via the table below,
// and this code is the single source of truth for how the JS return value is
// encoded back. No registration step is required on the JS side — the C#
// call site already knows the type it expects, so it just says so.

import type { ReturnShape } from "./registries";

// Numeric codes for ReturnShape, passed across the DllImport boundary.
// Must stay in sync with the C# BridgeReturnShape enum (Phase 3).
//   0 = value     (plain JSON round-trip; the default)
//   1 = object    (allocate a JsObject handle, return { $ref })
//   2 = function  (allocate a JsFunction handle, return { $fn })
//   3 = void      (discard the return, settle null)
const RETURN_SHAPES: ReturnShape[] = ["value", "object", "function", "void"];
function decodeReturnShape(code: number): ReturnShape {
  return RETURN_SHAPES[code] ?? "value";
}

// Allocates a UTF-8 string on the Emscripten heap and returns the pointer.
// The C# caller copies then frees the returned memory.
function allocString(s: string): number {
  const len = lengthBytesUTF8(s) + 1;
  const buf = _malloc(len);
  stringToUTF8(s, buf, len);
  return buf;
}

// Parses the args JSON pointer and returns an array of rehydrated values.
// A zero pointer is treated as an empty args list.
function parseArgs(argsJsonPtr: number): unknown[] {
  const json = argsJsonPtr ? UTF8ToString(argsJsonPtr) : "[]";
  return (JSON.parse(json || "[]") as unknown[]).map((v) => _EL_Rehydrate(v));
}

// Resolves resultOrPromise (which may be a live Promise), encodes the return
// value with the given shape, then settles the C# await via SendMessage.
function settleWith(
  promiseId: number,
  resultOrPromise: unknown,
  returnShape: ReturnShape,
): void {
  Promise.resolve(resultOrPromise)
    .then((result: unknown) => {
      const encoded = _EL_EncodeReturn(result, returnShape);
      _EL_Settle(promiseId, "ok", JSON.stringify(encoded) ?? "null");
    })
    .catch((e: unknown) => {
      _EL_Settle(promiseId, "err", e instanceof Error ? e.message : String(e));
    });
}

// --- Factory invocation (async) ---

export const EL_InvokeFactoryAsync__deps = [
  "$EL_LookupFactory",
  "$EL_Rehydrate",
  "$EL_EncodeReturn",
  "$EL_Settle",
];
export function EL_InvokeFactoryAsync(
  factoryNamePtr: number,
  argsJsonPtr: number,
  returnShape: number,
  promiseId: number,
): void {
  const name = UTF8ToString(factoryNamePtr);
  try {
    const fn = _EL_LookupFactory(name);
    if (!fn) throw new Error(`Unknown factory: ${name}`);
    settleWith(
      promiseId,
      fn(...parseArgs(argsJsonPtr)),
      decodeReturnShape(returnShape),
    );
  } catch (e: unknown) {
    _EL_Settle(promiseId, "err", e instanceof Error ? e.message : String(e));
  }
}

// --- Factory invocation (sync) ---

export const EL_InvokeFactorySync__deps = [
  "$EL_LookupFactory",
  "$EL_Rehydrate",
  "$EL_EncodeReturn",
];
export function EL_InvokeFactorySync(
  factoryNamePtr: number,
  argsJsonPtr: number,
  returnShape: number,
): number {
  try {
    const name = UTF8ToString(factoryNamePtr);
    const fn = _EL_LookupFactory(name);
    if (!fn) throw new Error(`Unknown factory: ${name}`);
    const result = fn(...parseArgs(argsJsonPtr));
    return allocString(
      JSON.stringify(
        _EL_EncodeReturn(result, decodeReturnShape(returnShape)),
      ) ?? "null",
    );
  } catch (e: unknown) {
    return allocString("!err:" + (e instanceof Error ? e.message : String(e)));
  }
}

// --- Object method call (async) ---

export const EL_ObjectCallAsync__deps = [
  "$EL_LookupObject",
  "$EL_Rehydrate",
  "$EL_EncodeReturn",
  "$EL_Settle",
];
export function EL_ObjectCallAsync(
  handle: number,
  methodPtr: number,
  argsJsonPtr: number,
  returnShape: number,
  promiseId: number,
): void {
  const method = UTF8ToString(methodPtr);
  try {
    const obj = _EL_LookupObject(handle);
    if (obj === undefined) throw new Error(`unknown handle: ${handle}`);
    const fn = (obj as Record<string, (...args: unknown[]) => unknown>)[method];
    if (typeof fn !== "function")
      throw new Error(`No method '${method}' on handle ${handle}`);
    settleWith(
      promiseId,
      fn.apply(obj, parseArgs(argsJsonPtr)),
      decodeReturnShape(returnShape),
    );
  } catch (e: unknown) {
    _EL_Settle(promiseId, "err", e instanceof Error ? e.message : String(e));
  }
}

// --- Object method call (sync) ---

export const EL_ObjectCallSync__deps = [
  "$EL_LookupObject",
  "$EL_Rehydrate",
  "$EL_EncodeReturn",
];
export function EL_ObjectCallSync(
  handle: number,
  methodPtr: number,
  argsJsonPtr: number,
  returnShape: number,
): number {
  try {
    const method = UTF8ToString(methodPtr);
    const obj = _EL_LookupObject(handle);
    if (obj === undefined) throw new Error(`unknown handle: ${handle}`);
    const fn = (obj as Record<string, (...args: unknown[]) => unknown>)[method];
    if (typeof fn !== "function")
      throw new Error(`No method '${method}' on handle ${handle}`);
    const result = fn.apply(obj, parseArgs(argsJsonPtr));
    return allocString(
      JSON.stringify(
        _EL_EncodeReturn(result, decodeReturnShape(returnShape)),
      ) ?? "null",
    );
  } catch (e: unknown) {
    return allocString("!err:" + (e instanceof Error ? e.message : String(e)));
  }
}

// --- Object property read (sync) ---

export const EL_ObjectGet__deps = ["$EL_LookupObject"];
export function EL_ObjectGet(handle: number, propPtr: number): number {
  try {
    const prop = UTF8ToString(propPtr);
    const obj = _EL_LookupObject(handle);
    if (obj === undefined) throw new Error(`unknown handle: ${handle}`);
    const value = (obj as Record<string, unknown>)[prop];
    return allocString(JSON.stringify(value) ?? "null");
  } catch (e: unknown) {
    return allocString("!err:" + (e instanceof Error ? e.message : String(e)));
  }
}

// --- Object release ---

export const EL_ObjectRelease__deps = ["$EL_ReleaseObject"];
export function EL_ObjectRelease(handle: number): void {
  _EL_ReleaseObject(handle);
}

// --- Function call (async) ---

export const EL_FunctionCallAsync__deps = [
  "$EL_LookupFunction",
  "$EL_Rehydrate",
  "$EL_EncodeReturn",
  "$EL_Settle",
];
export function EL_FunctionCallAsync(
  handle: number,
  argsJsonPtr: number,
  returnShape: number,
  promiseId: number,
): void {
  try {
    const fn = _EL_LookupFunction(handle);
    if (!fn) throw new Error(`unknown handle: ${handle}`);
    settleWith(
      promiseId,
      fn(...parseArgs(argsJsonPtr)),
      decodeReturnShape(returnShape),
    );
  } catch (e: unknown) {
    _EL_Settle(promiseId, "err", e instanceof Error ? e.message : String(e));
  }
}

// --- Function call (sync) ---

export const EL_FunctionCallSync__deps = [
  "$EL_LookupFunction",
  "$EL_Rehydrate",
  "$EL_EncodeReturn",
];
export function EL_FunctionCallSync(
  handle: number,
  argsJsonPtr: number,
  returnShape: number,
): number {
  try {
    const fn = _EL_LookupFunction(handle);
    if (!fn) throw new Error(`unknown handle: ${handle}`);
    const result = fn(...parseArgs(argsJsonPtr));
    return allocString(
      JSON.stringify(
        _EL_EncodeReturn(result, decodeReturnShape(returnShape)),
      ) ?? "null",
    );
  } catch (e: unknown) {
    return allocString("!err:" + (e instanceof Error ? e.message : String(e)));
  }
}

// --- Function release ---

export const EL_FunctionRelease__deps = ["$EL_ReleaseFunction"];
export function EL_FunctionRelease(handle: number): void {
  _EL_ReleaseFunction(handle);
}

// Dispatcher — the EL_* DllImport entry points.
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
//
// Why are the helpers ($EL_AllocString, $EL_ParseArgs, $EL_DecodeReturnShape,
// $EL_SettleWith) exposed as library entries with the `$` prefix?
//
// Emscripten's library loader treats each entry in `mergeInto(LibraryManager.library, …)`
// as a separately-emitted top-level function in the resulting framework — it
// does NOT preserve the surrounding Rolldown IIFE closure. Without `$EL_`
// declarations + `__deps` references, the helpers would be defined inside the
// IIFE but unreachable from the emitted dispatcher functions, causing every
// async call to silently throw ReferenceError after the JS side computes the
// result.

import type { ReturnShape } from "./registries";

// Helpers exposed as $-prefixed library entries so they survive Emscripten's
// per-entry extraction (see the file header for the why). The dispatcher
// entries depend on these via __deps so they're hoisted into framework scope.
//
// Numeric codes for ReturnShape, passed across the DllImport boundary, must
// stay in sync with the C# BridgeReturnShape enum (Phase 3):
//   0 = value     (plain JSON round-trip; the default)
//   1 = object    (allocate a JsObject handle, return { $ref })
//   2 = function  (allocate a JsFunction handle, return { $fn })
//   3 = void      (discard the return, settle null)
//
// The lookup table is inlined into the function body rather than declared as a
// module-level const because Emscripten extracts each library entry separately
// — module-level values do not survive and would produce a runtime ReferenceError.
export function $EL_DecodeReturnShape(code: number): ReturnShape {
  return (
    (["value", "object", "function", "void"] as ReturnShape[])[code] ?? "value"
  );
}

// Allocates a UTF-8 string on the Emscripten heap and returns the pointer.
// The C# caller copies then frees the returned memory.
export function $EL_AllocString(s: string): number {
  const len = lengthBytesUTF8(s) + 1;
  const buf = _malloc(len);
  stringToUTF8(s, buf, len);
  return buf;
}

// Parses the args JSON pointer and returns an array of rehydrated values.
// A zero pointer is treated as an empty args list.
export const $EL_ParseArgs__deps = ["$EL_Rehydrate"];
export function $EL_ParseArgs(argsJsonPtr: number): unknown[] {
  const json = argsJsonPtr ? UTF8ToString(argsJsonPtr) : "[]";
  return (JSON.parse(json || "[]") as unknown[]).map((v) => EL_Rehydrate(v));
}

// Resolves resultOrPromise (which may be a live Promise), encodes the return
// value with the given shape, then settles the C# await via the settle channel.
export const $EL_SettleWith__deps = ["$EL_EncodeReturn", "$EL_Settle"];
export function $EL_SettleWith(
  promiseId: number,
  resultOrPromise: unknown,
  returnShape: ReturnShape,
): void {
  Promise.resolve(resultOrPromise)
    .then((result: unknown) => {
      const encoded = EL_EncodeReturn(result, returnShape);
      EL_Settle(promiseId, "ok", JSON.stringify(encoded) ?? "null");
    })
    .catch((e: unknown) => {
      EL_Settle(promiseId, "err", e instanceof Error ? e.message : String(e));
    });
}

// --- Factory invocation (async) ---

export const EL_InvokeFactoryAsync__deps = [
  "$EL_LookupFactory",
  "$EL_ParseArgs",
  "$EL_DecodeReturnShape",
  "$EL_SettleWith",
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
    const fn = EL_LookupFactory(name);
    if (!fn) throw new Error(`Unknown factory: ${name}`);
    EL_SettleWith(
      promiseId,
      fn(...EL_ParseArgs(argsJsonPtr)),
      EL_DecodeReturnShape(returnShape),
    );
  } catch (e: unknown) {
    EL_Settle(promiseId, "err", e instanceof Error ? e.message : String(e));
  }
}

// --- Factory invocation (sync) ---

export const EL_InvokeFactorySync__deps = [
  "$EL_LookupFactory",
  "$EL_ParseArgs",
  "$EL_DecodeReturnShape",
  "$EL_EncodeReturn",
  "$EL_AllocString",
];
export function EL_InvokeFactorySync(
  factoryNamePtr: number,
  argsJsonPtr: number,
  returnShape: number,
): number {
  try {
    const name = UTF8ToString(factoryNamePtr);
    const fn = EL_LookupFactory(name);
    if (!fn) throw new Error(`Unknown factory: ${name}`);
    const result = fn(...EL_ParseArgs(argsJsonPtr));
    return EL_AllocString(
      JSON.stringify(
        EL_EncodeReturn(result, EL_DecodeReturnShape(returnShape)),
      ) ?? "null",
    );
  } catch (e: unknown) {
    return EL_AllocString(
      "!err:" + (e instanceof Error ? e.message : String(e)),
    );
  }
}

// --- Object method call (async) ---

export const EL_ObjectCallAsync__deps = [
  "$EL_LookupObject",
  "$EL_ParseArgs",
  "$EL_DecodeReturnShape",
  "$EL_SettleWith",
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
    const obj = EL_LookupObject(handle);
    if (obj === undefined) throw new Error(`unknown handle: ${handle}`);
    const fn = (obj as Record<string, (...args: unknown[]) => unknown>)[method];
    if (typeof fn !== "function")
      throw new Error(`No method '${method}' on handle ${handle}`);
    EL_SettleWith(
      promiseId,
      fn.apply(obj, EL_ParseArgs(argsJsonPtr)),
      EL_DecodeReturnShape(returnShape),
    );
  } catch (e: unknown) {
    EL_Settle(promiseId, "err", e instanceof Error ? e.message : String(e));
  }
}

// --- Object method call (sync) ---

export const EL_ObjectCallSync__deps = [
  "$EL_LookupObject",
  "$EL_ParseArgs",
  "$EL_DecodeReturnShape",
  "$EL_EncodeReturn",
  "$EL_AllocString",
];
export function EL_ObjectCallSync(
  handle: number,
  methodPtr: number,
  argsJsonPtr: number,
  returnShape: number,
): number {
  try {
    const method = UTF8ToString(methodPtr);
    const obj = EL_LookupObject(handle);
    if (obj === undefined) throw new Error(`unknown handle: ${handle}`);
    const fn = (obj as Record<string, (...args: unknown[]) => unknown>)[method];
    if (typeof fn !== "function")
      throw new Error(`No method '${method}' on handle ${handle}`);
    const result = fn.apply(obj, EL_ParseArgs(argsJsonPtr));
    return EL_AllocString(
      JSON.stringify(
        EL_EncodeReturn(result, EL_DecodeReturnShape(returnShape)),
      ) ?? "null",
    );
  } catch (e: unknown) {
    return EL_AllocString(
      "!err:" + (e instanceof Error ? e.message : String(e)),
    );
  }
}

// --- Object property read (sync) ---

export const EL_ObjectGet__deps = ["$EL_LookupObject", "$EL_AllocString"];
export function EL_ObjectGet(handle: number, propPtr: number): number {
  try {
    const prop = UTF8ToString(propPtr);
    const obj = EL_LookupObject(handle);
    if (obj === undefined) throw new Error(`unknown handle: ${handle}`);
    const value = (obj as Record<string, unknown>)[prop];
    return EL_AllocString(JSON.stringify(value) ?? "null");
  } catch (e: unknown) {
    return EL_AllocString(
      "!err:" + (e instanceof Error ? e.message : String(e)),
    );
  }
}

// --- Object release ---

export const EL_ObjectRelease__deps = ["$EL_ReleaseObject"];
export function EL_ObjectRelease(handle: number): void {
  EL_ReleaseObject(handle);
}

// --- Function call (async) ---

export const EL_FunctionCallAsync__deps = [
  "$EL_LookupFunction",
  "$EL_ParseArgs",
  "$EL_DecodeReturnShape",
  "$EL_SettleWith",
  "$EL_Settle",
];
export function EL_FunctionCallAsync(
  handle: number,
  argsJsonPtr: number,
  returnShape: number,
  promiseId: number,
): void {
  try {
    const fn = EL_LookupFunction(handle);
    if (!fn) throw new Error(`unknown handle: ${handle}`);
    EL_SettleWith(
      promiseId,
      fn(...EL_ParseArgs(argsJsonPtr)),
      EL_DecodeReturnShape(returnShape),
    );
  } catch (e: unknown) {
    EL_Settle(promiseId, "err", e instanceof Error ? e.message : String(e));
  }
}

// --- Function call (sync) ---

export const EL_FunctionCallSync__deps = [
  "$EL_LookupFunction",
  "$EL_ParseArgs",
  "$EL_DecodeReturnShape",
  "$EL_EncodeReturn",
  "$EL_AllocString",
];
export function EL_FunctionCallSync(
  handle: number,
  argsJsonPtr: number,
  returnShape: number,
): number {
  try {
    const fn = EL_LookupFunction(handle);
    if (!fn) throw new Error(`unknown handle: ${handle}`);
    const result = fn(...EL_ParseArgs(argsJsonPtr));
    return EL_AllocString(
      JSON.stringify(
        EL_EncodeReturn(result, EL_DecodeReturnShape(returnShape)),
      ) ?? "null",
    );
  } catch (e: unknown) {
    return EL_AllocString(
      "!err:" + (e instanceof Error ? e.message : String(e)),
    );
  }
}

// --- Function release ---

export const EL_FunctionRelease__deps = ["$EL_ReleaseFunction"];
export function EL_FunctionRelease(handle: number): void {
  EL_ReleaseFunction(handle);
}

// --- Heap memory management ---

// Thin wrapper so C# can free sync-result heap strings via a named DllImport
// instead of calling _free directly. Calling Emscripten's _free as a DllImport
// from IL2CPP fails at WebGL link time because _free is not in wasm exports;
// going through a jslib wrapper keeps the call on the JS side where it works.
export function EL_Free(ptr: number): void {
  _free(ptr);
}

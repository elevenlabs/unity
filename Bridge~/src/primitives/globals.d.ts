// Ambient declarations for runtime globals that the Emscripten jslib loader
// makes available, but @types/emscripten doesn't cover.
//
// Naming convention: Emscripten library entries prefixed with `$` are JS-internal
// (NOT exported to C). After Emscripten links the library, `$Foo` becomes `Foo`
// at runtime — the `$` is stripped, and NO underscore is added. Function bodies
// in other primitive modules therefore reference these entries by their stripped
// (no-underscore) name.
//
// Non-$ entries are different: a library entry like `EL_InvokeFactoryAsync` gets
// the standard `_` prefix because it's C-callable, becoming `_EL_InvokeFactoryAsync`.
// Those are called from C, not from inside this library bundle, so they don't
// need ambient declarations here.
//
// UTF8ToString is already declared by @types/emscripten.

// --- $EL_Log helper from log.ts ---
declare function EL_Log(
  level: "info" | "warn" | "error",
  scope: string,
  msg: string,
): void;

// --- Registry tables (from registries.ts $-prefixed entries) ---
declare const EL_Objects: Record<number, unknown>;
declare const EL_Functions: Record<number, (...args: unknown[]) => unknown>;
declare const EL_Factories: Record<string, (...args: unknown[]) => unknown>;
declare let EL_NextHandleId: number;

// --- Registry helpers (from registries.ts $-prefixed entries) ---
declare function EL_RegisterFactory(
  name: string,
  fn: (...args: unknown[]) => unknown,
): void;
declare function EL_AllocateObject(obj: unknown): number;
declare function EL_AllocateFunction(
  fn: (...args: unknown[]) => unknown,
): number;
declare function EL_LookupObject(handle: number): unknown;
declare function EL_LookupFunction(
  handle: number,
): ((...args: unknown[]) => unknown) | undefined;
declare function EL_LookupFactory(
  name: string,
): ((...args: unknown[]) => unknown) | undefined;
declare function EL_ReleaseObject(handle: number): void;
declare function EL_ReleaseFunction(handle: number): void;

// --- Marshalling helpers (from marshalling.ts $-prefixed entries) ---
declare function EL_Rehydrate(value: unknown): unknown;
declare function EL_EncodeReturn(
  value: unknown,
  shape: "object" | "function" | "value" | "void",
): unknown;

// --- Dispatcher helpers (from dispatcher.ts $-prefixed entries) ---
declare function EL_DecodeReturnShape(
  code: number,
): "object" | "function" | "value" | "void";
declare function EL_AllocString(s: string): number;
declare function EL_ParseArgs(argsJsonPtr: number): unknown[];
declare function EL_SettleWith(
  promiseId: number,
  resultOrPromise: unknown,
  returnShape: "object" | "function" | "value" | "void",
): void;

// --- Callback dispatch (from $EL_InvokeCallback in callbacks.ts) ---
declare function EL_InvokeCallback(handle: number, payload: string): void;

// --- Settle channel (from $EL_Settle in promise-settle.ts) ---
declare function EL_Settle(
  promiseId: number,
  status: "ok" | "err",
  payload: string,
): void;

// The Emscripten module object — available as a runtime global in jslib context.
// @types/emscripten declares EmscriptenModule as an interface but does not
// expose a global `Module` variable; Unity's Emscripten runtime does.
declare const Module: EmscriptenModule & { wasmTable?: WebAssembly.Table };

// Function-pointer slots for the DynCall bridge channels (function-pointers.ts).
// Initialised to 0; set once at bridge startup by EL_SetSettleCallback /
// EL_SetInvokeCallbackPtr before any async or callback operation can fire.
declare let EL_SettlePtr: number;
declare let EL_CallbackPtr: number;

// DynCall macro entry points — at runtime these are the implementations the
// `{{{ makeDynCall('sig', 'fnVar') }}}` macro expands to, but in TypeScript we
// type them as named functions so the source compiles before the bundler's
// substitute-make-dyncall plugin (Phase 2.5.6) rewrites the call sites. The
// signature characters follow Emscripten's convention: `v` = void return,
// `i` = i32 argument.
//
// One `declare` per signature is intentional rather than a generic
// `dynCall<S extends string>(sig: S, fnPtr, ...args)` with template-literal
// arg derivation: it keeps per-call-site mocks in tests as separate spies
// (`dynCall_viii` vs `dynCall_vii`), and lets the plugin treat the identifier
// name as the statically-known sig instead of having to validate that the
// first argument is a string literal. Revisit when adding a third signature
// or if this channel gets extracted as a standalone library.
declare function dynCall_viii(
  fnPtr: number,
  arg0: number,
  arg1: number,
  arg2: number,
): void;
declare function dynCall_vii(fnPtr: number, arg0: number, arg1: number): void;

// Emscripten heap allocation + UTF-8 string materialisation — available
// globally in the jslib runtime. `stringToNewUTF8` allocates a fresh heap
// buffer and writes a null-terminated UTF-8 encoding of the JS string; the
// caller owns the buffer and must `_free` it once the DynCall has returned.
declare function _malloc(size: number): number;
declare function _free(ptr: number): void;
declare function stringToNewUTF8(str: string): number;

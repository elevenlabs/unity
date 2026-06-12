// Function-pointer globals for the DynCall bridge channels.
//
// C# registers its static callback methods as wasm function pointers at startup
// via EL_SetSettleCallback and EL_SetInvokeCallbackPtr. The JS side stores the
// pointers in these globals and invokes them via the
// {{{ makeDynCall('sig', 'fnVar') }}} macro (substituted at bundle time by
// Bridge~/build/substitute-make-dyncall.ts).
//
// Both globals are initialised to 0 (null pointer) until the C# bridge
// initialiser runs (RuntimeInitializeLoadType.SubsystemRegistration). The
// bridge is not usable until both are registered.

export const $EL_SettlePtr = 0;
export const $EL_CallbackPtr = 0;

export const EL_SetSettleCallback__deps = ["$EL_SettlePtr"];
export function EL_SetSettleCallback(ptr: number): void {
  _EL_SettlePtr = ptr;
}

export const EL_SetInvokeCallbackPtr__deps = ["$EL_CallbackPtr"];
export function EL_SetInvokeCallbackPtr(ptr: number): void {
  _EL_CallbackPtr = ptr;
}

// Returns 1 if Module.wasmTable is in scope (Use WebAssembly.Table is enabled),
// 0 otherwise. Called once at bridge startup as a defence-in-depth runtime check.
// Warn-only: Closure renaming can hide wasmTable even when the setting is on, so
// a false-negative here does not abort the bridge — the authoritative signal is the
// first EL_SetSettleCallback DynCall itself (see BridgeStaticCallbacks.cs).
export function EL_ProbeWasmTable(): number {
  return typeof Module.wasmTable !== "undefined" ? 1 : 0;
}

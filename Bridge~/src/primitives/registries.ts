// Shared state and helpers for the primitive registries:
//
//   $EL_Objects     — handle (int) -> JS object held on behalf of C#
//   $EL_Functions   — handle (int) -> JS function held on behalf of C#
//   $EL_Factories   — name -> fn registered by external JS at boot
//   $EL_NextHandleId — shared counter for JS-allocated object/function handles
//
// All four are $-prefixed so Unity hoists them as module-level globals
// (`_EL_Objects`, etc.) that other primitive modules access via __deps.
//
// Handle allocator is shared between objects and functions: there is one JS-side
// ID space for "things JS handed back to C# that need a registry entry." C# IDs
// for callbacks and promise IDs come from a separate C#-allocated space, so
// collisions across spaces are impossible.
//
// Return shapes are NOT stored here — they're passed per-call by C# as an int
// argument on every dispatcher entry point (see dispatcher.ts). This keeps the
// C# generic `<T>` parameter as the single source of truth for how a return
// value should be decoded.

export type ReturnShape = "object" | "function" | "value" | "void";

export type FactoryFn = (...args: unknown[]) => unknown;

export const $EL_Objects = {};
export const $EL_Functions = {};
export const $EL_Factories = {};
export const $EL_NextHandleId = 1;

// --- Factory registration (called from external JS at app boot) ---

export const $EL_RegisterFactory__deps = ["$EL_Factories"];
export function $EL_RegisterFactory(name: string, fn: FactoryFn): void {
  _EL_Factories[name] = fn;
}

// --- Object registry ---

export const $EL_AllocateObject__deps = ["$EL_Objects", "$EL_NextHandleId"];
export function $EL_AllocateObject(obj: unknown): number {
  const handle = _EL_NextHandleId++;
  _EL_Objects[handle] = obj;
  return handle;
}

export const $EL_LookupObject__deps = ["$EL_Objects"];
export function $EL_LookupObject(handle: number): unknown {
  return _EL_Objects[handle];
}

export const $EL_ReleaseObject__deps = ["$EL_Objects"];
export function $EL_ReleaseObject(handle: number): void {
  delete _EL_Objects[handle];
}

// --- Function registry ---

export const $EL_AllocateFunction__deps = ["$EL_Functions", "$EL_NextHandleId"];
export function $EL_AllocateFunction(fn: FactoryFn): number {
  const handle = _EL_NextHandleId++;
  _EL_Functions[handle] = fn;
  return handle;
}

export const $EL_LookupFunction__deps = ["$EL_Functions"];
export function $EL_LookupFunction(handle: number): FactoryFn | undefined {
  return _EL_Functions[handle];
}

export const $EL_ReleaseFunction__deps = ["$EL_Functions"];
export function $EL_ReleaseFunction(handle: number): void {
  delete _EL_Functions[handle];
}

// --- Factory lookup (used by the dispatcher) ---

export const $EL_LookupFactory__deps = ["$EL_Factories"];
export function $EL_LookupFactory(name: string): FactoryFn | undefined {
  return _EL_Factories[name];
}

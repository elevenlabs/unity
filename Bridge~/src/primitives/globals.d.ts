// Ambient declarations for runtime globals that Unity injects but @types/emscripten
// doesn't cover: SendMessage (Unity-specific) and the hoisted _EL_* globals that
// Unity creates from $-prefixed mergeInto entries when another function lists them
// in its __deps array. UTF8ToString is already declared by @types/emscripten.

declare function SendMessage(
  gameObject: string,
  method: string,
  value: string,
): void;

declare let _EL_BridgeName: string;
declare function _EL_Log(
  level: "info" | "warn" | "error",
  scope: string,
  msg: string,
): void;

// Registry tables — Unity hoists these from the $EL_-prefixed library entries.
// Other primitive modules declare them in __deps and reference them via the
// `_EL_*` names below.
declare const _EL_Objects: Record<number, unknown>;
declare const _EL_Functions: Record<number, (...args: unknown[]) => unknown>;
declare const _EL_Factories: Record<
  string,
  {
    fn: (...args: unknown[]) => unknown;
    returnShape: "object" | "function" | "value" | "void";
  }
>;
declare let _EL_NextHandleId: number;

// Registry helpers — hoisted by Unity when listed in __deps.
declare function _EL_AllocateObject(obj: unknown): number;
declare function _EL_AllocateFunction(
  fn: (...args: unknown[]) => unknown,
): number;

// Callback dispatch — hoisted from $EL_InvokeCallback in callbacks.ts.
declare function _EL_InvokeCallback(handle: number, payload: string): void;

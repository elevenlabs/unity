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
declare const _EL_Observers: Record<number, () => void>;
declare const _EL_PendingInvocations: Record<
  number,
  { resolve: (value: string) => void; reject: (reason: Error) => void }
>;
declare let _EL_InvocationCounter: number;

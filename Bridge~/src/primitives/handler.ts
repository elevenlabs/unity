// ── Handler Invocation primitive ───────────────────────────────────────────
// Maps invocation ID (int) → { resolve, reject } for in-flight C# handler calls.
export const $EL_PendingInvocations: Record<
  number,
  { resolve: (value: string) => void; reject: (reason: Error) => void }
> = {};

// Monotonic counter for JS-side invocation ID generation.
export const $EL_InvocationCounter = 0;

// Creates a handler invocation: fires SendMessage to C# with the handler name
// and payload, stores Promise resolvers keyed by the generated ID, and returns
// the Promise. Consumer jslib code awaits this to get the C# handler response.
// handlerName: JS string identifying the registered C# handler.
// payload: JS string (typically JSON) passed to the C# handler.
export const $EL_InvokeHandler__deps = [
  "$EL_BridgeName",
  "$EL_PendingInvocations",
  "$EL_InvocationCounter",
];
export function $EL_InvokeHandler(
  handlerName: string,
  payload: string,
): Promise<string> {
  const id = ++_EL_InvocationCounter;
  const promise = new Promise<string>(function (resolve, reject) {
    _EL_PendingInvocations[id] = { resolve: resolve, reject: reject };
  });
  SendMessage(
    _EL_BridgeName,
    "OnHandlerInvoked",
    id + ":" + handlerName + ":" + payload,
  );
  return promise;
}

// Called from C# DllImport when the registered handler completes successfully.
// Resolves the Promise stored for invocationId and removes the entry.
// No-op if the ID is not found (handles stale signals safely).
export const EL_ResolveInvocation__deps = ["$EL_PendingInvocations", "$EL_Log"];
export function EL_ResolveInvocation(
  invocationId: number,
  resultPtr: number,
): void {
  try {
    const entry = _EL_PendingInvocations[invocationId];
    if (!entry) {
      return;
    }
    delete _EL_PendingInvocations[invocationId];
    entry.resolve(UTF8ToString(resultPtr));
  } catch (e) {
    _EL_Log(
      "error",
      "EL_ResolveInvocation",
      (e as Error)?.message || String(e),
    );
  }
}

// Called from C# DllImport when the handler fails or no handler is registered.
// Rejects the stored Promise and removes the entry.
// No-op if the ID is not found.
export const EL_RejectInvocation__deps = ["$EL_PendingInvocations", "$EL_Log"];
export function EL_RejectInvocation(
  invocationId: number,
  errorPtr: number,
): void {
  try {
    const entry = _EL_PendingInvocations[invocationId];
    if (!entry) {
      return;
    }
    delete _EL_PendingInvocations[invocationId];
    entry.reject(new Error(UTF8ToString(errorPtr)));
  } catch (e) {
    _EL_Log("error", "EL_RejectInvocation", (e as Error)?.message || String(e));
  }
}

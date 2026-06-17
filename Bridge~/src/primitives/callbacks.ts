// Callback dispatch helper for the bridge wire protocol.
//
// $EL_InvokeCallback(handle, payload) invokes the C#-registered callback
// function pointer via dynCall_vii. JS allocates a UTF-8 payload buffer via
// stringToNewUTF8 and frees it once the DynCall returns — the call is
// synchronous by construction on the wasm call stack so the C# handler has
// finished reading the buffer before _free runs.
//
// JS never releases callback handles — the C# registry is the authority on
// lifetime; closures that capture a handle may outlive the C# registration
// and will silently no-op once the C# side has disposed the entry.

export const $EL_InvokeCallback__deps = ["$EL_CallbackPtr"];
export function $EL_InvokeCallback(handle: number, payload: string): void {
  const payloadPtr = stringToNewUTF8(payload);
  try {
    dynCall_vii(EL_CallbackPtr, handle, payloadPtr);
  } finally {
    _free(payloadPtr);
  }
}

// Settle helper for the bridge wire protocol.
//
// $EL_Settle(promiseId, status, payload) invokes the C#-registered settle
// function pointer via dynCall_viii. statusCode is 0 for "ok" / 1 for "err".
// JS allocates a UTF-8 payload buffer via stringToNewUTF8 and frees it once
// the DynCall returns — the call is synchronous by construction on the wasm
// call stack so the C# handler has finished reading the buffer before _free runs.

export const $EL_Settle__deps = ["$EL_SettlePtr"];
export function $EL_Settle(
  promiseId: number,
  status: "ok" | "err",
  payload: string,
): void {
  const statusCode = status === "ok" ? 0 : 1;
  const payloadPtr = stringToNewUTF8(payload);
  try {
    dynCall_viii(EL_SettlePtr, promiseId, statusCode, payloadPtr);
  } finally {
    _free(payloadPtr);
  }
}

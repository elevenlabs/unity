// Settle helper for the bridge wire protocol.
//
// $EL_Settle(promiseId, status, payload) fires a SendMessage to the Unity bridge
// GameObject, delivering `promiseId + ':' + status + ':' + payload` to the
// OnPromiseSettled C# handler. Every async entry point calls this on resolution
// or rejection; the C# registry routes the message to the waiting
// AwaitableCompletionSource<string> by promise ID.

export const $EL_Settle__deps = ["$EL_BridgeName"];
export function $EL_Settle(
  promiseId: number,
  status: "ok" | "err",
  payload: string,
): void {
  SendMessage(
    _EL_BridgeName,
    "OnPromiseSettled",
    promiseId + ":" + status + ":" + payload,
  );
}

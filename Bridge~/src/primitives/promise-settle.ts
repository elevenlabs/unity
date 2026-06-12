// Settle helper for the bridge wire protocol.
//
// $EL_Settle(promiseId, status, payload) fires a SendMessage to the Unity bridge
// GameObject, delivering `promiseId + ':' + status + ':' + payload` to the
// OnPromiseSettled C# handler. Every async entry point calls this on resolution
// or rejection; the C# registry routes the message to the waiting
// AwaitableCompletionSource<string> by promise ID.

// Local stubs for the SendMessage-era globals. The ambient declarations were
// removed from globals.d.ts in task 2.5.2; this file is rewritten in task 2.5.3
// to use the DynCall channel, at which point these stubs disappear.
declare function SendMessage(
  gameObject: string,
  method: string,
  value: string,
): void;
declare const _EL_BridgeName: string;

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

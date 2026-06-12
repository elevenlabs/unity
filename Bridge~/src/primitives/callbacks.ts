// Callback dispatch helper for the bridge wire protocol.
//
// $EL_InvokeCallback(handle, payload) fires a SendMessage to the Unity bridge
// GameObject, delivering `handle + ':' + payload` to the OnCallbackInvoked
// C# handler. JS never releases callback handles — the C# registry is the
// authority on lifetime; closures that capture a handle may outlive the C#
// registration and will silently no-op once the C# side has disposed the entry.

// Local stubs for the SendMessage-era globals. The ambient declarations were
// removed from globals.d.ts in task 2.5.2; this file is rewritten in task 2.5.4
// to use the DynCall channel, at which point these stubs disappear.
declare function SendMessage(
  gameObject: string,
  method: string,
  value: string,
): void;
declare const _EL_BridgeName: string;

export const $EL_InvokeCallback__deps = ["$EL_BridgeName"];
export function $EL_InvokeCallback(handle: number, payload: string): void {
  SendMessage(_EL_BridgeName, "OnCallbackInvoked", handle + ":" + payload);
}

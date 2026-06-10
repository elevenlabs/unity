// Callback dispatch helper for the bridge wire protocol.
//
// $EL_InvokeCallback(handle, payload) fires a SendMessage to the Unity bridge
// GameObject, delivering `handle + ':' + payload` to the OnCallbackInvoked
// C# handler. JS never releases callback handles — the C# registry is the
// authority on lifetime; closures that capture a handle may outlive the C#
// registration and will silently no-op once the C# side has disposed the entry.

export const $EL_InvokeCallback__deps = ["$EL_BridgeName"];
export function $EL_InvokeCallback(handle: number, payload: string): void {
  SendMessage(_EL_BridgeName, "OnCallbackInvoked", handle + ":" + payload);
}

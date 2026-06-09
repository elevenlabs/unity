// Shared state: name of the Unity GameObject that JS targets via SendMessage.
// Unity initialises this as a global _EL_BridgeName before any function that
// declares it as a dep is called. Set once at startup via EL_SetBridgeName;
// treated as read-only by every other function.
export const $EL_BridgeName = "";

export const EL_SetBridgeName__deps = ["$EL_BridgeName"];
export function EL_SetBridgeName(namePtr: number): void {
  _EL_BridgeName = UTF8ToString(namePtr);
}

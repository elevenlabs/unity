// ── Observer primitive ─────────────────────────────────────────────────────
// Maps observer ID (int) → unsubscribe function. Consumer jslib code writes
// here when setting up an event source; EL_DisposeObserver cleans up.
export const $EL_Observers: Record<number, () => void> = {};

// Stores the unsubscribe function for an observer. Consumer jslib code calls
// _EL_RegisterObserver(id, unsubscribeFn) after subscribing to their event source.
export const $EL_RegisterObserver__deps = ["$EL_Observers"];
export function $EL_RegisterObserver(
  observerId: number,
  unsubscribe: () => void,
): void {
  _EL_Observers[observerId] = unsubscribe;
}

// Fires an observer event to C# via SendMessage. Consumer jslib code calls
// _EL_EmitEvent(id, serialisedPayload) from their event handler.
// payload must already be serialised (e.g. JSON.stringify(event)).
export const $EL_EmitEvent__deps = ["$EL_BridgeName"];
export function $EL_EmitEvent(observerId: number, payload: string): void {
  SendMessage(_EL_BridgeName, "OnObserverEvent", observerId + ":" + payload);
}

// Called from C# DllImport when BridgeObserverHandle.Dispose() runs.
// Calls the stored unsubscribe function and removes the entry. No-op if the
// ID is not found (handles double-dispose and stale JS signals safely).
export const EL_DisposeObserver__deps = ["$EL_Observers", "$EL_Log"];
export function EL_DisposeObserver(observerId: number): void {
  try {
    const unsubscribe = _EL_Observers[observerId];
    if (!unsubscribe) {
      return;
    }
    delete _EL_Observers[observerId];
    unsubscribe();
  } catch (e) {
    _EL_Log("error", "EL_DisposeObserver", (e as Error)?.message || String(e));
  }
}

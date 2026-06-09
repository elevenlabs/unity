// Internal helper that routes a JS Promise result back to C# via SendMessage.
// Call this from any jslib function after starting async work. Synchronous
// errors in the setup code must still be caught in the caller's try/catch.
export const $EL_CallPromise__deps = ["$EL_BridgeName", "$EL_Log"];
export function $EL_CallPromise(
  promiseId: number,
  promise: Promise<unknown>,
): void {
  promise.then(
    function (result) {
      const payload = result == null ? "" : String(result);
      SendMessage(
        _EL_BridgeName,
        "OnPromiseSettled",
        promiseId + ":ok:" + payload,
      );
    },
    function (e) {
      const msg = e && e.message ? e.message : String(e);
      _EL_Log("error", "EL_CallPromise", msg);
      SendMessage(
        _EL_BridgeName,
        "OnPromiseSettled",
        promiseId + ":err:" + msg,
      );
    },
  );
}

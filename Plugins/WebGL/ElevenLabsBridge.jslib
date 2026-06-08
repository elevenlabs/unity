mergeInto(LibraryManager.library, {
  // Shared state: name of the Unity GameObject that JS targets via SendMessage.
  // Unity initialises this as a global _EL_BridgeName before any function that
  // declares it as a dep is called. Set once at startup via EL_SetBridgeName;
  // treated as read-only by every other function.
  $EL_BridgeName: "",

  EL_SetBridgeName__deps: ["$EL_BridgeName"],
  EL_SetBridgeName: function (namePtr) {
    _EL_BridgeName = UTF8ToString(namePtr);
  },

  // Internal logging helper. Never called from C# ($ prefix keeps it out of
  // DllImport resolution). Functions that use it declare `$EL_Log` as a dep
  // and call it as `_EL_Log(level, scope, message)`.
  //
  // level: "info" | "warn" | "error"
  $EL_Log: function (level, scope, msg) {
    var line = "[ElevenLabs Bridge] " + scope + ": " + msg;
    if (level === "error") console.error(line);
    else if (level === "warn") console.warn(line);
    else console.log(line);
  },

  // Internal helper that routes a JS Promise result back to C# via SendMessage.
  // Call this from any jslib function after starting async work. Synchronous
  // errors in the setup code must still be caught in the caller's try/catch.
  $EL_CallPromise__deps: ["$EL_BridgeName", "$EL_Log"],
  $EL_CallPromise: function (promiseId, promise) {
    promise.then(
      function (result) {
        var payload = result == null ? "" : String(result);
        SendMessage(
          _EL_BridgeName,
          "OnPromiseSettled",
          promiseId + ":ok:" + payload,
        );
      },
      function (e) {
        var msg = e && e.message ? e.message : String(e);
        _EL_Log("error", "EL_CallPromise", msg);
        SendMessage(
          _EL_BridgeName,
          "OnPromiseSettled",
          promiseId + ":err:" + msg,
        );
      },
    );
  },

  // ── Cross-cutting error-handling template ─────────────────────────────────
  // Every jslib function that starts async work MUST follow this pattern.
  // $EL_CallPromise routes the Promise result; the try/catch catches sync errors.
  //
  // EL_SomeFunction__deps: ["$EL_BridgeName", "$EL_Log", "$EL_CallPromise"],
  // EL_SomeFunction: function (promiseId, argPtr) {
  //   try {
  //     var arg = UTF8ToString(argPtr);
  //     var promise = someAsyncWork(arg); // must return a Promise
  //     _EL_CallPromise(promiseId, promise);
  //   } catch (e) {
  //     _EL_Log("error", "EL_SomeFunction", e.message || String(e));
  //     SendMessage(
  //       _EL_BridgeName,
  //       "OnPromiseSettled",
  //       promiseId + ":err:" + (e.message || String(e)),
  //     );
  //   }
  // },
});

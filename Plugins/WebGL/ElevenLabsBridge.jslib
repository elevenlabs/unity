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

  // ── Cross-cutting error-handling template ─────────────────────────────────
  // Every jslib function that starts async work MUST wrap its body in this
  // try/catch so errors are routed back through the same settlement channel
  // and the consumer's `await` throws instead of silently hanging.
  //
  // EL_SomeFunction__deps: ["$EL_BridgeName", "$EL_Log"],
  // EL_SomeFunction: function (promiseId, argPtr) {
  //   try {
  //     var arg = UTF8ToString(argPtr);
  //     // ... actual async work ...
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

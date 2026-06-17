// EL_SmokeTest_RegisterMathFactory — C# DllImport entry point bundled into
// ElevenLabsBridge.jslib. The C# smoke test (BridgePrimitiveSmokeTest.cs) calls
// this once at Start() to register the mathFactory used to exercise every
// bridge primitive surface (sync/async methods, property read, BridgeCallback
// fan-out, function-handle round-trip).
//
// Why bundled here and not in a separate BridgePrimitiveSmokeTest.jslib?
//   The factory body needs to call $EL_RegisterFactory (referenced as
//   EL_RegisterFactory at runtime — see the naming convention in globals.d.ts).
//   Putting the call in the same bundle means Emscripten guarantees the dep is
//   in scope, with no cross-jslib linker reasoning.
//
// Why is it kept in the production primitives bundle rather than gated on a
// build define?
//   The bundle is small (~50 lines) and inert: the factory is only materialised
//   when a caller asks for "mathFactory" by name via EL_InvokeFactoryAsync. A
//   shipping consumer never hits it.

export const EL_SmokeTest_RegisterMathFactory__deps = ["$EL_RegisterFactory"];

export function EL_SmokeTest_RegisterMathFactory(): void {
  // Each invocation creates an independent math object with its own listeners
  // array. The smoke test only calls this once.
  EL_RegisterFactory("mathFactory", () => {
    const listeners: Array<(v: number) => void> = [];

    return {
      add: (a: number, b: number) => a + b,
      addAsync: (a: number, b: number) => Promise.resolve(a + b),
      pi: Math.PI,

      addTickListener: (cb: (v: number) => void) => {
        listeners.push(cb);
        return () => {
          const idx = listeners.indexOf(cb);
          if (idx >= 0) listeners.splice(idx, 1);
        };
      },

      // Fires all registered callbacks synchronously with the given value.
      invokeTick: (value: number) => {
        [...listeners].forEach((cb) => cb(value));
      },

      // V1 variant: fires all callbacks from inside a resolved Promise microtask.
      invokeTickFromPromise: (value: number) =>
        Promise.resolve().then(() => {
          [...listeners].forEach((cb) => cb(value));
        }),

      // V3 variant: fires all callbacks `count` times, passing sequential indices.
      invokeTickN: (count: number) => {
        const lns = [...listeners];
        for (let i = 0; i < count; i++) {
          for (const cb of lns) cb(i);
        }
      },

      // V4 variant: returns a JS function that adds `n` to its argument.
      makeAdder: (n: number) => (x: number) => n + x,
    };
  });
}

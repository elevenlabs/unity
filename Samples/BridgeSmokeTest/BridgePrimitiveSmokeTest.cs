using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ElevenLabs.WebGL;
using UnityEngine;

namespace ElevenLabs.WebGL.Samples
{
    /// <summary>
    /// WebGL smoke-test MonoBehaviour that exercises every bridge primitive surface
    /// end-to-end against the mathFactory registered by ElevenLabsBridge.jslib.
    /// Add to a scene and build for WebGL; results are logged to the browser console.
    /// </summary>
    public class BridgePrimitiveSmokeTest : MonoBehaviour
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void EL_SmokeTest_RegisterMathFactory();
#else
        private static void EL_SmokeTest_RegisterMathFactory() { }
#endif

        private void Start()
        {
            _ = RunSmokeTestAsync();
        }

        private async Awaitable RunSmokeTestAsync()
        {
            // Registers the mathFactory in the JS registry. Defined in
            // smoke-test-factory.ts and bundled into ElevenLabsBridge.jslib so
            // EL_RegisterFactory is in scope when the call lands.
            EL_SmokeTest_RegisterMathFactory();

            using var math = await JsBridge.InvokeFactoryAsync<JsObject>("mathFactory");

            // Sync method: add(3, 4) → 7
            int sum = math.Call<int>("add", 3, 4);
            Assert(sum == 7, $"add(3, 4): expected 7, got {sum}");
            Debug.Log($"[SmokeTest] add(3, 4) = {sum} ✓");

            // Async method: addAsync(5, 6) → 11
            double asyncSum = await math.CallAsync<double>("addAsync", 5, 6);
            Assert(
                Math.Abs(asyncSum - 11.0) < 0.001,
                $"addAsync(5, 6): expected 11, got {asyncSum}"
            );
            Debug.Log($"[SmokeTest] addAsync(5, 6) = {asyncSum} ✓");

            // Property read: pi → Math.PI
            double pi = math.Get<double>("pi");
            Assert(Math.Abs(pi - Math.PI) < 1e-10, $"pi: expected {Math.PI}, got {pi}");
            Debug.Log($"[SmokeTest] pi = {pi} ✓");

            // BridgeCallback fan-out + function-handle round-trip via removeListener
            int tickCount = 0;
            using var cb = BridgeCallback.Wrap<int>(tick =>
            {
                tickCount++;
                Debug.Log($"[SmokeTest] tick callback fired: {tick}");
            });

            using var removeListener = await math.CallAsync<JsFunction>("addTickListener", cb);

            // Trigger a tick — the callback should fire once
            await math.CallAsync("invokeTick", 42);
            Assert(tickCount == 1, $"invokeTick: expected 1 callback, got {tickCount}");
            Debug.Log("[SmokeTest] BridgeCallback invoked ✓");

            // Remove the listener via the returned JsFunction handle
            await removeListener.CallAsync();
            Debug.Log("[SmokeTest] removeListener called ✓");

            // Trigger another tick — the callback must NOT fire again
            await math.CallAsync("invokeTick", 99);
            Assert(
                tickCount == 1,
                $"post-remove invokeTick: expected still 1 callback, got {tickCount}"
            );
            Debug.Log("[SmokeTest] post-remove tick no-op ✓");

            // --- V1: DynCall BridgeCallback fires in the same Unity frame as the invocation
            // even when triggered from inside a JS Promise microtask (async JS context).
            int v1CallbackFrame = -1;
            using var v1Cb = BridgeCallback.Wrap<int>(_ => v1CallbackFrame = Time.frameCount);
            using var v1Remove = await math.CallAsync<JsFunction>("addTickListener", v1Cb);
            int v1InvokeFrame = Time.frameCount;
            await math.CallAsync("invokeTickFromPromise", 0);
            Assert(
                v1CallbackFrame == v1InvokeFrame,
                $"V1: async callback frame {v1CallbackFrame} != invoke frame {v1InvokeFrame}"
            );
            await v1Remove.CallAsync();
            Debug.Log($"[SmokeTest] V1 same-frame async callback ✓ (frame {v1CallbackFrame})");

            // --- V2: DllImport callable immediately after an awaited async call.
            // Verifies Awaitable continuation preserves Emscripten call context.
            double v2Async = await math.CallAsync<double>("addAsync", 10, 20);
            int v2Sync = math.Call<int>("add", 7, 3);
            Assert(Math.Abs(v2Async - 30.0) < 0.001, $"V2 async: expected 30, got {v2Async}");
            Assert(v2Sync == 10, $"V2 sync: expected 10, got {v2Sync}");
            Debug.Log("[SmokeTest] V2 DllImport after await ✓");

            // --- V3: BridgeCallback invocations arrive in order under rapid JS emission.
            var v3Received = new List<int>();
            using var v3Cb = BridgeCallback.Wrap<int>(v3Received.Add);
            using var v3Remove = await math.CallAsync<JsFunction>("addTickListener", v3Cb);
            await math.CallAsync("invokeTickN", 5);
            Assert(v3Received.Count == 5, $"V3: expected 5 callbacks, got {v3Received.Count}");
            for (int i = 0; i < 5; i++)
                Assert(v3Received[i] == i, $"V3: position {i} expected {i}, got {v3Received[i]}");
            await v3Remove.CallAsync();
            Debug.Log("[SmokeTest] V3 callback ordering ✓");

            // --- V4: JsFunction returned from a sync method call survives multiple sync calls.
            using var adder = math.Call<JsFunction>("makeAdder", 10);
            int v4r1 = adder.Call<int>(1);
            int v4r2 = adder.Call<int>(2);
            int v4r3 = adder.Call<int>(3);
            Assert(v4r1 == 11, $"V4: expected 11, got {v4r1}");
            Assert(v4r2 == 12, $"V4: expected 12, got {v4r2}");
            Assert(v4r3 == 13, $"V4: expected 13, got {v4r3}");
            Debug.Log("[SmokeTest] V4 JsFunction multiple sync calls ✓");

            Debug.Log("[SmokeTest] All smoke tests passed!");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException($"[SmokeTest] ASSERTION FAILED: {message}");
        }
    }
}

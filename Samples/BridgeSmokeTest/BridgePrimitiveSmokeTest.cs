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

            // --- E1: Double-dispose is idempotent across every handle type.
            // Each handle has its own `_disposed` guard; the second call returns
            // before reaching EL_*Release / CallbackRegistry.TryRemove.
            var e1Object = await JsBridge.InvokeFactoryAsync<JsObject>("mathFactory");
            e1Object.Dispose();
            e1Object.Dispose();
            var e1Function = math.Call<JsFunction>("makeAdder", 100);
            e1Function.Dispose();
            e1Function.Dispose();
            var e1Callback = BridgeCallback.Wrap<int>(_ => { });
            e1Callback.Dispose();
            e1Callback.Dispose();
            Debug.Log("[SmokeTest] E1 double-dispose ✓");

            // --- E2: Operations on orphaned handles surface a clean BridgeException
            // (sync + async paths); JS invocations of a disposed BridgeCallback
            // wrapper miss the C# registry and no-op silently.
            var e2Object = await JsBridge.InvokeFactoryAsync<JsObject>("mathFactory");
            e2Object.Dispose();
            bool e2SyncThrew = false;
            try
            {
                e2Object.Call<int>("add", 1, 2);
            }
            catch (BridgeException)
            {
                e2SyncThrew = true;
            }
            Assert(e2SyncThrew, "E2: disposed JsObject.Call must throw BridgeException");
            bool e2AsyncThrew = false;
            try
            {
                await e2Object.CallAsync<int>("add", 1, 2);
            }
            catch (BridgeException)
            {
                e2AsyncThrew = true;
            }
            Assert(e2AsyncThrew, "E2: disposed JsObject.CallAsync must throw BridgeException");

            var e2Function = math.Call<JsFunction>("makeAdder", 5);
            e2Function.Dispose();
            bool e2FunctionThrew = false;
            try
            {
                e2Function.Call<int>(1);
            }
            catch (BridgeException)
            {
                e2FunctionThrew = true;
            }
            Assert(e2FunctionThrew, "E2: disposed JsFunction.Call must throw BridgeException");

            int e2CallbackHits = 0;
            var e2Callback = BridgeCallback.Wrap<int>(_ => e2CallbackHits++);
            var e2Remove = await math.CallAsync<JsFunction>("addTickListener", e2Callback);
            e2Callback.Dispose();
            await math.CallAsync("invokeTick", 1);
            Assert(
                e2CallbackHits == 0,
                $"E2: disposed callback should not fire (hits={e2CallbackHits})"
            );
            // Drop the orphan wrapper from the JS listeners array so later
            // sections don't pay for dead-handle dispatch.
            await e2Remove.CallAsync();
            e2Remove.Dispose();
            Debug.Log("[SmokeTest] E2 orphaned handles ✓");

            // --- E3: payloads with colons, newlines, quotes, backslashes, and
            // Unicode (BMP + astral) round-trip cleanly across the DynCall
            // payload buffer and the JSON encode/decode boundary.
            string e3Payload = "colon:newline\n\"quote\"\\backslash☃snowman😀emoji";
            string e3SyncResult = math.Call<string>("echo", e3Payload);
            Assert(e3SyncResult == e3Payload, $"E3 sync echo mismatch: got '{e3SyncResult}'");
            string e3AsyncResult = await math.CallAsync<string>("echoAsync", e3Payload);
            Assert(e3AsyncResult == e3Payload, $"E3 async echo mismatch: got '{e3AsyncResult}'");
            Debug.Log("[SmokeTest] E3 special-char payload round-trip ✓");

            // --- E4: 150 KB payloads survive arg-encoding (C#→JS) and
            // return-encoding (JS→C#). Exercises the heap-string + UTF-8 path
            // at sizes well beyond a normal bridge call.
            const int largeSize = 150 * 1024;
            string e4Outbound = new string('A', largeSize);
            string e4Inbound = math.Call<string>("echo", e4Outbound);
            Assert(
                e4Inbound.Length == largeSize && e4Inbound == e4Outbound,
                $"E4 echo length mismatch: expected {largeSize}, got {e4Inbound.Length}"
            );
            string e4JsGenerated = math.Call<string>("bigString", largeSize);
            Assert(
                e4JsGenerated.Length == largeSize,
                $"E4 bigString length mismatch: expected {largeSize}, got {e4JsGenerated.Length}"
            );
            Debug.Log($"[SmokeTest] E4 large payload ({largeSize} B) round-trip ✓");

            // --- E5: 1000 callbacks fired in a single sync JS loop arrive in
            // C# in order and none are dropped. Pushes the rapid-fire path
            // well past V3's 5-callback baseline.
            var e5Received = new List<int>();
            using var e5Cb = BridgeCallback.Wrap<int>(e5Received.Add);
            using var e5Remove = await math.CallAsync<JsFunction>("addTickListener", e5Cb);
            const int e5Count = 1000;
            await math.CallAsync("invokeTickN", e5Count);
            Assert(
                e5Received.Count == e5Count,
                $"E5 callback count: expected {e5Count}, got {e5Received.Count}"
            );
            for (int i = 0; i < e5Count; i++)
                Assert(e5Received[i] == i, $"E5 position {i} expected {i}, got {e5Received[i]}");
            await e5Remove.CallAsync();
            Debug.Log($"[SmokeTest] E5 rapid-fire {e5Count} callbacks ✓");

            Debug.Log("[SmokeTest] All smoke tests passed!");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException($"[SmokeTest] ASSERTION FAILED: {message}");
        }
    }
}

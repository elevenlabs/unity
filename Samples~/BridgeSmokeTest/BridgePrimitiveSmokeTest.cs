using System;
using System.Runtime.InteropServices;
using ElevenLabs.WebGL;
using UnityEngine;

namespace ElevenLabs.WebGL.Samples
{
    /// <summary>
    /// WebGL smoke-test MonoBehaviour that exercises every bridge primitive surface
    /// end-to-end against the mathFactory registered by BridgePrimitiveSmokeTest.jslib.
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

            Debug.Log("[SmokeTest] All smoke tests passed!");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException($"[SmokeTest] ASSERTION FAILED: {message}");
        }
    }
}

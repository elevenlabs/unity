using System;
using System.Runtime.InteropServices;
using AOT;
using ElevenLabs.WebGL.Internal;
using UnityEngine;

namespace ElevenLabs.WebGL
{
    /// <summary>
    /// Static JS→C# dispatch surface for the WebGL bridge. Holds two function pointers
    /// (promise-settle, callback-invoke) that the JS side calls synchronously via
    /// <c>{{{ makeDynCall('sig', 'fnVar') }}}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IL2CPP rejects generic delegates (<c>Action&lt;...&gt;</c>) decorated with
    /// <see cref="MonoPInvokeCallbackAttribute"/> — the marshaller silently emits a
    /// trampoline that misbehaves under stripping. The non-generic
    /// <see cref="SettleCallback"/> and <see cref="InvokeCallback"/> delegate types
    /// are mandatory.
    /// </para>
    /// <para>
    /// The two static delegate fields below keep the references alive for the
    /// lifetime of the player. If the delegate backing a registered function pointer
    /// were collected, calling the pointer would crash opaquely.
    /// </para>
    /// </remarks>
    internal static class BridgeStaticCallbacks
    {
        internal delegate void SettleCallback(int promiseId, int statusCode, IntPtr payloadPtr);

        internal delegate void InvokeCallback(int handle, IntPtr payloadPtr);

        private static SettleCallback _settleCallback;
        private static InvokeCallback _invokeCallback;
        private static bool _initialized;

        /// <summary>
        /// Registers both function pointers with the JS side before any user code can
        /// reach the bridge. Runs at <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>
        /// — earlier than <c>BeforeSceneLoad</c>, so initial scene scripts cannot fire
        /// a bridge call into an unregistered callback.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;

#if UNITY_WEBGL && !UNITY_EDITOR
            _settleCallback = OnSettleFromJs;
            _invokeCallback = OnCallbackInvokedFromJs;

            if (ElevenLabsBridgeNative.EL_ProbeWasmTable() == 0)
            {
                BridgeLog.Warn(
                    "WebAssembly.Table probe returned 0. The bridge requires Player Settings → "
                        + "WebGL → Publishing Settings → Use WebAssembly.Table to be enabled. "
                        + "Continuing — the next DynCall will surface the authoritative error if "
                        + "the setting is actually off."
                );
            }

            try
            {
                ElevenLabsBridgeNative.EL_SetSettleCallback(
                    Marshal.GetFunctionPointerForDelegate(_settleCallback)
                );
                ElevenLabsBridgeNative.EL_SetInvokeCallbackPtr(
                    Marshal.GetFunctionPointerForDelegate(_invokeCallback)
                );
            }
            catch (Exception ex) when (IsReferenceError(ex))
            {
                throw new BridgeException(
                    "ElevenLabs WebGL bridge failed to register its JS→C# callbacks. "
                        + "Enable Player Settings → WebGL → Publishing Settings → Use WebAssembly.Table "
                        + "and rebuild.",
                    ex
                );
            }

            Application.quitting += OnApplicationQuitting;
#endif
        }

        [MonoPInvokeCallback(typeof(SettleCallback))]
        internal static void OnSettleFromJs(int promiseId, int statusCode, IntPtr payloadPtr)
        {
            // The JS side `_free`s the buffer the moment this DynCall returns, so the
            // payload must be copied out synchronously — never captured into a continuation.
            string payload = payloadPtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(payloadPtr);
            PromiseRegistry.TrySettle(promiseId, ok: statusCode == 0, payload);
        }

        [MonoPInvokeCallback(typeof(InvokeCallback))]
        private static void OnCallbackInvokedFromJs(int handle, IntPtr payloadPtr)
        {
            string payload = payloadPtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(payloadPtr);
            CallbackRegistry.TryDispatch(handle, payload);
        }

        private static void OnApplicationQuitting()
        {
            // Best-effort teardown — Application.quitting is not raised on tab close,
            // navigation, or crash in WebGL builds. Acceptable because the wasm heap
            // dies with the page.
            _settleCallback = null;
            _invokeCallback = null;
        }

        private static bool IsReferenceError(Exception ex)
        {
            // Closure Compiler can rename the underlying symbols, so the type name is
            // not reliable. Fall back to a message substring match.
            return ex.GetType().Name.Contains("ReferenceError", StringComparison.Ordinal)
                || (
                    ex.Message != null
                    && ex.Message.Contains("ReferenceError", StringComparison.Ordinal)
                );
        }
    }
}

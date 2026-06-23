using System;
using System.Runtime.InteropServices;

namespace ElevenLabs.WebGL
{
    /// <summary>
    /// Raw DllImport declarations for the ElevenLabs WebGL bridge entry points. On non-WebGL
    /// platforms every method throws <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    internal static class ElevenLabsBridgeNative
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        public static extern void EL_Free(IntPtr ptr);

        [DllImport("__Internal")]
        public static extern void EL_SetSettleCallback(IntPtr functionPointer);

        [DllImport("__Internal")]
        public static extern void EL_SetInvokeCallbackPtr(IntPtr functionPointer);

        [DllImport("__Internal")]
        public static extern int EL_ProbeWasmTable();

        [DllImport("__Internal")]
        public static extern void EL_InvokeFactoryAsync(
            string factoryName,
            string argsJson,
            int returnShape,
            int promiseId
        );

        [DllImport("__Internal")]
        public static extern IntPtr EL_InvokeFactorySync(
            string factoryName,
            string argsJson,
            int returnShape
        );

        [DllImport("__Internal")]
        public static extern void EL_ObjectCallAsync(
            int handle,
            string method,
            string argsJson,
            int returnShape,
            int promiseId
        );

        [DllImport("__Internal")]
        public static extern IntPtr EL_ObjectCallSync(
            int handle,
            string method,
            string argsJson,
            int returnShape
        );

        [DllImport("__Internal")]
        public static extern IntPtr EL_ObjectGet(int handle, string property);

        [DllImport("__Internal")]
        public static extern void EL_ObjectRelease(int handle);

        [DllImport("__Internal")]
        public static extern void EL_FunctionCallAsync(
            int handle,
            string argsJson,
            int returnShape,
            int promiseId
        );

        [DllImport("__Internal")]
        public static extern IntPtr EL_FunctionCallSync(
            int handle,
            string argsJson,
            int returnShape
        );

        [DllImport("__Internal")]
        public static extern void EL_FunctionRelease(int handle);

        // No-op DllImport whose only purpose is to keep the connection-side
        // .jslib's `$EL_ConnectionInit` library entry alive across Emscripten's
        // dead-code pass. `$EL_ConnectionInit` is the single library variable
        // that carries the bundled @elevenlabs/client SDK and the
        // EL_RegisterFactory calls that wire the factories into the
        // primitives dispatcher (see Bridge~/build/bundle-jslib.ts postset
        // mode). Without a DllImport reference here, the entry — and its
        // bundled payload — gets stripped and the first StartSessionAsync
        // fails with "Unknown factory: createWebSocketConnection".
        [DllImport("__Internal")]
        public static extern void EL_EnsureConnectionFactoriesLoaded();
#else
        public static void EL_Free(IntPtr ptr) { }

        public static void EL_SetSettleCallback(IntPtr functionPointer) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static void EL_SetInvokeCallbackPtr(IntPtr functionPointer) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static int EL_ProbeWasmTable() =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static void EL_InvokeFactoryAsync(
            string factoryName,
            string argsJson,
            int returnShape,
            int promiseId
        ) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static IntPtr EL_InvokeFactorySync(
            string factoryName,
            string argsJson,
            int returnShape
        ) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static void EL_ObjectCallAsync(
            int handle,
            string method,
            string argsJson,
            int returnShape,
            int promiseId
        ) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static IntPtr EL_ObjectCallSync(
            int handle,
            string method,
            string argsJson,
            int returnShape
        ) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static IntPtr EL_ObjectGet(int handle, string property) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static void EL_ObjectRelease(int handle) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static void EL_FunctionCallAsync(
            int handle,
            string argsJson,
            int returnShape,
            int promiseId
        ) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static IntPtr EL_FunctionCallSync(int handle, string argsJson, int returnShape) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static void EL_FunctionRelease(int handle) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

        public static void EL_EnsureConnectionFactoriesLoaded() =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );
#endif
    }
}

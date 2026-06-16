using System;
using System.Runtime.InteropServices;

namespace ElevenLabs.WebGL
{
    internal static class ElevenLabsBridgeNative
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        public static extern void EL_SetBridgeName(string name);

        [DllImport("__Internal")]
        public static extern void EL_SetSettleCallback(IntPtr functionPointer);

        [DllImport("__Internal")]
        public static extern void EL_SetInvokeCallbackPtr(IntPtr functionPointer);

        [DllImport("__Internal")]
        public static extern int EL_ProbeWasmTable();
#else
        public static void EL_SetBridgeName(string name) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );

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
#endif
    }
}

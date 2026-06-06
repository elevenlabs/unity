using System;
using System.Runtime.InteropServices;

namespace ElevenLabs.WebGL
{
    internal static class ElevenLabsBridgeNative
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        public static extern void EL_SetBridgeName(string name);
#else
        public static void EL_SetBridgeName(string name) =>
            throw new PlatformNotSupportedException(
                "WebGL bridge is not available outside WebGL builds."
            );
#endif
    }
}

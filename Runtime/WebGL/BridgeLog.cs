using UnityEngine;

namespace ElevenLabs.WebGL
{
    internal static class BridgeLog
    {
        private const string Prefix = "[ElevenLabs Bridge]";

        public static void Info(string msg) => Debug.Log($"{Prefix} {msg}");

        public static void Warn(string msg) => Debug.LogWarning($"{Prefix} {msg}");

        public static void Error(string msg) => Debug.LogError($"{Prefix} {msg}");
    }
}

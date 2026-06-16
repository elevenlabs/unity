using UnityEngine;

namespace ElevenLabs.WebGL
{
    /// <summary>Tagged logger that prefixes every message with <c>[ElevenLabs Bridge]</c>.</summary>
    internal static class BridgeLog
    {
        private const string Prefix = "[ElevenLabs Bridge]";

        /// <summary>Logs an informational message to the Unity console.</summary>
        public static void Info(string msg) => Debug.Log($"{Prefix} {msg}");

        /// <summary>Logs a warning message to the Unity console.</summary>
        public static void Warn(string msg) => Debug.LogWarning($"{Prefix} {msg}");

        /// <summary>Logs an error message to the Unity console.</summary>
        public static void Error(string msg) => Debug.LogError($"{Prefix} {msg}");
    }
}

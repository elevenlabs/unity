#nullable enable

using ElevenLabs.Agents;
using UnityEngine;

namespace ElevenLabs.Native.Samples.StandaloneSmokeTest
{
    /// <summary>
    /// Per-developer / per-CI agent credentials for the native standalone
    /// smoke test. Place the asset at
    /// <c>Assets/Resources/StandaloneSmokeConfig.asset</c> so
    /// <see cref="Resources.Load{T}(string)"/> can find it at runtime; the
    /// asset itself is gitignored so secrets never reach the repository.
    /// </summary>
    /// <remarks>
    /// When the asset is missing or <see cref="AgentId"/> is empty, the
    /// smoke MonoBehaviour logs <c>[StandaloneSmoke] CONFIG MISSING</c> and
    /// exits cleanly. A fresh clone without secrets therefore still builds
    /// and runs to completion — only the real-agent assertion is skipped.
    /// </remarks>
    [CreateAssetMenu(
        menuName = "ElevenLabs/Standalone Smoke Config",
        fileName = "StandaloneSmokeConfig"
    )]
    public sealed class StandaloneSmokeConfig : ScriptableObject
    {
        [SerializeField]
        [Tooltip("Public agent identifier from elevenlabs.io. Required.")]
        private string agentId = "";

        [SerializeField]
        [Tooltip("Optional pre-signed WebSocket URL for private agents.")]
        private string signedUrl = "";

        [SerializeField]
        [Tooltip(
            "Prompt sent as a text-only user message after the session opens. "
                + "The agent's reply is logged and asserted non-empty."
        )]
        private string prompt = "Hello, please reply with the word READY and stop.";

        public string AgentId => agentId;
        public string? SignedUrl => string.IsNullOrWhiteSpace(signedUrl) ? null : signedUrl;
        public string Prompt => prompt;
    }
}

#nullable enable

using ElevenLabs.Agents;
using UnityEngine;

namespace ElevenLabs.Agents.Samples.QuickStart
{
    /// <summary>
    /// Per-developer agent credentials for the QuickStart sample. Create via
    /// <b>Assets → Create → ElevenLabs → Samples → QuickStart Config</b> and
    /// place the asset under any folder named <c>Resources</c> in your project
    /// (e.g. <c>Assets/Resources/QuickStartConfig.asset</c>) so the sample's
    /// <see cref="Resources.Load{T}(string)"/> can find it at runtime.
    /// </summary>
    /// <remarks>
    /// The asset is gitignored from this repository — every developer fills in
    /// their own agent id, and nothing reaches the upstream tree. The sample
    /// logs a clear message and exits cleanly when the asset is missing so
    /// importing the sample on a fresh project remains low-friction.
    /// </remarks>
    [CreateAssetMenu(
        menuName = "ElevenLabs/Samples/QuickStart Config",
        fileName = "QuickStartConfig"
    )]
    public sealed class QuickStartConfig : ScriptableObject
    {
        [SerializeField]
        [Tooltip("Public agent identifier from elevenlabs.io. Required for public agents.")]
        private string agentId = "";

        [SerializeField]
        [Tooltip(
            "Optional pre-signed WebSocket URL for private agents. "
                + "When set, takes precedence over agentId at session start."
        )]
        private string signedUrl = "";

        [SerializeField]
        [Tooltip("Transport selection. Defaults to WebSocket.")]
        private ConnectionType connectionType = ConnectionType.WebSocket;

        public string AgentId => agentId;
        public string? SignedUrl => string.IsNullOrWhiteSpace(signedUrl) ? null : signedUrl;
        public ConnectionType ConnectionType => connectionType;
    }
}

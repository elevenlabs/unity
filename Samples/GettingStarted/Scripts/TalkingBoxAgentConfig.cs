#nullable enable

using UnityEngine;

namespace ElevenLabs.Agents.Samples.GettingStarted
{
    /// <summary>
    /// Per-developer agent credentials for the GettingStarted sample. Create via
    /// <b>Assets → Create → ElevenLabs → Samples → Talking Box Agent Config</b>
    /// and place the asset under any folder named <c>Resources</c> in your
    /// project (e.g. <c>Assets/Resources/TalkingBoxAgentConfig.asset</c>) so the
    /// sample's <see cref="Resources.Load{T}(string)"/> call can find it at
    /// runtime.
    /// </summary>
    /// <remarks>
    /// The asset is gitignored from this repository — every developer fills in
    /// their own agent id, and nothing reaches the upstream tree. The sample
    /// logs a clear error and skips opening a session when the asset is missing
    /// so importing the sample on a fresh project remains low-friction.
    /// </remarks>
    [CreateAssetMenu(
        menuName = "ElevenLabs/Samples/Talking Box Agent Config",
        fileName = "TalkingBoxAgentConfig"
    )]
    public sealed class TalkingBoxAgentConfig : ScriptableObject
    {
        [SerializeField]
        [Tooltip("Public ElevenLabs agent ID. Authentication must be disabled in the dashboard.")]
        private string agentId = "";

        public string AgentId => agentId;
    }
}

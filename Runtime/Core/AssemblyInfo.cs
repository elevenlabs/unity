using System.Runtime.CompilerServices;
using ElevenLabs.Protocol;

// Platform-specific implementations of IConnection / IInputController /
// IOutputController live in sibling assemblies that need to see the
// internal contracts.
[assembly: InternalsVisibleTo("ElevenLabs.Agents.WebGL")]
[assembly: InternalsVisibleTo("ElevenLabs.Agents.WebGL.Tests")]
[assembly: InternalsVisibleTo("ElevenLabs.Agents.Native")]
[assembly: InternalsVisibleTo("ElevenLabs.Agents.Native.Tests")]

namespace ElevenLabs.Agents
{
    /// <summary>
    /// How this SDK identifies itself to the server, as the
    /// <c>source_info</c> of every conversation it starts. Both transports
    /// read it: the native one puts it on the initiation event, the WebGL one
    /// hands it to the bundled JS client.
    /// </summary>
    internal static class SdkInfo
    {
        internal const string Source = "unity_sdk";

        /// <summary>
        /// Must match <c>version</c> in the package's <c>package.json</c>.
        /// Unity has no runtime API for a package's version, so it is
        /// duplicated here; an Edit Mode test fails when the two drift.
        /// </summary>
        internal const string Version = "0.1.0";

        internal static SourceInfo ToSourceInfo() => new() { Source = Source, Version = Version };
    }
}

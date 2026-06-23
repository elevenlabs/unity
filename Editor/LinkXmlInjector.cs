#nullable enable

using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;

namespace ElevenLabs.WebGL.Editor
{
    /// <summary>
    /// Explicitly hands <c>Runtime/Native/link.xml</c> to UnityLinker via the
    /// <see cref="IUnityLinkerProcessor"/> build callback. Auto-discovery of
    /// package-internal link.xml files is unreliable in Unity 6 when the
    /// package is referenced as a local file-path package whose root happens
    /// to be the repository root (manifest.json: <c>"file:../.."</c>) —
    /// observed by IL2CPP standalone builds stripping the
    /// <see cref="ElevenLabs.Protocol.ConversationInitiationMetadata"/>
    /// default constructor despite a valid link.xml under
    /// <c>Runtime/Native/</c>. The explicit callback sidesteps the discovery
    /// path entirely.
    /// </summary>
    /// <remarks>
    /// Runs on every player build (not gated on platform): the link.xml only
    /// preserves types the Native and WebGL transports both reflect through
    /// Newtonsoft.Json, so keeping them under both pipelines is harmless and
    /// avoids a second copy under <c>Runtime/WebGL/</c>.
    /// </remarks>
    public sealed class LinkXmlInjector : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(
            BuildReport report,
            UnityLinkerBuildPipelineData data
        )
        {
            string projectRoot = Directory.GetCurrentDirectory();
            // Walk up from the embedded TestProject (or any consumer host
            // project) to the package directory. We resolve the absolute
            // path so the linker doesn't choke on a Windows backslash /
            // Mac forward-slash mismatch when the project lives on a
            // different drive than the package.
            string path = Path.GetFullPath(
                Path.Combine(
                    projectRoot,
                    "Packages",
                    "io.elevenlabs.agents",
                    "Runtime",
                    "Native",
                    "link.xml"
                )
            );
            if (File.Exists(path))
                return path;
            // Local-dev / in-repo invocation: TestProject lives inside the
            // package directory, so the file is one level up from
            // Application.dataPath instead of routed through the package
            // cache.
            string repoLocal = Path.GetFullPath(
                Path.Combine(projectRoot, "..", "Runtime", "Native", "link.xml")
            );
            return File.Exists(repoLocal) ? repoLocal : string.Empty;
        }
    }
}

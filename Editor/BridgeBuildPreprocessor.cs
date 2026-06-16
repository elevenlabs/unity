using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace ElevenLabs.WebGL.Editor
{
    /// <summary>
    /// Validates that <c>Use WebAssembly.Table</c> is enabled before a WebGL build starts.
    /// </summary>
    /// <remarks>
    /// The ElevenLabs bridge dispatches JS→C# calls via the
    /// <c>{{{ makeDynCall(...) }}}</c> Emscripten macro. With <c>Use WebAssembly.Table</c>
    /// disabled the macro falls back to <c>getWasmTableEntry</c>, which Closure Compiler
    /// can rename on release builds, producing a runtime <c>ReferenceError</c> on the
    /// first bridge call. This preprocessor catches the misconfiguration at build time,
    /// not in production.
    /// </remarks>
    public sealed class BridgeBuildPreprocessor : IPreprocessBuildWithReport
    {
        internal const string FixInstructions =
            "Player Settings → WebGL → Publishing Settings → Use WebAssembly.Table";

        /// <inheritdoc />
        public int callbackOrder => 0;

        /// <inheritdoc />
        public void OnPreprocessBuild(BuildReport report) => Validate(report.summary.platform);

        internal static void Validate(BuildTarget target)
        {
            if (target != BuildTarget.WebGL)
                return;

            if (!PlayerSettings.WebGL.webAssemblyTable)
                throw new BuildFailedException(
                    $"ElevenLabs Unity SDK requires 'Use WebAssembly.Table' to be enabled. "
                        + $"Go to: {FixInstructions}"
                );
        }
    }
}

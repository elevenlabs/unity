using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using NamedBuildTarget = UnityEditor.Build.NamedBuildTarget;

namespace ElevenLabs.WebGL.Editor
{
    public static class HostBuild
    {
        /// <summary>
        /// Entry point for headless WebGL primitives smoke builds
        /// (<c>-executeMethod ElevenLabs.WebGL.Editor.HostBuild.Build</c>).
        /// </summary>
        public static void Build()
        {
            BuildSceneAndExit(
                "ElevenLabs.WebGL.Samples.BridgePrimitiveSmokeTest, ElevenLabs.WebGL.Samples.BridgeSmokeTest",
                "WebGL",
                BuildTarget.WebGL
            );
        }

        /// <summary>
        /// Entry point for headless WebGL conversation smoke builds
        /// (<c>-executeMethod ElevenLabs.WebGL.Editor.HostBuild.BuildConversation</c>).
        /// Output lands in a sibling directory so the primitives build stays
        /// untouched and both harness tests can run against their own artifact.
        /// </summary>
        public static void BuildConversation()
        {
            BuildSceneAndExit(
                "ElevenLabs.WebGL.Samples.ConversationSmokeTest.ConversationSmokeTest, ElevenLabs.WebGL.Samples.ConversationSmokeTest",
                "WebGLConversationSmoke",
                BuildTarget.WebGL
            );
        }

        /// <summary>
        /// Entry point for the spatial-output variant of the conversation
        /// smoke (<c>-executeMethod ElevenLabs.WebGL.Editor.HostBuild.BuildConversationSpatial</c>).
        /// Drives the same JS bridge but with an
        /// <see cref="ConversationOptions.OutputAudioSource"/> wired up so the
        /// integration test can introspect the Web Audio graph.
        /// </summary>
        public static void BuildConversationSpatial()
        {
            BuildSceneAndExit(
                "ElevenLabs.WebGL.Samples.ConversationSmokeTest.ConversationSpatialSmokeTest, ElevenLabs.WebGL.Samples.ConversationSmokeTest",
                "WebGLConversationSpatialSmoke",
                BuildTarget.WebGL
            );
        }

        /// <summary>
        /// Entry point for headless desktop standalone smoke builds with
        /// IL2CPP + managed stripping enabled
        /// (<c>-executeMethod ElevenLabs.WebGL.Editor.HostBuild.BuildStandalone</c>).
        /// Builds for the host OS so the same shell script works on every
        /// developer / CI machine without a per-OS branch. The produced
        /// binary exercises <see cref="ElevenLabs.Protocol"/> Newtonsoft
        /// reflection under AOT and gates the
        /// <c>Runtime/Native/link.xml</c> preservation.
        /// </summary>
        public static void BuildStandalone()
        {
            var hostTarget = HostStandaloneTarget();
            var hostGroup = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(hostTarget)
            );

            // IL2CPP + High stripping is the worst-case AOT shape — if the
            // native link.xml ever drifts from the codegen output, the
            // smoke surfaces it as a runtime JsonReaderException instead
            // of a vague consumer-side bug report. Restore the pre-call
            // settings so a developer running this on their workstation
            // doesn't end up with their project silently flipped to IL2CPP.
            ScriptingImplementation previousBackend = PlayerSettings.GetScriptingBackend(hostGroup);
            ManagedStrippingLevel previousStripping = PlayerSettings.GetManagedStrippingLevel(
                hostGroup
            );
            // Unity's FeatureExtractor refuses to build when a referenced
            // assembly uses the Microphone class without a usage description
            // string — UnityMicrophoneInput is pulled in via NativeSessionLauncher,
            // so the smoke must declare one even though the binary may not
            // actually open a mic before sending its first text message.
            string previousMacMicUsage = PlayerSettings.macOS.microphoneUsageDescription;

            BuildResult result;
            try
            {
                PlayerSettings.SetScriptingBackend(hostGroup, ScriptingImplementation.IL2CPP);
                PlayerSettings.SetManagedStrippingLevel(hostGroup, ManagedStrippingLevel.High);
                PlayerSettings.macOS.microphoneUsageDescription =
                    "ElevenLabs standalone smoke test exercises the native session transport.";

                result = BuildSceneWithBehaviour(
                    "ElevenLabs.Native.Samples.StandaloneSmokeTest.StandaloneSmokeTest, ElevenLabs.Native.Samples.StandaloneSmokeTest",
                    System.IO.Path.Combine("Standalone", "StandaloneSmoke" + StandaloneExtension()),
                    hostTarget
                );
            }
            finally
            {
                // Restore before Exit so the developer's ProjectSettings.asset
                // isn't dirtied with our build-time IL2CPP / stripping / mic
                // description overrides. SaveAssets persists the restored
                // values; otherwise Unity flushes the dirty in-memory values
                // during shutdown and the file ends up modified anyway.
                PlayerSettings.SetScriptingBackend(hostGroup, previousBackend);
                PlayerSettings.SetManagedStrippingLevel(hostGroup, previousStripping);
                PlayerSettings.macOS.microphoneUsageDescription = previousMacMicUsage;
                AssetDatabase.SaveAssets();
            }

            EditorApplication.Exit(result == BuildResult.Succeeded ? 0 : 1);
        }

        private static BuildTarget HostStandaloneTarget()
        {
            return Application.platform switch
            {
                RuntimePlatform.OSXEditor => BuildTarget.StandaloneOSX,
                RuntimePlatform.WindowsEditor => BuildTarget.StandaloneWindows64,
                RuntimePlatform.LinuxEditor => BuildTarget.StandaloneLinux64,
                _ => throw new PlatformNotSupportedException(
                    $"HostBuild.BuildStandalone does not support {Application.platform}"
                ),
            };
        }

        private static string StandaloneExtension()
        {
            return Application.platform switch
            {
                RuntimePlatform.OSXEditor => ".app",
                RuntimePlatform.WindowsEditor => ".exe",
                RuntimePlatform.LinuxEditor => "",
                _ => "",
            };
        }

        // Shared scene-build pipeline: assemble an empty scene with the named
        // MonoBehaviour as its only object, run BuildPipeline.BuildPlayer
        // targeting the requested platform, exit 0 on success / 1 on failure.
        // Returns the BuildResult so callers can run cleanup before exiting.
        private static BuildResult BuildSceneWithBehaviour(
            string assemblyQualifiedTypeName,
            string outputSubdir,
            BuildTarget target
        )
        {
            var testProjectRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..")
            );
            var outputPath = System.IO.Path.Combine(testProjectRoot, "Build", outputSubdir);

            const string tmpScenePath = "Assets/SmokeBuildScene.unity";
            var tmpScene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single
            );

            var behaviourType = Type.GetType(assemblyQualifiedTypeName);
            if (behaviourType == null)
                throw new InvalidOperationException(
                    $"Type '{assemblyQualifiedTypeName}' not found. "
                        + "Ensure its sample assembly compiled successfully."
                );

            var go = new GameObject(behaviourType.Name);
            go.AddComponent(behaviourType);
            EditorSceneManager.SaveScene(tmpScene, tmpScenePath);

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(
                    new BuildPlayerOptions
                    {
                        scenes = new[] { tmpScenePath },
                        locationPathName = outputPath,
                        target = target,
                        options = BuildOptions.None,
                    }
                );
            }
            finally
            {
                AssetDatabase.DeleteAsset(tmpScenePath);
            }

            if (report.summary.result == BuildResult.Succeeded)
            {
                Debug.Log(
                    $"Smoke build succeeded ({outputSubdir}) — errors: {report.summary.totalErrors}, warnings: {report.summary.totalWarnings}"
                );
            }
            else
            {
                Debug.LogError($"Smoke build failed ({outputSubdir}): {report.summary.result}");
            }
            return report.summary.result;
        }

        // Wrap BuildSceneWithBehaviour for the two WebGL entry points: they
        // don't need post-build cleanup, so we exit immediately afterward.
        private static void BuildSceneAndExit(
            string assemblyQualifiedTypeName,
            string outputSubdir,
            BuildTarget target
        )
        {
            BuildResult result = BuildSceneWithBehaviour(
                assemblyQualifiedTypeName,
                outputSubdir,
                target
            );
            EditorApplication.Exit(result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}

using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

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
            BuildSceneWithBehaviour(
                "ElevenLabs.WebGL.Samples.BridgePrimitiveSmokeTest, ElevenLabs.WebGL.Samples.BridgeSmokeTest",
                "WebGL"
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
            BuildSceneWithBehaviour(
                "ElevenLabs.WebGL.Samples.ConversationSmokeTest.ConversationSmokeTest, ElevenLabs.WebGL.Samples.ConversationSmokeTest",
                "WebGLConversationSmoke"
            );
        }

        // Shared scene-build pipeline: assemble an empty scene with the named
        // MonoBehaviour as its only object, run BuildPipeline.BuildPlayer
        // targeting WebGL, exit 0 on success / 1 on failure.
        private static void BuildSceneWithBehaviour(
            string assemblyQualifiedTypeName,
            string outputSubdir
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
                        target = BuildTarget.WebGL,
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
                    $"WebGL smoke build succeeded ({outputSubdir}) — errors: {report.summary.totalErrors}, warnings: {report.summary.totalWarnings}"
                );
                EditorApplication.Exit(0);
            }
            else
            {
                Debug.LogError(
                    $"WebGL smoke build failed ({outputSubdir}): {report.summary.result}"
                );
                EditorApplication.Exit(1);
            }
        }
    }
}

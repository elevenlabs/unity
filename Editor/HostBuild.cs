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
        /// Entry point for headless WebGL smoke builds (-executeMethod ElevenLabs.WebGL.Editor.HostBuild.Build).
        /// Creates a temporary scene with the BridgePrimitiveSmokeTest MonoBehaviour (which is always
        /// compiled as part of the package via Samples/BridgeSmokeTest/), runs BuildPipeline.BuildPlayer
        /// for WebGL, then exits with code 0 on success or 1 on failure.
        /// </summary>
        public static void Build()
        {
            var testProjectRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..")
            );
            var outputPath = System.IO.Path.Combine(testProjectRoot, "Build", "WebGL");

            const string tmpScenePath = "Assets/SmokeBuildScene.unity";
            var tmpScene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single
            );

            var smokeTestType = Type.GetType(
                "ElevenLabs.WebGL.Samples.BridgePrimitiveSmokeTest, ElevenLabs.WebGL.Samples.BridgeSmokeTest"
            );
            if (smokeTestType == null)
                throw new InvalidOperationException(
                    "BridgePrimitiveSmokeTest type not found in ElevenLabs.WebGL.Samples.BridgeSmokeTest. "
                        + "Ensure the Samples/BridgeSmokeTest/ assembly compiled successfully."
                );

            var go = new GameObject("BridgePrimitiveSmokeTest");
            go.AddComponent(smokeTestType);
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
                    $"WebGL smoke build succeeded — errors: {report.summary.totalErrors}, warnings: {report.summary.totalWarnings}"
                );
                EditorApplication.Exit(0);
            }
            else
            {
                Debug.LogError($"WebGL smoke build failed: {report.summary.result}");
                EditorApplication.Exit(1);
            }
        }
    }
}

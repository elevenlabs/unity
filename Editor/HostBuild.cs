using System.IO;
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
        /// Creates a temporary empty scene, runs BuildPipeline.BuildPlayer for WebGL,
        /// then exits with code 0 on success or 1 on failure.
        /// </summary>
        public static void Build()
        {
            // Application.dataPath = TestProject/Assets; two levels up is the repo root.
            var repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            var outputPath = Path.Combine(repoRoot, "TestProject", "Build", "WebGL");

            // BuildPlayer requires at least one scene; create a temporary empty scene.
            const string tmpScenePath = "Assets/SmokeBuildScene.unity";
            var tmpScene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single
            );
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

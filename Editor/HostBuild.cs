using System;
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
        /// Imports the BridgeSmokeTest sample into Assets/, creates a scene with the
        /// BridgePrimitiveSmokeTest MonoBehaviour, runs BuildPipeline.BuildPlayer for WebGL,
        /// then exits with code 0 on success or 1 on failure.
        /// </summary>
        public static void Build()
        {
            // Application.dataPath = TestProject/Assets; two levels up is the TestProject root,
            // three levels up is the repo root where the package lives.
            var testProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var repoRoot = Path.GetFullPath(Path.Combine(testProjectRoot, "..", ".."));
            var outputPath = Path.Combine(testProjectRoot, "Build", "WebGL");

            // Import the BridgeSmokeTest sample into Assets/ so Unity compiles it.
            var sampleSrc = Path.Combine(repoRoot, "Samples~", "BridgeSmokeTest");
            const string sampleDst = "Assets/BridgeSmokeTest";
            var sampleDstAbs = Path.Combine(Application.dataPath, "BridgeSmokeTest");
            ImportSample(sampleSrc, sampleDstAbs);
            AssetDatabase.Refresh(); // Blocks until recompile in batchmode.

            const string tmpScenePath = "Assets/SmokeBuildScene.unity";
            var tmpScene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single
            );

            // Instantiate BridgePrimitiveSmokeTest on a GameObject in the scene.
            var smokeTestType = Type.GetType(
                "ElevenLabs.WebGL.Samples.BridgePrimitiveSmokeTest, Assembly-CSharp"
            );
            if (smokeTestType == null)
                throw new InvalidOperationException(
                    "BridgePrimitiveSmokeTest type not found. "
                        + "Ensure BridgeSmokeTest sample compiled successfully."
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
                AssetDatabase.DeleteAsset(sampleDst);
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

        private static void ImportSample(string srcDir, string dstDir)
        {
            if (!Directory.Exists(dstDir))
                Directory.CreateDirectory(dstDir);

            foreach (var srcFile in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var relative = srcFile
                    .Substring(srcDir.Length)
                    .TrimStart(Path.DirectorySeparatorChar);
                var dstFile = Path.Combine(dstDir, relative);
                var dstParent = Path.GetDirectoryName(dstFile);
                if (dstParent != null && !Directory.Exists(dstParent))
                    Directory.CreateDirectory(dstParent);
                File.Copy(srcFile, dstFile, overwrite: true);
            }
        }
    }
}

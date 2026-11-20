using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ReproBuild
{
    public static void Build()
    {
        const string scenePath = "Assets/Repro.unity";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Repro").AddComponent<Repro>();
        EditorSceneManager.SaveScene(scene, scenePath);

        bool ok =
            BuildWith(ScriptingImplementation.Mono2x, "Build/mono/Repro", scenePath)
            & BuildWith(ScriptingImplementation.IL2CPP, "Build/il2cpp/Repro", scenePath);
        EditorApplication.Exit(ok ? 0 : 1);
    }

    private static bool BuildWith(ScriptingImplementation backend, string output, string scene)
    {
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);
        BuildReport report = BuildPipeline.BuildPlayer(
            new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = output,
                target = BuildTarget.StandaloneLinux64,
                options = BuildOptions.None,
            }
        );
        Debug.Log($"REPRO BUILD {backend}: {report.summary.result}");
        return report.summary.result == BuildResult.Succeeded;
    }
}

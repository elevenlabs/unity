# Linux players crash or hang at startup without an X display, even with `-batchmode -nographics`

**Unity version:** 6000.3.6f1 (Unity 6 LTS).
**Platform reproduced:** Linux x86_64 standalone players, Mono and IL2CPP.
Reproduced on GitHub Actions `ubuntu-latest` (Ubuntu 26.04) and
`ubuntu-24.04` runners, and in a local `ubuntu:24.04` amd64 container.
**Severity:** Fatal for headless use. With `-nographics` the player
segfaults before the first scene's scripts run. Without it, the player
hangs and ignores `SIGTERM`.
**Discovered:** 2026-11-20 while adding a CI job that runs an IL2CPP
standalone smoke binary on a hosted Linux runner.

## Summary

A Linux standalone player built from an empty project can't start on
a machine without an X display (`DISPLAY` unset). The log shows
`Selected window backend: (null)`. Then:

- with `-nographics` (with or without `-batchmode`), the player
  segfaults (exit 139) right after loading the first scene, before any
  `MonoBehaviour.Start` runs;
- without `-nographics`, the player never reaches `Start`, doesn't exit,
  and ignores `SIGTERM`; it has to be killed with `SIGKILL`.

The same binary starts normally under `xvfb-run`, where the window
backend is `x11`.

## Minimal reproduction

A bare project: `ProjectSettings/ProjectVersion.txt` containing
`m_EditorVersion: 6000.3.6f1`, a `Packages/manifest.json` of
`{ "dependencies": {} }`, and two scripts.

`Assets/Repro.cs`:

```csharp
using UnityEngine;

public sealed class Repro : MonoBehaviour
{
    private void Start()
    {
        Debug.Log("REPRO reached Start");
        Application.Quit(0);
    }
}
```

`Assets/Editor/ReproBuild.cs` creates an empty scene containing one
`Repro` component and builds it for `StandaloneLinux64` twice, once
with `ScriptingImplementation.Mono2x` and once with
`ScriptingImplementation.IL2CPP`:

```csharp
var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
new GameObject("Repro").AddComponent<Repro>();
EditorSceneManager.SaveScene(scene, "Assets/Repro.unity");

PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);
BuildPipeline.BuildPlayer(new BuildPlayerOptions
{
    scenes = new[] { "Assets/Repro.unity" },
    locationPathName = output,
    target = BuildTarget.StandaloneLinux64,
});
```

Run each player on a Linux machine with no `DISPLAY` or
`WAYLAND_DISPLAY` set:

```bash
timeout -k 10 60 ./Repro -batchmode -nographics -logFile player.log < /dev/null
echo $?   # 139
xvfb-run -a ./Repro -batchmode -nographics -logFile player.log
echo $?   # 0, and player.log contains "REPRO reached Start"
```

The full project and the workflow that builds and runs it are on the
[`scratch/linux-headless-segfault-repro`](https://github.com/elevenlabs/unity/tree/scratch/linux-headless-segfault-repro)
branch ([run 44772993075](https://github.com/elevenlabs/unity/actions/runs/44772993075)).

## Results

GitHub Actions `ubuntu-latest`, `DISPLAY` and `WAYLAND_DISPLAY` unset,
each run under `timeout -k 10 60`:

| Flags | Mono | IL2CPP |
| --- | --- | --- |
| `-batchmode -nographics` | segfault (139) | segfault (139) |
| `-nographics` | segfault (139) | segfault (139) |
| `-batchmode` | hang, killed (137) | hang, killed (137) |
| none | hang, killed (137) | hang, killed (137) |
| `-batchmode -nographics` under `xvfb-run -a` | exits 0, `Start` reached | exits 0, `Start` reached |

The window backend is `(null)` in every run without Xvfb and `x11`
under Xvfb.

## Expected behaviour

The
[Player command-line arguments](https://docs.unity.com/en-us/engine/6000.3/manual/unity-editor/command-line-arguments/player)
page says `-batchmode` runs "the application in headless mode", and
that with `-nographics` in batch mode Unity "doesn't initialize a
graphics device. This makes it possible to run your automated
workflows on machines that don't have a GPU."

A player started with `-batchmode -nographics` should therefore run
without a display server, and at minimum fail with an error instead
of crashing or hanging.

## Actual behaviour

The Mono player's log ends after the scripting domain reset, with no
crash handler output:

```
Selected window backend: (null)
...
Forcing GfxDevice: Null
NullGfxDevice:
    Renderer: Null Device
FMOD initialized on nosound output
Begin MonoManager ReloadAssembly
- Loaded All Assemblies, in  0.109 seconds
- Finished resetting the current domain, in  0.004 seconds
[Physics::Module] Selected backend.
```

The IL2CPP player logs the same `SIGSEGV` line. The trace below is
from our SDK's IL2CPP smoke binary, which crashes at the same point;
the stack never enters `GameAssembly.so`:

```
Selected window backend: (null)
...
UnloadTime: 0.206936 ms
Received signal SIGSEGV (11) - code:1 errno:0 addr:(nil)
Obtained 7 stack frames.
#0  0x007f3e60645330 in (Unknown)
#1  0x007f3e6078a21a in (Unknown)
#2  0x007f3e620a6596 in (Unknown)
#3  0x007f3e62086e89 in PlayerMain(int, char**)
#4  0x007f3e6062a1ca in (Unknown)
#5  0x007f3e6062a28b in __libc_start_main
#6  0x000000002015b1 in (Unknown)
```

## Workaround applied in this SDK

The `standalone-stripping-smoke` CI job installs Xvfb and runs the
smoke binary through `xvfb-run -a`. See the "Run stripping probe" step
in [`.github/workflows/integration.yml`](../../.github/workflows/integration.yml).

The Dedicated Server build subtarget is the documented headless
target, but it isn't a substitute here: the binary under test is the
same desktop player our consumers ship.

## Asks for Unity

1. **Confirm the expected behaviour:** should a Linux desktop player
   started with `-batchmode -nographics` run without a display server?
   If a display is required, please document it on the Player
   command-line arguments page.
2. **Fail instead of crashing or hanging:** when no window backend can
   be selected, exit with a clear error. The `-batchmode`-only case
   currently hangs and ignores `SIGTERM`, which stalls CI jobs until
   their timeout.

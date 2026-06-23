# UnityLinker silently drops package-internal `link.xml` files when the package is referenced via `file:` and the host project is embedded in the package tree

**Unity version:** 6000.3.6f1 (Unity 6 LTS)
**Platforms reproduced:** macOS arm64 Standalone, IL2CPP, Managed
Stripping Level = High
**Severity:** Silent — no warning logged, build succeeds, runtime
throws a misleading `JsonSerializationException` once Newtonsoft.Json
tries to reflect over a stripped constructor.
**Discovered:** 2026-06-23 while building the IL2CPP standalone smoke
gate for our SDK (see [`Docs~/plans/v0.1-parity.md` → #9e](../plans/v0.1-parity.md)).

## Summary

UnityLinker should auto-discover `link.xml` files anywhere inside a
package per the [Managed code stripping docs](https://docs.unity3d.com/6000.3/Documentation/Manual/managed-code-stripping.html).
That holds for packages installed via the Package Manager (e.g.
`com.unity.nuget.newtonsoft-json`, whose `link.xml` is honoured),
**but not** for packages referenced as a local file-path package
(`"file:..."` in `manifest.json`) when the host project happens to
live inside the package's directory tree. In that layout, the
`link.xml` files are imported (they show up in the asset import logs),
but UnityLinker is never told about them — the
`--include-link-xml=...` arg list on the linker invocation contains
only the auto-generated `TypesInScenes.xml`. The link.xml content is
silently ignored.

## Repository layout that reproduces

```
<repo>/
  package.json                           ← UPM manifest (id: io.elevenlabs.agents)
  Runtime/Native/link.xml                ← preserves ElevenLabs.Protocol.* types
  Runtime/Native/.../*.cs                ← Newtonsoft-reflected DTOs
  TestProject/                           ← embedded host project (developer CWD)
    Assets/
    Packages/
      manifest.json                      ← contains "io.elevenlabs.agents": "file:../.."
    ProjectSettings/
      ProjectSettings.asset
```

The `file:../..` reference makes the embedded `TestProject/` part of
the package tree from Unity's perspective: it sees
`Packages/io.elevenlabs.agents/TestProject/Assets/...` alongside the
local `Assets/`, and most asset paths show up in import logs twice.

## Minimal reproduction

1. Create a Unity 6 package with the layout above. A `link.xml` at
   `Runtime/Anything/link.xml` preserves a type that is **only**
   constructed via Newtonsoft.Json reflection (e.g. a DTO with a
   `[JsonProperty]`-annotated property setter and no other callers).
2. Build the host project for the current platform with **Scripting
   Backend = IL2CPP** and **Managed Stripping Level = High** via
   `BuildPipeline.BuildPlayer`.
3. Run the player and trigger a deserialization that uses the
   preserved type.

Observed failure on macOS arm64 IL2CPP:

```
JsonSerializationException: Unable to find a constructor to use for type
ElevenLabs.Protocol.ConversationInitiationMetadata. A class should either
have a default constructor, one constructor with arguments or a constructor
marked with the JsonConstructor attribute. Path
'conversation_initiation_metadata_event', line 1, position 42.
```

The default constructor on `ConversationInitiationMetadata` has been
stripped, despite the `link.xml` in `Runtime/Native/link.xml` declaring
`<type fullname="ElevenLabs.Protocol.ConversationInitiationMetadata"
preserve="all" />`.

## Diagnostic that proves the link.xml never reached UnityLinker

Inspect the UnityLinker command Unity actually runs (look for the
`build/deploy/UnityLinker` line in the batchmode build log) and
extract the `--include-link-xml=` arguments:

```bash
grep "build/deploy/UnityLinker" build.log \
  | tr ' ' '\n' \
  | grep -E '^--include-link-xml='
```

Expected: one `--include-link-xml=...` for `TypesInScenes.xml` **plus**
one for each `link.xml` Unity discovered in the project's packages and
`Assets/`.

Observed: only `TypesInScenes.xml` is passed. None of the project's
package-internal `link.xml` files appear.

The auto-discovery does see them — they show up in the asset import
log, e.g.:

```
Packages/io.elevenlabs.agents/Runtime/Native/link.xml(modified date ...)
Packages/io.elevenlabs.agents/Runtime/WebGL/link.xml(modified date ...)
```

…but the asset-import discovery and the linker-argument plumbing are
not connected in this configuration.

## Expected behaviour

`link.xml` files inside a package referenced as `file:...` are
auto-discovered and passed to `UnityLinker --include-link-xml=` just
like `link.xml` files in packages installed via the Package Manager.
The per-package auto-discovery should not depend on the embedded host
project layout.

## Workaround applied in this SDK

Register an `IUnityLinkerProcessor` build callback that explicitly
returns the absolute path of our `Runtime/Native/link.xml` to
UnityLinker. See [`Editor/LinkXmlInjector.cs`](../../Editor/LinkXmlInjector.cs).
With that callback, the standalone smoke runs end-to-end and the
constructor is preserved.

Other workarounds we considered and rejected:

- **Wildcard `<type fullname="ElevenLabs.Protocol.*" preserve="all" />`.**
  Not honoured by UnityLinker — also a silent failure (likely a
  separate bug; the [`com.unity.nuget.newtonsoft-json` package's
  link.xml](https://github.com/jilleJr/Newtonsoft.Json-for-Unity/blob/master/Src/Newtonsoft.Json-for-Unity/link.xml)
  lists each type explicitly for the same reason).
- **Move `link.xml` to the package root** (`<repo>/link.xml`). Would
  collide with the package metadata at the repo root and doesn't
  remove the underlying auto-discovery bug.
- **`[Preserve]` attribute on each DTO** in source. Would require
  modifying our protocol DTO codegen output; the link.xml route is
  the more standard channel.

## Asks for Unity

1. **Confirm or repudiate the bug:** in the layout above, should
   `link.xml` auto-discovery work without the `IUnityLinkerProcessor`
   shim? If yes, what makes the in-tree package different from a
   Package-Manager-installed package?
2. **Surface a diagnostic:** today there is no log line indicating
   which `link.xml` files were considered and which were dropped.
   Even a verbose-mode log of "discovered N link.xml files, passing M
   to UnityLinker" would have collapsed the debugging time on our
   side from hours to minutes.
3. **Wildcard `<type fullname="Foo.*"/>` support:** the [Mono linker
   format docs](https://github.com/dotnet/runtime/blob/main/docs/tools/illink/data-formats.md)
   reference wildcard usage; Unity's UnityLinker silently skips them.
   Either implement the same wildcard semantics or fail the build
   when a wildcard fullname is encountered.

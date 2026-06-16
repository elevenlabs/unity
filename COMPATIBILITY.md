# Compatibility

Hard requirements for using the ElevenAgents SDK in a Unity project.

## Unity version

**Unity 2023.1 or later** (Unity 6 LTS 6000.3.6f1 tested).

The async bridge API uses Unity's [`Awaitable` / `AwaitableCompletionSource<T>`](https://docs.unity3d.com/2023.1/Documentation/ScriptReference/Awaitable.html), which was introduced in Unity 2023.1.

## API compatibility level

**Player Settings → Player → Other Settings → Api Compatibility Level** must be **.NET Standard 2.1** or higher.

The SDK reads JS→C# callback payloads on WebGL via [`Marshal.PtrToStringUTF8`](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.ptrtostringutf8), which is not available in .NET Standard 2.0. Unity 2023.1+ projects default to .NET Standard 2.1, so most projects require no change. A project that has been explicitly downgraded to .NET Standard 2.0 will encounter a `MissingMethodException` on the first JS→C# callback at runtime.

## Platform support

The public API (`Runtime/`) is cross-platform. WebGL is the most mature target today; native targets (desktop, mobile, XR) are implemented under `Runtime/Native/` and ship in the same package — the right implementation is selected at compile time per platform.

## Additional WebGL setup

### Enable "Use WebAssembly.Table"

**Player Settings → WebGL → Publishing Settings → Use WebAssembly.Table** must be **on**.

This is a hard requirement. The SDK's JS→C# delivery path uses Emscripten's [`{{{ makeDynCall(...) }}}`](https://docs.unity3d.com/6000.0/Documentation/Manual/web-interacting-browser-deprecated.html) macro, which relies on the wasm table being exported. With the setting off, the macro expands to a call to `getWasmTableEntry`, which references a `wasmTable` that wasn't exported — the result is a runtime `ReferenceError` on the first JS→C# callback, not a link-time error. The build preprocessor included in the SDK will fail the build with instructions when the setting is off.

**Why Unity deprecated the old API:** Unity 6 deprecated the legacy `Module.dynCall_*` family of functions in favour of the `makeDynCall` macro (see Unity's [deprecated browser-interaction APIs page](https://docs.unity3d.com/6000.0/Documentation/Manual/web-interacting-browser-deprecated.html)). Enabling `Use WebAssembly.Table` is Unity's recommended forward-compatible setting that makes the macro path work (see the [WebGL Player Settings docs](https://docs.unity3d.com/6000.3/Documentation/Manual/class-PlayerSettingsWebGL.html)).

**Consumer-impact risk:** enabling `Use WebAssembly.Table` is incompatible with any other `.jslib` in the same project that still calls `Module.dynCall_*`. If a third-party plug-in you are using does this, you will need to either migrate that plug-in or wait for its author to do so before the SDK can be used in the same build. `dynCall_*` is deprecated in Unity 6 regardless of this SDK; the right long-term path is migration.

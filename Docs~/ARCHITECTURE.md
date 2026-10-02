# Architecture

> How the ElevenLabs Unity SDK is put together: assemblies, transports, audio
> pipeline, and codegen. For in-flight design work, see [`plans/`](./plans/).

## Goal

One public C# API for ElevenLabs Agents across native Unity targets and WebGL.
The user writes a single `Conversation` against a single event/method surface
and gets the same semantics regardless of where their game runs. Desktop
standalone, the Editor, and WebGL are built and tested; other native targets
share the native implementation but are untested.

## Overview

```
┌──────────────────────────────────────────────────────────────────────────┐
│  ElevenLabs.Agents.Core   (asmdef; every platform)                       │
│    public    Conversation, ConversationOptions, events, RegisterTool     │
│              Protocol DTOs + *Args records in ElevenLabs.Protocol        │
│              (codegen from AsyncAPI, incoming + outgoing)                │
│    internal  IConnection        mirrors JS SDK BaseConnection            │
│              IInputController   mirrors JS SDK InputController           │
│              IOutputController  mirrors JS SDK OutputController          │
└──────────────────────────────────────────────────────────────────────────┘
            │                                              │
            │  excludePlatforms: [WebGL]                   │  includePlatforms: [Editor, WebGL]
            ▼                                              ▼
┌─────────────────────────────────────┐    ┌──────────────────────────────────┐
│  ElevenLabs.Agents.Native           │    │  ElevenLabs.Agents.WebGL         │
│    NativeWebSocketConnection        │    │    BridgedWebSocketConnection    │
│    UnityMicrophoneInput             │    │    BridgedWebRTCConnection       │
│    UnityAudioSourceOutput           │    │    BridgedInputController        │
│      └ IAudioOutputEngine           │    │    WebAudioBackedOutput (WS)     │
│        └ UnityGeneratorAudio-       │    │    BridgedOutputController (RTC) │
│          OutputEngine               │    └──────────────────────────────────┘
│          + AgentAudioGenerator-     │                     │
│            Component (IAudioGen.)   │                     │  jslib via
└─────────────────────────────────────┘                     ▼
                                            ┌──────────────────────────────────┐
                                            │  WebGL Bridge (jslib)            │
                                            │    Primitives: JsObject /        │
                                            │                JsFunction /      │
                                            │                BridgeCallback    │
                                            │    Factories: @elevenlabs/client │
                                            │                classes + Web     │
                                            │                Audio sink,       │
                                            │                registered at init│
                                            └──────────────────────────────────┘
```

The same `Conversation` orchestrates `IConnection` + `IInputController` +
`IOutputController` regardless of platform. Instead of bridging the
conversation, the WebGL implementation bridges the three abstractions the
conversation depends on, using a generic primitive layer that knows nothing
about the SDK.

### Platform selection

The platform split is done by asmdef platform filters, not `#if` around
implementation code. [`ElevenLabs.Agents.Native`](../Runtime/Native/ElevenLabs.Agents.Native.asmdef)
excludes WebGL; [`ElevenLabs.Agents.WebGL`](../Runtime/ElevenLabs.Agents.WebGL.asmdef)
(at the `Runtime/` root, covering `Runtime/WebGL/`) includes only Editor and
WebGL, so its Edit Mode tests compile on any active build target. Both
assemblies therefore load in the Editor. Each ships a launcher
([`NativeSessionLauncher`](../Runtime/Native/NativeSessionLauncher.cs),
[`BridgedSessionLauncher`](../Runtime/WebGL/Bridged/BridgedSessionLauncher.cs))
that assigns `Conversation.SessionFactory` at startup; their registration is
guarded by `#if !UNITY_WEBGL` / `#if UNITY_WEBGL` so exactly one wins. The
remaining `#if` guards sit in the WebGL interop layer, swapping its
`DllImport`s for throwing stubs outside a WebGL player.

## The C# Conversation

Owns: lifecycle, status/mode tracking, message dispatch, ping/pong,
interruption handling, client-tool invocation, feedback eligibility, agent-tool
and MCP event routing, file upload.

Does **not** own: WebSocket framing, microphone access, audio playback. Those
are behind the three abstractions below.

The public surface mirrors `@elevenlabs/client`'s `Conversation` conceptually
but is idiomatic C#: standard `event` keyword with `+=` / `-=`; `Awaitable` /
`Awaitable<T>` for async methods (`StartSessionAsync`, `EndSession`,
`UploadFileAsync`, …). Wire-level message shapes come from the same AsyncAPI
spec the JS SDK consumes, so the two clients stay in lock-step at the protocol
layer.

Event subscribers register and deregister freely at any time. The connection's
single `onMessage` callback is wired once at session start and the C# router
fans out into the user's subscribed events.

## The three abstractions

Each mirrors a class in `@elevenlabs/client` and is `internal` to
`ElevenLabs.Agents.Core`, exposed via `InternalsVisibleTo` to the Native and
WebGL assemblies (and their tests). Supporting types referenced from the
public surface (`Mode`, `FormatConfig`, `InputDeviceConfig`,
`OutputDeviceConfig`, `DisconnectionDetails`) are `public`.

### IConnection

Mirrors [`BaseConnection`](https://github.com/elevenlabs/packages/blob/main/packages/client/src/utils/BaseConnection.ts)
([source](../Runtime/Core/IConnection.cs)):

- Properties: `ConversationId`, `InputFormat`, `OutputFormat`
- `void Send(OutgoingSocketEvent message)`
- Events: `OnMessage(IncomingSocketEvent)`, `OnDisconnect(DisconnectionDetails)`, `OnModeChange(Mode)`
- `void Close()` (idempotent)

Implementations:

- `NativeWebSocketConnection` — `System.Net.WebSockets.ClientWebSocket`; rejects `ConnectionType.WebRTC` with `NotSupportedException`
- `BridgedWebSocketConnection` — wraps `@elevenlabs/client`'s `WebSocketConnection`
- `BridgedWebRTCConnection` — wraps `@elevenlabs/client`'s `WebRTCConnection` (WebGL only)

The transport is chosen explicitly via `ConversationOptions.ConnectionType`
(default `WebSocket`).

### IInputController

Mirrors [`InputController`](https://github.com/elevenlabs/packages/blob/main/packages/client/src/InputController.ts)
([source](../Runtime/Core/IInputController.cs)):

- `bool IsMuted`
- `event Action<byte[]>? AudioChunkAvailable` — 16-bit LE PCM; native only
- `Awaitable Close()`, `Awaitable SetDevice(InputDeviceConfig? config = null, FormatConfig? format = null)`, `Awaitable SetMuted(bool isMuted)`
- `float GetVolume()`, `void GetByteFrequencyData(byte[] buffer)`

Implementations:

- `UnityMicrophoneInput` — `UnityEngine.Microphone` into a looping `AudioClip`, polled on the main thread every ~25 ms
- `BridgedInputController` — wraps `@elevenlabs/client`'s `MediaDeviceInput` (WebSocket) or the WebRTC connection's coupled input

### IOutputController

Mirrors [`OutputController`](https://github.com/elevenlabs/packages/blob/main/packages/client/src/OutputController.ts)
([source](../Runtime/Core/IOutputController.cs)):

- `Awaitable Close()`, `Awaitable SetDevice(OutputDeviceConfig? config = null, FormatConfig? format = null)`
- `void PushAudio(byte[] pcm)` — 16-bit LE PCM; no-op on WebGL
- `void SetVolume(float volume)`, `void Interrupt(int? resetDurationMs = null)`
- `float GetVolume()`, `void GetByteFrequencyData(byte[] buffer)`

Implementations:

- `UnityAudioSourceOutput` — decodes PCM into a ring buffer drained on the audio thread by an `IAudioOutputEngine` (see "Audio routing")
- `WebAudioBackedOutput` — WebSocket arm on WebGL; drives the JS `createWebAudioSink`
- `BridgedOutputController` — WebRTC arm on WebGL; wraps the connection's coupled output

## Bridge primitives

Three domain-agnostic handle types over Unity's jslib interop, shipped as
`Plugins/WebGL/ElevenLabsBridge.jslib`. Two are invoked by C#, one is invoked
by JS — all share one registry-and-marker machinery, one async settle channel,
and one set of JSON marker conventions (`$ref` / `$fn` / `$cb`) for passing
handles across the boundary.

- **`JsObject`** — C# handle to a remote JS object. Construct via `JsBridge.InvokeFactoryAsync<JsObject>(name, args)` against factories the JS side registered at module init; call methods, read properties, dispose.
- **`JsFunction`** — C# handle to a remote JS function (typically returned from a `JsObject` method or factory, e.g. the detach function returned by `attachDefaultAudio`); call sync or async, dispose.
- **`BridgeCallback`** — C# delegate exposed to JS as a callable. Wrap a delegate, pass it as a method argument; JS sees a plain function. JS invokes it through a function pointer registered at startup (`makeDynCall`), and the delegate runs on Unity's main thread. Fire-and-forget: JS gets no return value.

Calling a new method on an already-exposed JS object requires **no new bridge
code** — the C# side just calls a new method name through `JsObject`.

## Audio routing

Identical from the consumer's perspective — same `Conversation`, same events,
same `ConversationOptions.OutputAudioSource` — but each platform reaches the
speakers differently.

### Native

Input controller emits PCM chunks → C# Conversation wraps them as
`user_audio_chunk` → connection sends. Connection emits audio events → C#
Conversation decodes the payload and calls `PushAudio` →
[`UnityAudioSourceOutput`](../Runtime/Native/UnityAudioSourceOutput.cs) writes
it into a ring buffer. The production engine,
[`UnityGeneratorAudioOutputEngine`](../Runtime/Native/UnityGeneratorAudioOutputEngine.cs),
adds an [`AgentAudioGeneratorComponent`](../Runtime/Native/AgentAudioGeneratorComponent.cs)
(Unity 6.3 `IAudioGenerator`) next to the `AudioSource` — either a hidden host
GameObject or the user-supplied `ConversationOptions.OutputAudioSource` — and
drains the ring on the audio thread, resampling to the device output rate.
Unity's audio engine owns spatialisation, mixer routing, rolloff curves, and
effects. The streaming-`AudioClip` engine
([`UnityAudioOutputEngine`](../Runtime/Native/UnityAudioOutputEngine.cs)) is
not used in production; it remains as a fallback covered by a PlayMode
characterization test.

### WebGL

Audio bytes **never cross the bridge** in either direction. On the WebSocket
arm, input ↔ connection ↔ output are wired _inside JavaScript_ by the
`attachDefaultAudio` factory
([`audio-glue.ts`](../Bridge~/src/connection/audio-glue.ts)), which composes
`@elevenlabs/client`'s
[`attachInputToConnection`](https://github.com/elevenlabs/packages/blob/main/packages/client/src/utils/attachInputToConnection.ts)
and [`attachConnectionToOutput`](https://github.com/elevenlabs/packages/blob/main/packages/client/src/utils/attachConnectionToOutput.ts).
The C# Conversation still receives audio events from the connection (for
`event_id` tracking, interruption, mode/feedback), but with the `audio_base_64`
payload stripped on the JS side.

Playback on the WebSocket arm always goes through a Web Audio graph
([`web-audio-sink.ts`](../Bridge~/src/connection/web-audio-sink.ts):
`AudioWorkletNode` → gain → analyser → `StereoPannerNode` / `PannerNode`
crossfade → `AudioContext.destination`). When
`ConversationOptions.OutputAudioSource` is set,
[`WebAudioBackedOutput`](../Runtime/WebGL/Bridged/WebAudioBackedOutput.cs)
mirrors a curated subset of the `AudioSource`'s properties (transform position
in the active `AudioListener`'s local frame, `volume`, `spatialBlend`,
`minDistance`, `maxDistance`, `rolloffMode`, `panStereo`) onto the JS sink
once per Unity frame; without one, the sink plays unspatialized mono.
`dopplerLevel` is pushed too but has no effect, since Web Audio has no doppler
scalar. The `AudioSource` itself is _decorative_ on WebGL — Unity never streams
audio through it because it can't (no scriptable audio pipeline on WebGL — see
[`unity-issues/webgl-scriptable-audio-pipeline.md`](./unity-issues/webgl-scriptable-audio-pipeline.md)).
The fidelity matrix lives in
[`COMPATIBILITY.md`](../COMPATIBILITY.md#webgl-audio-output-limitations); the
design lives in [`plans/output-audio-source.md`](./plans/output-audio-source.md).

On the WebRTC arm, livekit-client owns audio playback through a remote audio
track, so `OutputAudioSource` is ignored (with a one-time warning).

## Protocol DTOs

Both transports speak the same `IncomingSocketEvent` / `OutgoingSocketEvent`
union. The C# DTOs under `Runtime/Core/Protocol/` are generated by
[`Codegen~/`](../Codegen~/) from the vendored AsyncAPI spec (same source the
JS SDK consumes), committed, and checked for drift in CI
(`verify:protocol-dtos`). They serialize with Newtonsoft.Json
(`[JsonProperty]`) and are also used when (de)serializing payloads that cross
the jslib boundary.

What is **not** generated: the JS-side wrappers under `Bridge~/src/`. They're
small and shaped exactly for the bridge contract.

## JS-side artifacts shipped to consumers

Two `.jslib` files under `Plugins/WebGL/`, both bundled by Rolldown from
TypeScript, committed, and checked for drift in CI
(`verify:primitives` / `verify:connection`):

- `ElevenLabsBridge.jslib` — the three generic primitives; no SDK dependency.
- `ElevenLabsConnection.jslib` — bundles `@elevenlabs/client` and registers its factories with the primitive layer at startup: `createWebSocketConnection`, `createWebRTCConnection`, `createConnection`, `createMediaDeviceInput` ([`factories.ts`](../Bridge~/src/connection/factories.ts)), `attachDefaultAudio`, and `createWebAudioSink`. Its only `DllImport` entry point is the no-op `EL_EnsureConnectionFactoriesLoaded`, called from `BridgedSessionLauncher` so Emscripten doesn't strip the bundle; every interaction with the factories goes through the generic primitives.

Emscripten concatenates them at WebGL build time.

## Repository layout

```
package.json                     ← UPM manifest (io.elevenlabs.agents)
Runtime/
  ElevenLabs.Agents.WebGL.asmdef ← covers Runtime/WebGL/ (Editor + WebGL)
  Core/                          ← ElevenLabs.Agents.Core.asmdef
    Conversation.cs, ConversationOptions.cs, ConnectionType.cs, …
    IConnection.cs, IInputController.cs, IOutputController.cs
    HttpFileUploader.cs
    Protocol/                    ← codegen output (committed)
  Native/                        ← ElevenLabs.Agents.Native.asmdef (all but WebGL)
    NativeSessionLauncher.cs, NativeWebSocketConnection.cs
    UnityMicrophoneInput.cs, UnityAudioSourceOutput.cs
    IAudioOutputEngine.cs, UnityGeneratorAudioOutputEngine.cs,
    AgentAudioGeneratorComponent.cs, AgentAudioControl.cs,
    AgentAudioRealtime.cs, AudioPcmRing.cs, UnityAudioOutputEngine.cs
  WebGL/
    JsBridge.cs, JsObject.cs, JsFunction.cs, BridgeCallback.cs
    ElevenLabsBridgeNative.cs    ← DllImports into the .jslib files
    Marshalling/, Internal/      ← marker encoding, handle registries
    Bridged/                     ← BridgedSession(Launcher), Bridged*Connection,
                                   BridgedInput/OutputController, WebAudioBackedOutput
Editor/                          ← WebGL build preprocessor, link.xml injector, headless build entry points
Plugins/WebGL/
  ElevenLabsBridge.jslib         ← bundled from Bridge~/src/primitives/
  ElevenLabsConnection.jslib     ← bundled from Bridge~/src/connection/
Samples/                         ← in-repo smoke-test scenes (Bridge, Conversation, Standalone)
Samples~/                        ← UPM-importable samples (GettingStarted, QuickStart)
Tests/
  Editor/                        ← Edit Mode: bridge primitives, Bridged/, Native/
  Runtime/Native/                ← Play Mode: audio engine characterization tests

Bridge~/                         ← Unity ignores `~` dirs; JS dev tooling
  src/primitives/                ← generic JsObject / JsFunction / BridgeCallback layer
  src/connection/                ← factory registrations, audio glue, Web Audio sink
  build/                         ← Rolldown bundling + makeDynCall substitution
                                   (Vitest tests are co-located as src/**/*.test.ts)
Codegen~/                        ← AsyncAPI → C# DTO generator
IntegrationTests~/               ← Playwright-driven WebGL smoke tests
TestProject/                     ← host Unity project for headless test runs and builds
Docs~/
  ARCHITECTURE.md                ← this file
  plans/                         ← design plans
  unity-issues/                  ← Unity bug writeups
```

## Open architecture questions

1. **Public vs. internal surface of the three abstractions.** They are `internal` today. Revisit if users want to plug in custom transports (e.g. a relay); flipping `internal → public` is non-breaking.

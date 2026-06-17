# Architecture

> Living document. Captures the **to-be** shape of the ElevenLabs Unity SDK as we
> build toward v0.1 and beyond. Update as decisions land. For implementation
> sequencing, see [`plans/`](./plans/).

## Goal

One public C# API for ElevenLabs Agents across every Unity target — desktop,
mobile, XR, and WebGL — with no `#if UNITY_WEBGL` leaking past the
infrastructure layer. The user writes a single `Conversation` against a single
event/method surface and gets identical semantics regardless of where their
game runs.

## Overview

```
┌──────────────────────────────────────────────────────────────────────────┐
│  ElevenLabs.Agents        (public C# API — identical on every platform)  │
│    Conversation, ConversationOptions, events, ClientTools                │
└──────────────────────────────────────────────────────────────────────────┘
                                    │
                                    │   depends on
                                    ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  ElevenLabs.Agents.Core   (internal abstractions, shared by all impls)   │
│    IConnection            — mirrors @elevenlabs/client BaseConnection    │
│    IInputController       — mirrors @elevenlabs/client InputController   │
│    IOutputController      — mirrors @elevenlabs/client OutputController  │
│    Protocol DTOs          — codegen from AsyncAPI (incoming + outgoing)  │
└──────────────────────────────────────────────────────────────────────────┘
            │                                              │
            │  #if !UNITY_WEBGL                            │  #if UNITY_WEBGL
            ▼                                              ▼
┌─────────────────────────────────────┐    ┌──────────────────────────────────┐
│  ElevenLabs.Agents.Native           │    │  ElevenLabs.Agents.WebGL         │
│    NativeWebSocketConnection        │    │    BridgedWebSocketConnection    │
│    UnityMicrophoneInput             │    │    BridgedWebRTCConnection       │
│    UnityAudioSourceOutput           │    │    BridgedInputController        │
│  (future) NativeWebRTCConnection    │    │    BridgedOutputController       │
└─────────────────────────────────────┘    └──────────────────────────────────┘
                                                            │
                                                            │  jslib via
                                                            ▼
                                            ┌──────────────────────────────────┐
                                            │  WebGL Bridge (jslib)            │
                                            │    Primitives: JsObject /        │
                                            │                JsFunction /      │
                                            │                BridgeCallback    │
                                            │    Factories: @elevenlabs/client │
                                            │                classes registered│
                                            │                at module init    │
                                            └──────────────────────────────────┘
```

The same `Conversation` orchestrates `IConnection` + `IInputController` +
`IOutputController` regardless of platform. The bridge moves *down* one layer:
instead of bridging the conversation, the WebGL implementation bridges the
three abstractions the conversation depends on, using a generic primitive
layer that knows nothing about the SDK.

## The C# Conversation

Owns: lifecycle, status/mode tracking, message dispatch, interruption handling,
client-tool invocation, feedback eligibility, server-tool routing.

Does **not** own: WebSocket framing, microphone access, audio playback. Those
are behind the three abstractions below.

The public surface mirrors `@elevenlabs/client`'s `Conversation` conceptually
but is idiomatic C# (standard `event` keyword with `+=` / `-=`; `Awaitable<T>`
for async methods; `IDisposable` for cleanup). Wire-level message shapes come
from the same AsyncAPI spec the JS SDK consumes, so the two clients stay in
lock-step at the protocol layer.

Event subscribers register and deregister freely at any time. The C# `Conversation`
holds standard .NET multicast delegates; the JS-side connection's single
`onMessage` callback is wired once at session start (via a `BridgeCallback`)
and the C# router fans out into the user's subscribed events. No "register all
listeners before `StartSession`" constraint — that limitation belonged to the
old design where the C# side was a façade over `@elevenlabs/client`'s
`Conversation`.

## The three abstractions

Each mirrors a class already proven in `@elevenlabs/client`. Keeping the
C# shapes close to the JS shapes means the bridged WebGL implementations are
near-passthrough, and divergence between the two transports stays small.

### IConnection

Mirrors [`BaseConnection`](https://github.com/elevenlabs/packages/blob/main/packages/client/src/utils/BaseConnection.ts):

- Properties: `ConversationId`, `InputFormat`, `OutputFormat`
- `Send(OutgoingSocketEvent)` — typed outgoing message
- Events: `OnMessage(IncomingSocketEvent)`, `OnDisconnect(DisconnectionDetails)`, `OnModeChange(Mode)`
- `Close()`

Implementations:
- `NativeWebSocketConnection` — `System.Net.WebSockets.ClientWebSocket`
- `BridgedWebSocketConnection` — wraps `@elevenlabs/client`'s `WebSocketConnection`
- `BridgedWebRTCConnection` — wraps `@elevenlabs/client`'s `WebRTCConnection`
- `NativeWebRTCConnection` — future (post-1.0)

### IInputController

Mirrors [`InputController`](https://github.com/elevenlabs/packages/blob/main/packages/client/src/InputController.ts):

- `Close()`, `SetDevice(InputDeviceConfig)`, `SetMuted(bool)`, `IsMuted`
- `GetVolume()`, `GetByteFrequencyData(byte[] buffer)`
- Audio source for the connection (see "Audio routing" below)

Implementations:
- `UnityMicrophoneInput` — `UnityEngine.Microphone` + an `AudioClip` PCM reader
- `BridgedInputController` — wraps `@elevenlabs/client`'s `MediaDeviceInput`

### IOutputController

Mirrors [`OutputController`](https://github.com/elevenlabs/packages/blob/main/packages/client/src/OutputController.ts):

- `Close()`, `SetDevice(OutputDeviceConfig)`, `SetVolume(float)`, `Interrupt(int? resetMs)`
- `GetVolume()`, `GetByteFrequencyData(byte[] buffer)`
- Audio sink fed by the connection (see "Audio routing" below)

Implementations:
- `UnityAudioSourceOutput` — PCM into an `AudioClip` via `PCMReaderCallback`, played through an `AudioSource`
- `BridgedOutputController` — wraps `@elevenlabs/client`'s `MediaDeviceOutput`

## Bridge primitives

Three domain-agnostic handle types over Unity's jslib interop, shipped as
`Plugins/WebGL/ElevenLabsBridge.jslib`. Two are invoked by C#, one is invoked
by JS — all share one registry-and-marker machinery, one async settle channel,
and one set of JSON marker conventions (`$ref` / `$fn` / `$cb`) for passing
handles across the boundary.

- **`JsObject`** — C# handle to a remote JS object. Construct via `JsBridge.InvokeFactoryAsync<JsObject>(name, args)` against factories the JS side registered at module init; call methods, read properties, dispose.
- **`JsFunction`** — C# handle to a remote JS function (typically returned from a `JsObject` method, e.g. a `removeListener` returned by `addListener`); call sync or async, dispose.
- **`BridgeCallback`** — C# delegate exposed to JS as a callable. Wrap a delegate, pass it as a method argument; JS sees a plain function. When JS calls it, SendMessage routes back to the bridge and the wrapped delegate runs on Unity's main thread.

See [`plans/generic-bridge-primitives.md`](./plans/generic-bridge-primitives.md)
for the detailed design and protocol. Adding a method to the JS SDK requires
**no new bridge code** — the C# façade just calls a new method name through
the existing `JsObject` primitive.

## Audio routing

Two modes, exposed via an `AudioSink` choice on `ConversationOptions`. The
choice affects only where audio physically plays — the Conversation, events,
and protocol surface are identical.

### Default mode (v0.1)

- **Native:** input controller emits PCM frames → C# Conversation wraps as `user_audio_chunk` → connection sends. Connection emits audio events → C# Conversation routes the chunk into the output controller → `AudioSource` plays.
- **WebGL:** input ↔ connection ↔ output are wired *inside JavaScript* using `@elevenlabs/client`'s existing [`attachInputToConnection`](https://github.com/elevenlabs/packages/blob/main/packages/client/src/utils/attachInputToConnection.ts) and [`attachConnectionToOutput`](https://github.com/elevenlabs/packages/blob/main/packages/client/src/utils/attachConnectionToOutput.ts) helpers. **Audio bytes never cross the bridge.** C# Conversation still receives audio events from the connection (for `event_id` tracking, interruption, mode/feedback), but with the `audio_base_64` payload stripped on the JS side.

### Unity-routed mode (v0.3)

- **Native:** unchanged — that's how it already works.
- **WebGL:** audio bytes *do* cross the bridge so the C# Conversation can hand them to a user-supplied `AudioSource` for spatial 3D playback. WebSocket transports send base64 PCM via the existing string-channel; WebRTC transports require a Unity-specific `WebRTCAudioAdapter` that exposes decoded PCM frames through the bridge. Binary payload via heap pointer + dynCall comes in at this milestone if profiling warrants it.

Latency cost of default mode on WebGL: zero on the audio path, ~1 Unity frame
on control-plane operations (acceptable — network round-trips dominate).
Latency cost of Unity-routed mode: ~1 frame on the audio path.

## Protocol DTOs

Both `IConnection` implementations speak the same `IncomingSocketEvent` /
`OutgoingSocketEvent` union. We codegen these C# DTOs from the AsyncAPI spec
(same source the JS SDK consumes), commit the generated output, and gate
freshness in CI. The same generated DTOs are used by `BridgedWebSocketConnection`
when (de)serializing payloads that cross the jslib boundary.

What we do **not** codegen: the JS-side wrappers under `Bridge~/src/`. They're
small enough to hand-write and benefit from being shaped exactly for the
bridge contract rather than mechanically translated.

## JS-side artifacts shipped to consumers

Two `.jslib` files under `Plugins/WebGL/`, both committed and CI-verified for
freshness against their TypeScript sources:

- `ElevenLabsBridge.jslib` — the three generic primitives; no SDK dependency
- `ElevenLabsConnection.jslib` — registers `@elevenlabs/client`'s `WebSocketConnection`, `WebRTCConnection`, `MediaDeviceInput`, `MediaDeviceOutput` as factories with the primitive layer, plus the `attachDefaultAudio` composition helper. Bundles `@elevenlabs/client` itself. Contains zero `EL_*` `DllImport` entry points of its own — every interaction with these classes goes through the generic primitives.

Emscripten concatenates them at WebGL build time.

## Repository layout

```
Docs~/
  ARCHITECTURE.md                ← this file
  plans/                         ← design plans (read these before architectural changes)
package.json                     ← UPM manifest (io.elevenlabs.agents)

Runtime/
  ElevenLabs.Agents.asmdef       ← public API + Core abstractions
  Conversation.cs
  ConversationOptions.cs
  Core/
    IConnection.cs
    IInputController.cs
    IOutputController.cs
    Protocol/                    ← codegen output (committed)
  WebGL/                         ← #if UNITY_WEBGL
    BridgedWebSocketConnection.cs   ← thin wrapper around a JsObject
    BridgedWebRTCConnection.cs      ← thin wrapper around a JsObject
    BridgedInputController.cs       ← thin wrapper around a JsObject
    BridgedOutputController.cs      ← thin wrapper around a JsObject
    BridgedSession.cs               ← orchestrates the factory calls + attachDefaultAudio
    WebGLBridge.cs                  ← primitives MonoBehaviour (existing scaffolding)
    JsBridge.cs                     ← public InvokeFactory entry point
    JsObject.cs
    JsFunction.cs
    BridgeCallback.cs
  Native/                        ← #if !UNITY_WEBGL
    NativeWebSocketConnection.cs
    UnityMicrophoneInput.cs
    UnityAudioSourceOutput.cs

Plugins/WebGL/
  ElevenLabsBridge.jslib         ← bundled from Bridge~/src/primitives/   (shipping)
  ElevenLabsConnection.jslib     ← bundled from Bridge~/src/connection/   (Plan B)

Bridge~/                         ← Unity ignores `~` dirs; JS dev tooling
  src/
    primitives/                  ← generic JsObject / JsFunction / BridgeCallback layer
    connection/                  ← Plan B — factory registrations + attachDefaultAudio helper
  tests/                         ← Vitest

Tests/Editor/                    ← Unity Test Runner (Edit Mode)
```

## Open architecture questions

1. **Connection-aware vs. connection-agnostic Conversation.** Is the choice between WebSocket and WebRTC explicit on `ConversationOptions`, or inferred (as the JS SDK does via `determineConnectionType`)? Lean toward mirroring the JS SDK's inference rules to keep parity.
2. **Public vs. internal surface of the three abstractions.** Settled in Phase 4.2 — `IConnection` / `IInputController` / `IOutputController` are `internal` to `ElevenLabs.Agents.Core`, exposed via `InternalsVisibleTo` to `ElevenLabs.Agents.WebGL` (and the future `ElevenLabs.Agents.Native`) so platform impls can satisfy them. Supporting types referenced from the public Conversation surface (`Mode`, `FormatConfig`, `InputDeviceConfig`, `OutputDeviceConfig`, `DisconnectionDetails` + friends) stay `public`. Revisit if users want to plug in custom transports (e.g. a relay); flipping `internal → public` is non-breaking.

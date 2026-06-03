# RFC: ElevenLabs Unity SDK - Architecture & Distribution

**Status:** Draft for discussion  
**Author:** Claude steered by [Kræn Hansen](mailto:kraen@elevenlabs.io)  
**Audience:** Developer Experience team

---

## TL;DR

- **Build a Unity SDK for ElevenLabs Agents** with a single public C# API across desktop, mobile, XR, and WebGL.
- **Hybrid implementation:** two `Conversation` implementations behind one public C# API - a native C# implementation on desktop/mobile/XR, a thin façade over `@elevenlabs/client` on WebGL. Selected at compile time. WebGL gets WebRTC for free via the bridged implementation.
- **WebSocket-first on native.** The Agents WebSocket protocol is feature-complete; native WebRTC is a future second transport inside the native implementation, behind an internal `IAgentTransport` abstraction.
- **No LiveKit dependency in Unity.** Considered and rejected on platform coverage and packaging grounds; this also preserves strategic flexibility as we evolve our transport story across SDKs.
- **WebGL bridge built from three small generic primitives:** Promise-as-Task (C#→JS one-shot), an observer primitive (JS→C# streams), and JS-initiated promises (for client tools). All share a signal-then-retrieve shape; all reusable beyond this SDK.
- **Audio routing has two modes.** Default (Web Audio on WebGL, `AudioSource` on native) gives you a working voice agent with zero bridge latency on the audio path. Opt-in Unity-routed mode wires agent voice through a Unity `AudioSource` for spatial audio and in-game positioning.
- **Code generation** for protocol DTOs (OpenAPI → C#) and WebGL façade (`@elevenlabs/client` `.d.ts` → C#), using custom TypeScript generators. Generated output committed with a CI freshness gate. Pattern intended for broader SDK adoption.
- **Distribution:** UPM via git URL at launch, scoped registry once mature.
- **Roadmap:** v0.1 core session → v0.2 extensibility (client tools, overrides) → v0.3 platform polish (XR, spatial audio).

---

## Context

We need a Unity SDK for the ElevenLabs Agents platform. Our existing SDKs (JS, React, RN, Swift, Kotlin) support both WebRTC and WebSocket; WebRTC is provided by LiveKit's client SDKs today, but that's an implementation detail. The Agents WebSocket protocol is feature-complete relative to WebRTC - client tools, overrides, and the full event surface all work over WebSocket.

The Unity target matrix is broad: desktop (Windows/macOS/Linux), mobile (iOS/Android), XR (Quest, Vision Pro, PCVR), and WebGL. The SDK should match the _conceptual_ surface of the existing SDKs but be idiomatic to C# rather than mirror the JS API verbatim.

---

## Proposal: hybrid architecture

A single public C# API. Two implementations of the `Conversation` selected at compile time via `UNITY_WEBGL`. The split is invisible to consumers.

```
ElevenLabs.Agents (public API, identical on all platforms)
  └── Conversation, ConversationSession, ConversationOptions

ElevenLabs.Agents.Native (compiled on desktop/mobile/XR)
  ├── NativeConversation        (owns connection, audio, lifecycle in C#)
  └── IAgentTransport           (internal - WebSocket today, future WebRTC/WebTransport)

ElevenLabs.Agents.WebGL (compiled on WebGL)
  ├── WebGLBridgedConversation  (façade over @elevenlabs/client)
  └── Bridge                    (Promise-as-Task, observer, JS-initiated promise)

ElevenLabs.Agents.Audio (shared internal interfaces)
  └── Default and Unity-routed playback paths

ElevenLabs.Agents.Components (optional, separate package)
  └── Drop-in MonoBehaviours: orb visualizer, push-to-talk, conversation logger
```

### Why hybrid

WebGL has no threads, no `System.Net` sockets, no `UnityEngine.Microphone`, no native WebRTC. Every networking and audio operation goes through the browser via jslib regardless of what we build. Given that constraint: bridge to a hand-written JS layer we'd have to maintain, or bridge to `@elevenlabs/client` which already solves WebSocket, WebRTC, mic capture, playback, jitter buffering, reconnection, and the browser-quirks long tail? The latter, every time.

Desktop, mobile, and XR are the inverse: full BCL access, `System.Net.WebSockets` works, `UnityEngine.Microphone` works, audio playback through `AudioClip` works. A C# implementation of the WebSocket protocol is straightforward and produces a lighter, cleaner result than wrapping anything.

The hybrid puts engineering effort where it's most needed (native platforms) and reuses existing work where reuse is cheapest (WebGL).

### The bridge primitives

The [jslib interop model](https://docs.unity3d.com/Manual/web-interacting-browser-scripting.html) is asymmetric: **C# → JS calls are synchronous** ([`[DllImport("__Internal")]`](https://docs.unity3d.com/Manual/web-interacting-browser-js-to-unity.html) - real interop, not message passing), but **JS → C# is fire-and-forget via [`SendMessage`](https://docs.unity3d.com/Manual/web-interacting-browser-unity-to-js.html)** with a single string argument, delivered on the next Unity frame. C# cannot block on a Promise.

Three small primitives, all sharing the same registry-and-signal shape, handle every JS interaction we need:

**Promise-as-Task** (C#→JS one-shot, e.g. `StartSession`). C# allocates an ID and a [`TaskCompletionSource<T>`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.taskcompletionsource-1), calls jslib synchronously to kick off the operation, jslib stores the result by ID when the Promise settles and fires `SendMessage('Bridge', 'OnPromiseResolved' | 'OnPromiseRejected', id)`. The Bridge [`MonoBehaviour`](https://docs.unity3d.com/ScriptReference/MonoBehaviour.html) pulls the payload via a synchronous getter on the next frame and completes the TCS. Results are pulled-once and deleted on read; a finalizer-based `ReleaseResult` covers abandoned Tasks.

**Observer** (JS→C# streams, e.g. conversation events). C# registers a handler with the bridge against an observer ID, then passes that ID into whatever JS-side source emits events (a `Conversation`, a clipboard listener, etc.). The JS source pushes payloads into a per-observer queue and signals via `SendMessage('Bridge', 'OnObserverEvent', "{observerId}:{seq}")`. The Bridge retrieves payloads via synchronous getter and dispatches to the registered handler on Unity's main thread. The primitive is intentionally generic - conversation-specific dispatch (routing by event type tag in the payload) lives in the conversation consumer, not the bridge.

**JS-initiated promise** (JS→C# request awaiting a result, e.g. client tools). JS fires `SendMessage('Bridge', 'OnToolInvocation', "{invocationId}:{toolName}:{argsJson}")` and holds a Promise open. C# dispatches to the registered tool handler, then synchronously calls back into jslib with `ResolveToolInvocation(invocationId, resultJson)` or `RejectToolInvocation(invocationId, errorJson)`, which settles the held Promise. Binary payloads (where needed, e.g. PCM frames in Unity-routed audio) use [emscripten heap pointers via `_malloc` / `HEAPU8`](https://docs.unity3d.com/Manual/web-interacting-browser-js.html) rather than JSON, passed across SendMessage as `ptr,length` strings.

All three primitives are domain-agnostic, thoroughly unit-testable, and reusable for any future Unity WebGL surfaces we build (MediaSession, future Web APIs, or other JS SDK concepts / features we’d want to bridge).

### Conversation callbacks: a consumer of the observer primitive

`@elevenlabs/client` today requires callbacks at construction time; reliable late registration isn't supported. The v0 C# API must therefore collect all event subscribers before `StartSession` and pass the full set into the jslib call at session start. Two construction-time API shapes are reasonable; see open questions.

Late-registration support in `@elevenlabs/client` (registering listeners for events via a regular observer pattern) is on our roadmap. When it lands, the conversation consumer evolves to use the observer primitive's full lifecycle (`RegisterObserver` / `DisposeObserver` at any time), and the public C# API can move to a standard `.NET` event pattern where `+=` works after `StartSession`. The bridge primitive doesn't change. Building it generically now is what makes this evolution local.

### Audio routing and the latency cost of the bridge

**Latency.** The bridge adds **one Unity frame** (\~16ms at 60fps) to control-plane operations (`StartSession`, `SendUserMessage`, conversation events, tool resolution). For network-bound operations dominated by 50–500ms server round-trips, this is invisible.

The default audio path on WebGL stays entirely in JavaScript (`@elevenlabs/client` → Web Audio → speakers); C# is not in the loop, and bridge latency on audio is **zero**.

**Where the voice plays from.** Two customer expectations exist: "audio just works" (default speakers, browser audio) and "audio plays from a `GameObject` with Unity's 3D spatialization" (NPCs, holographic avatars, XR companions). The second is the differentiated value of Unity-as-platform; we need to support it from day one or the SDK feels half-baked.

On native, this is the canonical Unity pattern: PCM into an `AudioClip` via `PCMReaderCallback`, fed into an `AudioSource` on a `GameObject`. Unity's audio engine handles spatialization.

On WebGL, we offer two modes behind an `AudioSink` interface on the public API. **Default mode:** `@elevenlabs/client` plays via Web Audio - zero bridge crossings on audio, no Unity audio integration. **Unity-routed mode:** jslib intercepts decoded PCM frames before Web Audio, forwards them across the bridge to C# (binary payloads via heap pointer rather than JSON, for efficiency), and C# feeds them into an assigned `AudioSource`. Cost: one frame of latency on the audio path and \~50 bridge crossings/sec per session, both well within tolerance.

Unity-routed mode requires `@elevenlabs/client` to expose a PCM intercept hook. If that doesn't exist today, adding it is in-scope work, scheduled with v0.3's spatial audio milestone.

---

## Code generation

Both implementations have a large surface that mirrors something we already maintain: the WebSocket protocol on native, and `@elevenlabs/client`'s TypeScript API on WebGL. Hand-writing this twice creates drift risk. We code-generate it.

- **Native:** OpenAPI spec → C# DTOs, serialization, and a typed message router. Custom TypeScript-based generator targeting Unity-runtime characteristics (IL2CPP friendliness, allocation behavior, AOT compatibility).
- **WebGL:** `@elevenlabs/client`'s `.d.ts` → C# façade classes and jslib shims, including the observer wiring for conversation callbacks. Custom TypeScript-based generator using the TypeScript Compiler API.
- **Build integration:** generated code is committed and regenerated by a build step; CI fails if the committed output is stale. This combines IDE navigability and review visibility with guaranteed freshness, and is a workflow we'd like to adopt more broadly across ElevenLabs SDKs going forward.

What's **not** generated: the connection lifecycle, the public `Conversation` API, the bridge primitives themselves, audio capture/playback. These encode product decisions, not protocol shape.

---

## Distribution

**Phase 1: UPM via Git URL.** Standard for developer-focused Unity SDKs. Customers add a line to `manifest.json`:

```json
"com.elevenlabs.agents": "https://github.com/elevenlabs/unityelevenlabs-unity-sdk.git#v0.1.0"
```

Zero infrastructure, version-pinned via git tags, no Git LFS.

**Phase 2: [UPM scoped registry](https://docs.unity3d.com/6000.1/Documentation/Manual/upm-scoped.html)** (`com.elevenlabs.*` on [npmjs.com](http://npmjs.com), GitHub packages or self-hosted) once the SDK stabilizes. Gives semantic versioning, discoverability, and the install UX Unity developers expect.

**Asset Store:** deferred. Heavy publishing process, less developer-friendly UX. Worth considering once we have a paying Unity customer base. It could give us a more “approved by Unity” feel, but we expect we don’t actually need that to gain traction initially.

---

## Roadmap

**v0.1 - Core session.** `StartSession`, public and private agents, WebSocket transport (native) and `@elevenlabs/client` bridge (WebGL), audio capture and playback in default mode across all platforms, core events (`OnConnect`, `OnDisconnect`, `OnMessage`, `OnError`, `OnStatusChange`, `OnModeChange`), basic interruption handling, the three bridge primitives, codegen pipelines, UPM-git-URL distribution. Target: \~7–9 engineer-weeks.

**v0.2 - Extensibility.** Client tools, conversation overrides, feedback submission, improved reconnection, transcript export, Components package. Target: \~4–6 weeks after v0.1.

**v0.3 - Platform polish.** XR-tested capture (Quest, Vision Pro, PCVR), spatial audio via `AudioSink` (native and WebGL paths, including the `@elevenlabs/client` PCM intercept hook if needed), scoped registry distribution, perf pass. Target: \~4–6 weeks after v0.2.

**Future (post-1.0).** Native WebRTC as a second transport inside `NativeConversation`, behind `IAgentTransport` (WebGL already has WebRTC via `@elevenlabs/client`). Most likely candidate: Unity's first-party [`com.unity.webrtc`](https://docs.unity3d.com/Packages/com.unity.webrtc@3.0/manual/index.html) - IL2CPP-compatible, libwebrtc-based, maintained by Unity Technologies. Caveats: WebGL and UWP unsupported (acceptable - WebGL gets WebRTC via the bridge), XR not in the supported matrix (same gap as LiveKit Unity), and it's a video-streaming-oriented package so we'd be using a fraction of what it ships. **WebTransport** as a forward-looking third native transport - HTTP/3-and-QUIC-based with attractive properties for realtime media (datagrams, multiplexed streams without head-of-line blocking), but unspecced and not yet supported by our server; flagged as opportunity, not plan. Cancellation across async operations once `@elevenlabs/client` and the native protocol support it (the bridge already accepts `CancellationToken` as a no-op). Console targets (PS5/Xbox/Switch). Asset Store.

---

## Honest risks

- **Two implementations to maintain.** Bugs that reproduce on WebGL but not native (or vice versa) are harder to triage because the code paths are entirely separate. Mitigation: codegen reduces what's hand-written on each side; integration tests run on both.
- **`@elevenlabs/client` coupling on WebGL.** Each Unity SDK release pins a specific JS version, bundled into the WebGL output. Version bumps require diff review of regenerated façade code. Coupling is real but bounded - it's our own package, not a vendor's.
- **Native v0 ships WebSocket-only.** No WebRTC means TCP head-of-line blocking under poor networks, no built-in jitter buffer or echo cancellation. Feature-complete protocol, but network resilience and built-in audio processing are weaker than WebRTC. For Unity's typical use cases (foreground, often headset-equipped) the tradeoff is acceptable; WebRTC remains on the roadmap.
- **Custom codegen tooling.** Two custom generators add upfront cost and ongoing maintenance. Bounded scope mitigates this; off-the-shelf alternatives produce output we'd fight harder.

---

## Open questions

1. **Repository structure.** Standalone repo or inside `elevenlabs/packages` alongside the JS family? Soft preference for in-monorepo, since WebGL depends on `@elevenlabs/client` and codegen is smoother when sources are local.
2. **`@elevenlabs/client` version pinning.** Bundle a specific version into the Unity WebGL build, or allow customers to supply their own? Bundling is simpler and safer.
3. **Components package boundary.** Separate UPM package or samples inside the main package?
4. **C# async style.** `Task` (works everywhere, allocates on GC) vs. `UniTask` (Unity-idiomatic, zero-allocation, adds a dependency).
5. **Minimum Unity version.** 2021 LTS floor, or push to Unity 6 for audio improvements?
6. **Construction-time event subscription API shape.** `ConversationOptions` builder with per-event setters, or config object literal with handler fields? Both produce the same jslib call.
7. **`@elevenlabs/client` PCM intercept hook.** Audit whether it exists today; add it if not. Small in-scope work for v0.3.

---

## Decision requested

1. Hybrid architecture: native C# on desktop/mobile/XR, `@elevenlabs/client` bridge on WebGL.
2. The three bridge primitives as core SDK infrastructure.
3. Custom TypeScript-based codegen for native protocol DTOs and WebGL façade, with committed output \+ CI gate.
4. UPM-git-URL → UPM-scoped-registry distribution path.
5. Three-stage v0.1/v0.2/v0.3 roadmap shape.
6. Assign owners for the open questions above.

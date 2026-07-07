# Audio generator engine — eliminate the streaming-buffer pre-fill via Unity 6.3's `IAudioGenerator` / `GeneratorInstance`

**Status:** Active, 2026-06-29 — supersedes [`audio-output-filter-engine.md`](audio-output-filter-engine.md). Branch: TBD (suggest `feat/audio-generator-engine` after sign-off).
**Driver:** [`bob-alignment-redesign.md`](bob-alignment-redesign.md) compensates for Unity's structural 800 ms streaming-`AudioClip` pre-fill (written up at [`Docs~/unity-issues/streaming-audioclip-prefill-depth.md`](../unity-issues/streaming-audioclip-prefill-depth.md)). The original v0.2 plan was to bypass the pre-fill with `OnAudioFilterRead` ([`audio-output-filter-engine.md`](audio-output-filter-engine.md)); Unity 6.3 LTS introduced a purpose-built scriptable-audio surface ([`Audio.IAudioGenerator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.IAudioGenerator.html) + [`Audio.GeneratorInstance`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.GeneratorInstance.html)) that eliminates the pre-fill *structurally* without the supplied-source-as-parent-hint ergonomic break, without a hand-rolled resampler (the generator returns its own Setup and Unity handles rate/channel conversion), and with Burst-compileable realtime structs for audio-thread safety.
**Assumes:** Bob-alignment redesign has shipped (v0.1 audio output path) — kept as the legacy code path until the generator engine soaks.
**Out of scope:** WebGL audio (the generator surface is almost certainly not on WebGL — same FMOD-vs-WebAudio split as `OnAudioFilterRead`; WebGL keeps its existing `web-audio-sink.ts` path). Confirming that during step 0.

---

## Why this supersedes the filter engine plan

| Concern | `OnAudioFilterRead` plan ([`audio-output-filter-engine.md`](audio-output-filter-engine.md)) | `IAudioGenerator` plan (this) |
|---|---|---|
| Pre-fill | ~5 ms (one DSP buffer) — workaround | Structurally zero — pre-fill is a streaming-`AudioClip` property the generator path bypasses entirely |
| Supplied-source ergonomics | Breaking change: SDK creates child `GameObject` under user's AudioSource; user's literal source isn't played | Non-breaking: SDK adds a sibling `IAudioGenerator` `MonoBehaviour` to the user's GameObject and binds via [`AudioSource.generator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AudioSource-generator.html). User's literal AudioSource plays the agent's voice. |
| Resampler | Hand-rolled linear-interp ([`audio-output-filter-engine.md`](audio-output-filter-engine.md) step 2) | Unity handles rate/channel conversion via the [`Configure(...)` → `Setup`](https://docs.unity3d.com/6000.3/Documentation/Manual/audio-scriptable-processors-example-creating-a-generator.html) negotiation. We declare our input rate; Unity adapts. |
| Audio-thread safety | Plain managed code + `lock`/`volatile` discipline | `[BurstCompile]`'d value-type `IRealtime` struct + value-type message pipe |
| Min Unity version | 6.0 LTS (current floor) | **6.3 LTS** — requires a floor bump |
| Future-proofing | Could be deprecated in a future Unity version once `IAudioGenerator` matures | First-party API, two-year LTS support window (until Dec 2027) |

The floor-bump cost (next section) is the only real downside. The benefits compound: cleaner ergonomics, less code to maintain, better audio-thread guarantees.

## Unity 6.3 LTS floor bump

[Unity 6.3 LTS](https://unity.com/blog/unity-6-3-lts-is-now-available) shipped on 2025-12-04 (latest patch as of writing: `6000.3.6f1`, which is what the dev environment already runs against). LTS support runs through December 2027. Risk of forcing existing users to upgrade is real but bounded: Unity 6.3 is a drop-in upgrade from 6.0 for the vast majority of projects (no API breaks in our consumer surface), and the SDK is still at v0.1 — no large installed base to break.

**Files to update:**

- [`README.md`](../../README.md) line 7: `Unity 2023.1 or later (Unity 6 LTS recommended)` → `Unity 6.3 LTS or later (6000.3.0f1+)`.
- [`COMPATIBILITY.md`](../../COMPATIBILITY.md) lines 5–9: replace the `Unity 2023.1 or later (Unity 6 LTS 6000.3.6f1 tested)` block with a Unity 6.3 LTS rationale paragraph that:
  - States the new floor: `Unity 6.3 LTS (6000.3.0f1 or later)`.
  - Drops the obsolete `Awaitable` rationale (Unity 6.3 includes Unity 2023.1, so the existing `Awaitable` justification is implied — keep a brief mention but stop calling out 2023.1 as the explicit floor).
  - Adds an explicit "Why Unity 6.3?" rationale: the SDK's audio output engine uses [`IAudioGenerator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.IAudioGenerator.html) / [`GeneratorInstance`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.GeneratorInstance.html), introduced in Unity 6.3. Link to the [`streaming-audioclip-prefill-depth.md`](../unity-issues/streaming-audioclip-prefill-depth.md) writeup for the back-story.
- [`package.json`](../../package.json) line 6: `"unity": "6000.0"` → `"unity": "6000.3"`. (Unity Package Manager uses this for version-floor enforcement; mismatched UPM clients refuse to import.)
- [`CHANGELOG.md`](../../CHANGELOG.md): add an `[Unreleased]` entry calling out the breaking change.
- Any `unityRelease` field in `package.json` (none currently — verify during step).

**Non-files to update:** the existing `.unity` scenes don't pin a Unity version. The CI workflow at `.github/workflows/` (if any) may need its Unity Hub image bumped — verify during step.

## IAudioGenerator surface summary

**Documentation:**
- Manual: [Generators](https://docs.unity3d.com/6000.3/Documentation/Manual/audio-scriptable-processors-generators.html)
- Manual: [Example — Create a generator](https://docs.unity3d.com/6000.3/Documentation/Manual/audio-scriptable-processors-example-creating-a-generator.html)
- Scripting: [`IAudioGenerator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.IAudioGenerator.html), [`GeneratorInstance`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.GeneratorInstance.html), [`ControlContext.AllocateGenerator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.ControlContext.AllocateGenerator.html)

**The three pieces we'd implement:**

1. **`IRealtime` struct** (audio thread) — burst-compileable `struct` implementing `GeneratorInstance.IRealtime`. The `Process(in RealtimeContext, Pipe, ChannelBuffer, Arguments) → Result` method fills the `ChannelBuffer` (`buffer[ch, frame]`) by pulling samples from a SPSC ring shared with the producer (network) thread. Capabilities: `isFinite = false`, `isRealtime = true`, `length = null`.
2. **`IControl<Realtime>` struct** (control thread) — `Configure(ControlContext, ref Realtime, in AudioFormat, out Setup, ref Properties)` negotiates the output format. We declare `setup = new Setup(format.speakerMode, format.sampleRate)` to inherit Unity's preferred rate (so Unity resamples our 16 kHz clip-rate input to the output device's 48 kHz). `Dispose`, `Update`, `OnMessage` are wire-up stubs unless we need to pipe control events later.
3. **`IAudioGenerator` MonoBehaviour** (control thread, scene-attached) — the user-facing component. `CreateInstance(ControlContext, AudioFormat?, CreationParameters) → GeneratorInstance` returns a new instance pairing `Realtime` + `Control`. The component is added to the user's supplied-AudioSource GameObject (or our hidden host) and bound via `audioSource.generator = thisComponent`.

**Shared state between control and realtime:** the SPSC ring buffer of incoming PCM samples lives in a heap-allocated wrapper (likely `NativeArray<float>` for Burst compatibility); both structs hold a reference via a Pipe or a shared handle. Per the [generator manual](https://docs.unity3d.com/6000.3/Documentation/Manual/audio-scriptable-processors-generators.html): avoid allocations, locks, blocking I/O, logging, and exceptions on the audio thread.

## Architecture: how this plugs into the existing `IAudioOutputEngine` seam

The seam (`IAudioOutputEngine` at [`Runtime/Native/IAudioOutputEngine.cs`](../../Runtime/Native/IAudioOutputEngine.cs)) stays untouched — it's the right shape. A new `UnityGeneratorAudioOutputEngine : IAudioOutputEngine` mirrors today's [`UnityAudioOutputEngine`](../../Runtime/Native/UnityAudioOutputEngine.cs) constructor signature and lifecycle methods:

```
UnityGeneratorAudioOutputEngine(AudioSource? suppliedSource, OutputDeviceConfig? device)
  ├─ Constructor: attach AudioGeneratorComponent (MonoBehaviour, IAudioGenerator) to
  │    suppliedSource.gameObject (or hidden host); wire audioSource.generator = component
  ├─ Start(FormatConfig, drainCallback): the drain callback is what the realtime struct calls
  │    on the audio thread to pull PCM samples; AudioSource.Play() starts the generator
  ├─ Stop(): AudioSource.Stop()
  ├─ Tick(elapsedSeconds): no-op
  └─ Dispose(): tear down via ControlContext.Destroy(instance); detach component; restore
       audioSource.generator = null (preserved snapshot semantics from today's engine)
```

The Fake test seam ([`FakeAudioOutputEngine`](../../Tests/Editor/Native/FakeAudioOutputEngine.cs)) doesn't change. The audible-head wall-clock model in [`UnityAudioSourceOutput.ComputeWallClockRms`](../../Runtime/Native/UnityAudioSourceOutput.cs) likely simplifies to a no-op anchor path (drain head ≈ audible head when pre-fill is zero) — same simplification the filter-engine plan called out.

## Open questions to settle during step 0 + step 1

Three of these were resolvable from official Unity docs in step 0; the remaining two need code-shape decisions in step 3.

- **~~Does `IAudioGenerator` work on WebGL?~~ Resolved (step 0):** **No.** Per Unity's own [audio-team Q3 2025 status update](https://discussions.unity.com/t/audio-status-update-q3-2025/1681867): *"Scriptable Processors don't work on WebGL. We are investigating how to achieve feature parity, but it's a larger body of work."* WebGL keeps its `web-audio-sink.ts` path — preprocessor-gated as designed.
- **~~Binding mechanism for `audioSource.generator`.~~ Resolved (step 0):** **Public settable property — assign from code.** Per the [`AudioSource.generator` API ref](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AudioSource-generator.html): `public Audio.IAudioGenerator generator;` with a public set accessor. The 6.3 "What's New" describes the inspector picker (the manual example's path), but the property is also settable at runtime — which is the path we need (we attach the component dynamically). We'll snapshot the pre-session value on construction and restore it on `Dispose`, mirroring today's `clip`/`loop`/`volume` snapshot pattern.
- **~~Burst dependency.~~ Resolved (step 0):** **Optional.** Per the [`GeneratorInstance.IRealtime` API ref](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.GeneratorInstance.IRealtime.html): *"implementations can annotate this with `Unity.Burst.BurstCompileAttribute` to have it compiled with Burst"* — Burst is a performance optimization, not a hard requirement. We ship the realtime struct without `[BurstCompile]` to keep `com.unity.burst` out of `package.json` dependencies (one fewer transitive package for consumers), with a TODO to add the attribute behind an optional `BURST` define if measurements ever require it. The struct shape stays Burst-compatible (value-type, no managed refs) so the toggle is one-line in the future.
- **Coexistence with `AudioSource.clip`.** When `audioSource.generator` is set, does `audioSource.clip` get ignored or do they layer? The user's supplied AudioSource may have a clip assigned — does setting the generator override or coexist? Settle empirically in step 1's characterization test (snapshot the user's `clip` regardless — that's our existing pattern — and verify the generator takes priority during `Play()`).
- **Drain-callback bridging.** Our existing `IAudioOutputEngine.Start(FormatConfig, Func<float[], int> drainCallback)` takes a managed `Func<float[], int>`. A `[BurstCompile]`'d `IRealtime` struct couldn't capture a managed delegate, but since we're shipping un-Bursted (above), the struct *can* hold a managed reference — to the SPSC ring (step 2) at minimum, possibly the delegate itself. Cleanest shape: the struct reads from the shared ring directly; the control side wires the producer into the ring at `Start`. Locked in during step 3.
- **`ControlContext.builtIn` vs custom contexts.** Manual example uses `ControlContext.builtIn` everywhere; the [`AllocateGenerator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.ControlContext.AllocateGenerator.html) ref mentions creating contexts "purely in code." We'll use `builtIn` (it's the AudioSource-integrated context — the only one that makes sense for our supplied-source ergonomics).

### Empirical sanity checks (folded into step 1, confirmed 2026-06-29)

The step 0 plan listed a unity-mcp-driven 20-line MonoBehaviour probe as a belt-and-braces check that the docs match reality. With unity-mcp revoked at the start of step 1, those sanity checks were folded into the characterization test (`UnityGeneratorAudioOutputEngineCharacterizationTest`) and confirmed once the user re-approved MCP mid-step. All three landed green: `audioSource.generator = component` runtime assignment binds, `Process` fires after `Play()` (187.6 Hz cadence; zero sync pre-fill), and the generator takes priority over a coexisting streaming `AudioClip` during playback. See step 1's "Measured values" table for the full data.

## WebGL — confirmed out of reach in 6.3

Unity's WebGL audio backend doesn't expose a scriptable pipeline (per the writeup at [`Docs~/unity-issues/webgl-scriptable-audio-pipeline.md`](../unity-issues/webgl-scriptable-audio-pipeline.md)). `AudioClip.PCMReaderCallback`, `OnAudioFilterRead`, `AudioRenderer`, `AudioListener.GetOutputData` are all unsupported there. The scriptable-processor surface falls in the same bucket — it's a FMOD-side concept, and WebGL uses Web Audio.

**Confirmed in step 0** via Unity's [audio-team Q3 2025 status update](https://discussions.unity.com/t/audio-status-update-q3-2025/1681867):

> Scriptable Processors don't work on WebGL. We are investigating how to achieve feature parity, but it's a larger body of work.

**WebGL stays on the existing `web-audio-sink.ts` path.** The SDK ships two output engines, gated by `#if UNITY_WEBGL && !UNITY_EDITOR` (or the equivalent preprocessor pattern already used in [`UnityAudioSourceOutput.cs`](../../Runtime/Native/UnityAudioSourceOutput.cs)). Same shape as today's split. If Unity ships scriptable processors on WebGL in a future LTS, that's a follow-up plan; not blocking this one.

---

## Execution sequencing

### Step 0 — Floor bump + scope verification

- [x] Update [`README.md`](../../README.md) min-version line
- [x] Update [`COMPATIBILITY.md`](../../COMPATIBILITY.md) Unity version section (drop the obsolete 2023.1 framing; add Unity 6.3 rationale + audio-generator link)
- [x] Update [`package.json`](../../package.json) `unity` field (`6000.0` → `6000.3`)
- [x] Add `[Unreleased]` entry to [`CHANGELOG.md`](../../CHANGELOG.md) for the breaking floor bump
- [x] Check CI workflows under `.github/workflows/` for any pinned Unity version; bump if needed — already pinned to `6000.3.6f1` in both `unity-tests.yml` and `integration.yml`; no change needed
- [x] Verify WebGL doesn't support `IAudioGenerator` — **confirmed unsupported** per Unity audio-team [Q3 2025 status update](https://discussions.unity.com/t/audio-status-update-q3-2025/1681867) ("Scriptable Processors don't work on WebGL"). WebGL stays on `web-audio-sink.ts`; the SDK ships two engines gated by preprocessor (same shape as today's split)
- [x] Verify `[BurstCompile]` is optional vs required for `GeneratorInstance.IRealtime` — **confirmed optional** per [`GeneratorInstance.IRealtime`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.GeneratorInstance.IRealtime.html) docs ("implementations *can* annotate this with `Unity.Burst.BurstCompileAttribute`"). We ship without `com.unity.burst` as a hard dep; keep the struct Burst-compatible so it's a one-line toggle later
- [x] Confirm `AudioSource.generator` binding mechanism — **confirmed public settable property** per [`AudioSource.generator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AudioSource-generator.html) (`public Audio.IAudioGenerator generator { get; set; }`). Runtime `audioSource.generator = component` assignment is supported alongside the inspector picker. Empirical PlayMode confirmation deferred to step 1's characterization test (unity-mcp connection was revoked during this session, so the docs-only confirmation stands; step 1 is the belt-and-braces check)

### Step 1 — Characterization test (PlayMode)

Mirror [`UnityAudioOutputEngineCharacterizationTest`](../../Tests/Runtime/Native/UnityAudioOutputEngineCharacterizationTest.cs) for the generator path at `Tests/Runtime/Native/UnityGeneratorAudioOutputEngineCharacterizationTest.cs`:

- Allocate a minimal `IAudioGenerator` MonoBehaviour with an `IRealtime` struct that counts `Process` calls
- Bind via `audioSource.generator = component`, `audioSource.Play()`
- Measure: sync fires inside `Play()` (should be 0), ongoing rate (`Process` calls per second), `ChannelBuffer.frameCount` per call, `ChannelBuffer.channelCount`
- Loose-band assertions; `Assert.Inconclusive` fallback for batchmode
- Log values for fake calibration

- [x] Characterization test file written + .meta stamped via test runner ([`Tests/Runtime/Native/UnityGeneratorAudioOutputEngineCharacterizationTest.cs`](../../Tests/Runtime/Native/UnityGeneratorAudioOutputEngineCharacterizationTest.cs))
- [x] PlayMode run captures empirical values via unity-mcp (re-approved on the live `Getting Started` Editor; `TestRunnerApi.Execute` queued the PlayMode test, results read from `~/Library/Logs/Unity/Editor-prev.log`)
- [x] Plan updated with measured values; open questions resolved (see below)

**Measured values (2026-06-29, macOS 26.5.1 arm64, Unity 6000.3.6f1, 48 kHz/2-channel system audio device, `Getting Started` Editor in PlayMode):**

| Quantity | Observed | Notes |
|---|---|---|
| `Process` fires synchronously inside `AudioSource.Play()` | **0** | Confirms the IAudioGenerator path has zero structural pre-fill — the core driver of this plan. Compare ~8 PCMReaderCallback fires × ~100 ms = ~800 ms pre-fill burst in the streaming-`AudioClip` path ([`streaming-audioclip-prefill-depth.md`](../unity-issues/streaming-audioclip-prefill-depth.md)). |
| Ongoing `Process` cadence | **187.6 Hz** (938 fires / 5 s) | One fire per DSP buffer: 256 frames @ 48 kHz output device ≈ 5.33 ms / 187.5 Hz. Matches the prediction. |
| `ChannelBuffer.frameCount` | **256 (constant)** | Standard Unity 6 DSP buffer size on this hardware. |
| `ChannelBuffer.channelCount` | **2** | Stereo system output — Unity adapts Setup's mono declaration to device speaker mode. |
| `audioSource.generator = component` (runtime assignment) | **Sticks; Process fires after `Play()`** | Confirms the [`AudioSource.generator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AudioSource-generator.html) public setter is runtime-usable, not just an inspector picker. |
| Clip vs generator coexistence | **Generator takes priority during playback** | Clip's `PCMReaderCallback` fired 50 times during `AudioClip.Create()` (the streaming pre-fill burst — same number `UnityAudioOutputEngineCharacterizationTest` measures for the legacy path) but 0 times during the 5-second playback window. Snapshot+restore of `audioSource.clip` is sufficient — no need to actively null it on `Start`. |

**Open questions resolved in step 1:**

- **~~Coexistence with `AudioSource.clip`.~~ Resolved:** generator takes priority during playback. Production engine just needs the existing snapshot/restore pattern for `clip` (no active null-out needed). The 50 create-time fires are the streaming-clip's own pre-fill property — the generator path never creates a streaming clip, so this is moot for production.
- **~~Drain-callback bridging.~~ Resolved:** the realtime struct accesses shared state via `Interlocked`/`Volatile` on an enclosing class's static fields (in the test) — for production (settled empirically during step 3 compile), Unity's `GeneratorInstance.IControl<TRealtime>` generic constraint is `unmanaged` (a struct whose entire field graph is unmanaged), which forbids storing a managed bridge reference inline. The realtime + control structs carry an `int` handle into a process-wide `ConcurrentDictionary<int, AgentAudioGeneratorBridge>` (lock-free `TryGetValue` on the audio thread) instead. Bridge is registered at construction with a monotonic handle, deregistered on `Dispose`. Keeps the Burst-compatibility hatch open since the structs stay value-type with only `int` fields.
- **~~`ControlContext.builtIn` vs custom contexts.~~ Resolved:** step 0 already concluded `builtIn`; step 1 confirms it works end-to-end with `AudioSource.generator` binding.

### Step 2 — SPSC ring buffer (audio-thread safe)

Standalone helper at `Runtime/Native/AudioPcmRing.cs` (or extend the existing ring if it can be reused as-is):

- Backed by `NativeArray<float>` for Burst compatibility
- Producer (network thread): `Write(ReadOnlySpan<float>)` — non-blocking, drops on overflow
- Consumer (audio thread): `Read(Span<float>) → int` — non-blocking, returns sample count actually read
- Lock-free using atomic read/write indices (`Interlocked.Read` / `Interlocked.Exchange` if `NativeArray<long>` for the indices)
- Edit-Mode unit tests at `Tests/Editor/Native/AudioPcmRingTests.cs`: empty read, full write overflow, cross-boundary read/write, producer/consumer race smoke

- [x] Ring implementation ([`Runtime/Native/AudioPcmRing.cs`](../../Runtime/Native/AudioPcmRing.cs)) — `NativeArray<float>`-backed SPSC ring, drop-on-overflow producer, non-blocking consumer, `Interlocked.Read`/`Exchange` on monotonic `long` indices (64-bit atomicity across 32-bit targets)
- [x] Unit tests cover empty/full/boundary/race cases ([`Tests/Editor/Native/AudioPcmRingTests.cs`](../../Tests/Editor/Native/AudioPcmRingTests.cs)) — 12 tests, all green via `pnpm --dir TestProject run test` (440/440 passing). Race smoke streams a monotonic counter through producer/consumer threads for ~1 s and asserts the consumer received the full produced prefix in order with no gaps

### Step 3 — `UnityGeneratorAudioOutputEngine : IAudioOutputEngine` + companion structs

New files under `Runtime/Native/`:

- `UnityGeneratorAudioOutputEngine.cs` — implements `IAudioOutputEngine`; same constructor signature + lifecycle as today's [`UnityAudioOutputEngine`](../../Runtime/Native/UnityAudioOutputEngine.cs)
- `AgentAudioGeneratorComponent.cs` — `MonoBehaviour, IAudioGenerator`. Holds reference to the SPSC ring. `CreateInstance` allocates the generator pairing `Realtime` + `Control`
- `AgentAudioRealtime.cs` — `struct, GeneratorInstance.IRealtime`. `[BurstCompile]`'d (or plain managed if Burst optional). `Process` pulls from the ring, writes to `ChannelBuffer`
- `AgentAudioControl.cs` — `struct, GeneratorInstance.IControl<AgentAudioRealtime>`. `Configure` declares `Setup` matching the host's format

Supplied-source ergonomics: add `AgentAudioGeneratorComponent` as a sibling component to the user's supplied AudioSource. Bind via `audioSource.generator = component`. Capture pre-session `generator` value for restoration on `Dispose` (same snapshot/restore pattern as today's engine for `clip`/`loop`/`volume`).

- [x] Engine class with all `IAudioOutputEngine` methods ([`Runtime/Native/UnityGeneratorAudioOutputEngine.cs`](../../Runtime/Native/UnityGeneratorAudioOutputEngine.cs)) — same constructor/lifecycle as [`UnityAudioOutputEngine`](../../Runtime/Native/UnityAudioOutputEngine.cs) plus `generator` field added to the supplied-source snapshot set
- [x] Generator MonoBehaviour wires `IAudioGenerator` correctly ([`Runtime/Native/AgentAudioGeneratorComponent.cs`](../../Runtime/Native/AgentAudioGeneratorComponent.cs)) — `CreateInstance` pairs `AgentAudioRealtime` + `AgentAudioControl` via `ControlContext.AllocateGenerator`; co-located in the same file as `AgentAudioGeneratorBridge` (heap-allocated shared state)
- [x] Realtime + Control structs build clean without Burst ([`Runtime/Native/AgentAudioRealtime.cs`](../../Runtime/Native/AgentAudioRealtime.cs), [`Runtime/Native/AgentAudioControl.cs`](../../Runtime/Native/AgentAudioControl.cs)) — `[BurstCompile]` left off per step 0's finding. Unity's `IControl<TRealtime>` constraint is `unmanaged`, which forbids inline managed refs in the struct, so the structs hold an `int` handle and resolve the bridge via `AgentAudioGeneratorBridge.LookupByHandle` (lock-free `ConcurrentDictionary` read on the audio thread). Promoting to `[BurstCompile]` later is still a one-line flip since the struct shape stays value-typed
- [x] Owned-host + supplied-source paths both compile clean and the existing 440-test Edit-Mode suite still passes (`pnpm --dir TestProject run test`) — step 6 will swap production wiring; step 4 re-runs the fake-driven suite against the new engine via constructor injection for behavioural validation

### Step 4 — Re-run Fake-driven test suite

Same shape as the filter-engine plan's step 4. The seam is implementation-agnostic; existing Fake-driven tests under `Tests/Editor/Native/UnityAudioSourceOutputTests.cs` should pass. The two `[Ignore]`d regression tests likely pass too (pre-fill is structurally zero — well within the wall-clock window).

- [x] Fake-driven tests pass against `UnityGeneratorAudioOutputEngine` via engine constructor injection ([`Tests/Editor/Native/UnityGeneratorAudioOutputEngineTests.cs`](../../Tests/Editor/Native/UnityGeneratorAudioOutputEngineTests.cs)) — mirrors the five engine-seam tests from [`UnityAudioSourceOutputTests`](../../Tests/Editor/Native/UnityAudioSourceOutputTests.cs) (`PushAudio_ResetsEngineVolumeToUserLevel`, `Interrupt_ImmediateCut_RestoresUserVolumeOnEngine`, `SetVolume_ClampedToZeroOne_PropagatedToEngine`, `GetByteFrequencyData_EngineUnavailable_FillsZeros`, `Close_DisposesEngine`) but constructs the generator engine against a real Edit-Mode `AudioSource` instead of injecting `FakeAudioOutputEngine`. Adds parity coverage for the new `generator` snapshot/restore field + owned-host GameObject teardown. 449 tests pass via `pnpm --dir TestProject run test` (440 prior + 9 new). Cadence-calibrated tests in `UnityAudioSourceOutputTests` (sync-pre-fill threshold gate, drain-timing) keep their Fake calibration: they're regression-locking [`UnityAudioSourceOutput`](../../Runtime/Native/UnityAudioSourceOutput.cs)'s threshold-gate logic for the streaming-clip path that production still wires, and step 5 will revisit them as that gate simplifies for the structurally-zero-pre-fill generator path
- [x] `[Ignore]`d regression tests un-ignored if they now pass — N/A: a prior bob-alignment pass already un-ignored every regression test in `UnityAudioSourceOutputTests`. The header comment at line 868 documents the historical state; `grep -n '\[Ignore'` over the test tree returns no matches
- [x] FakeAudioOutputEngine defaults recalibrated if cadence differs from streaming-clip path — kept calibrated to the streaming-clip path. Production currently wires `UnityAudioOutputEngine` (50 × 256 = 12,800 sync pre-fill samples, ~60 Hz ongoing); cadence-calibrated tests in `UnityAudioSourceOutputTests` depend on those defaults. Tests modeling the generator path already opt in via `SyncPrefillCallbackCount = 0` (see `GetVolume_TracksPlaybackPosition_BetweenOngoingDrains`, `Interrupt_ClearsRing_AndResetsPlaybackStartAnchor`, `GetVolume_AfterClearRingThenNewChunk_OnlyReStampsOnFirstRealDrain`). Added a paragraph to [`FakeAudioOutputEngine`](../../Tests/Editor/Native/FakeAudioOutputEngine.cs)'s class XML doc spelling this out + pointing at the generator-path numbers (0 pre-fill, ~187.5 Hz ongoing) and flagging step 6 as the right moment to revisit the defaults once production wiring swaps

### Step 5 — Simplify `UnityAudioSourceOutput` audible-head model

Likely smaller delta than the bob-redesign work. With zero pre-fill, the [`_playbackStartStampTicks` / `_playbackStartRingPos`](../../Runtime/Native/UnityAudioSourceOutput.cs) anchor path can be a no-op (drain head ≈ audible head). The 900 ms threshold gate before `Start` can probably shrink to a single DSP buffer — the generator path doesn't need a primed ring before `Play()` (it just generates silence until the ring fills).

- [x] **Audible-head model — keep as-is.** The wall-clock-interpolated anchor in [`UnityAudioSourceOutput.ComputeWallClockRms`](../../Runtime/Native/UnityAudioSourceOutput.cs) was originally introduced to compensate for the streaming-clip's ~800 ms sync pre-fill putting `_readPosLinear` ahead of the audible head. Under the generator path that pre-fill is structurally zero, so the first-drain stamp captures `preDrainReadPosLinear ≈ 0` and the wall-clock sweep tracks `_readPosLinear` at the input sample rate — i.e., it reads the most-recently-drained samples, which is the desired bob behaviour. Keeping the model has two upsides over simplifying:
  1. **Cross-engine correctness without gating.** [`UnityAudioOutputEngine`](../../Runtime/Native/UnityAudioOutputEngine.cs) stays in tree as the v0.2 soak fallback (per step 6); one audible-head model that works for both engines is cheaper than a per-engine code path, and removes the risk of the fallback engine's bob misaligning by ~800 ms while soaking.
  2. **Wall-clock interpolation between drains is still useful.** Even at the generator path's ~187.5 Hz drain cadence (5.33 ms period) vs. Update's 60 Hz poll cadence (16.67 ms period), a poll may land between two drains; the wall-clock sweep keeps the bob smooth instead of stepping at drain granularity. The pre-history pad-with-silence branch becomes effectively unreachable under zero pre-fill, but the bookkeeping itself is allocation-free and O(windowSamples) = O(80) at 16 kHz × 5 ms — too cheap to be worth removing.
- [x] **Threshold gate — made engine-driven.** Replaced the controller-side `PrefillThresholdMs = 900` constant with [`IAudioOutputEngine.SyncPrefillSampleCount`](../../Runtime/Native/IAudioOutputEngine.cs) + a fixed [`PrefillMarginSamples = 256`](../../Runtime/Native/UnityAudioSourceOutput.cs) DSP-buffer margin, exposed via `output.ComputePrefillThresholdSamples()`. Per-engine values:
  - [`UnityAudioOutputEngine`](../../Runtime/Native/UnityAudioOutputEngine.cs): `12_800` — empirically-measured streaming-clip pre-fill. Today's threshold was `16_000 × 0.900 = 14_400` at 16 kHz; new threshold is `12_800 + 256 = 13_056` (slightly tighter, but the test trigger sizes use `ComputePrefillThresholdSamples() + 256` so the "comfortably past threshold" margin is preserved).
  - [`UnityGeneratorAudioOutputEngine`](../../Runtime/Native/UnityGeneratorAudioOutputEngine.cs): `0` — the IAudioGenerator path's `Process` fires strictly post-`Play()`. Threshold collapses to the 256-sample margin (~16 ms at 16 kHz), so step 6's wiring swap drops first-audio latency by ~880 ms vs. today's gate without any further controller-side changes.
  - [`NullAudioOutputEngine`](../../Runtime/Native/NullAudioOutputEngine.cs): `0` — bare-engine tests don't fire the drain callback.
  - [`FakeAudioOutputEngine`](../../Tests/Editor/Native/FakeAudioOutputEngine.cs): `SyncPrefillCallbackCount × SyncPrefillSampleCountPerCallback` (default `50 × 256 = 12_800`, matching the legacy engine; tests opting into the generator path via `SyncPrefillCallbackCount = 0` automatically collapse the threshold to the margin without any extra wiring).

  Step 6 becomes a true one-line wiring swap as the plan envisioned — the threshold gate self-adjusts to the engine it's pointed at, and the fallback path keeps its ~12,800-sample gate via the unchanged legacy engine.

### Step 6 — Production wiring swap (native only; WebGL unchanged)

Single line change at [`UnityAudioSourceOutput.CreateAsync`](../../Runtime/Native/UnityAudioSourceOutput.cs#L204): swap `new UnityAudioOutputEngine(...)` for `new UnityGeneratorAudioOutputEngine(...)` on native; WebGL preprocessor gate keeps its `web-audio-sink.ts` path.

Keep the old `UnityAudioOutputEngine` in tree as a fallback through v0.2 soak; mark deprecated in XML doc. Removable in a follow-up.

- [x] Native wiring switched — single-line swap in [`UnityAudioSourceOutput.CreateAsync`](../../Runtime/Native/UnityAudioSourceOutput.cs#L215) (`new UnityAudioOutputEngine(...)` → `new UnityGeneratorAudioOutputEngine(...)`). The engine-driven prefill threshold gate (step 5) collapsed from `12_800 + 256 = 13_056` samples to `0 + 256 = 256` samples automatically — no further controller-side changes. Three CreateAsync-driven regression tests in [`UnityAudioSourceOutputTests`](../../Tests/Editor/Native/UnityAudioSourceOutputTests.cs) updated to assert against the new generator semantics (`audioSource.generator != null` post-trigger; restore on Close includes `generator`); they whitelist Unity's batchmode-only `"Realtime generators must obey system sampling rate"` error via `LogAssert.Expect` since Edit-Mode batchmode has no live audio device. 449/449 Edit-Mode tests green via `pnpm --dir TestProject run test`
- [x] WebGL preprocessor gate verified — the entire `Runtime/Native/` asmdef ([`ElevenLabs.Agents.Native.asmdef`](../../Runtime/Native/ElevenLabs.Agents.Native.asmdef)) sets `excludePlatforms: ["WebGL"]`, so neither engine compiles into a WebGL build. No per-file `#if !UNITY_WEBGL` gate needed inside `CreateAsync`; WebGL keeps its `web-audio-sink.ts` path via the separate `Runtime/WebGL/` asmdef + `BridgedSessionLauncher` registration
- [x] Old engine retained + deprecation-marked — [`UnityAudioOutputEngine`](../../Runtime/Native/UnityAudioOutputEngine.cs)'s XML doc now opens with a `<strong>Deprecated for production.</strong>` paragraph pointing at `UnityGeneratorAudioOutputEngine` as the new production wiring and noting the v0.2 soak-fallback retention reason. The class is `internal sealed` so there's no `[Obsolete]` public-API contract to attach; the XML deprecation note is the right shape for the seam

### Step 7 — Manual PlayMode A/B + WebGL build verification

A/B 1–4 are subjective listening tests against the `Getting Started` Editor's `TalkingBox` scene (one MonoBehaviour per `01_Box_Yellow` / `03_Box_Orange` / `04_Box_Red` cube, each binding an `AudioSource` via `ConversationOptions.OutputAudioSource`). Procedure: focus the Editor (required for audio callbacks to fire), Play, walk `PlayerRobot` into a cube's trigger to open the session, listen, exit. To compare against the legacy engine, temporarily swap the one-line wiring at [`UnityAudioSourceOutput.CreateAsync`](../../Runtime/Native/UnityAudioSourceOutput.cs#L220) back to `new UnityAudioOutputEngine(...)`.

**Audio-thread resampler — added 2026-06-29 mid-step.** First A/B 1 attempt revealed a sample-rate bug: declaring `GeneratorInstance.Setup` at our negotiated input rate (e.g. 16 kHz from the server's `agent_output_audio_format`) caused Unity to consume samples 1:1 at the device's 48 kHz output rate, producing ~3× pitch-shifted playback and logging `"Realtime generators must obey system sampling rate"`. Step 0's docs-only assumption "Unity handles rate/channel conversion via the Configure(...) → Setup negotiation" only holds for non-realtime generators; realtime generators (which is what we are — `isRealtime = true` on a live stream) must declare a Setup rate matching the device. The same error was already in the test logs (Edit-Mode batchmode whitelist via `LogAssert.Expect`) but was misattributed to "batchmode-only" — it's the actual PlayMode behaviour too.

Fix: declare `Setup` at the device's output rate (read from `AgentAudioControl.Configure`'s `in AudioFormat format`) and linear-interpolate from the bridge's negotiated input rate to the device rate on the audio thread inside `AgentAudioRealtime.Process`. Bridge publishes `DeviceSampleRate` via `Volatile`-stored int (control thread writes from Configure; audio thread reads from Process). State carried between Process calls: `LastInputSample` + `ResampleFracPos` (both audio-thread-only, no concurrency). Fast path (`deviceRate == inputRate` or `deviceRate == 0`) keeps the pre-resampler 1:1 shape — no resample cost when the negotiated rates already match. Whitelist removed from `UnityAudioSourceOutputTests`'s three `CreateAsync_…` tests: the error no longer fires in batchmode either, because Configure now declares the rate Unity demands.

End-to-end rate flexibility preserved: the server picks `agent_output_audio_format` at handshake time (per the AsyncAPI spec at `Codegen~/schemas/convai-asyncapi.yml`), `NativeWebSocketConnection.ParseFormatString` plumbs it into `FormatConfig.SampleRate`, the engine plumbs that into `AgentAudioGeneratorBridge.InputSampleRate` at `Start`, and the resampler bridges to whatever rate Unity reveals via `Configure`. No hardcoded rate at either end of the audio pipeline. Linear interpolation is a reasonable starting point for the realistic upsampling case (16 kHz → 48 kHz speech); if a future server-side rate change introduces audible artifacts we'd revisit (sinc kernel / windowed-sinc), but speech bandwidth is well below the Nyquist limit at either rate.

Edit-Mode tests: 449/449 pass via `pnpm --dir TestProject run test` after the fix + whitelist removal.

**Cubic Hermite upgrade — added 2026-06-29.** Second A/B 1 attempt (post-rate-fix) confirmed pitch correctness but surfaced a constant background "dust" on speech. Diagnosis: linear interpolation when upsampling 16 → 48 kHz leaves spectral mirror images centered at the source Nyquist (8 kHz); those images fall at 8–16 kHz, in the audible band, and read as harsh sibilant haze on voice. Bumping the prefill threshold was considered but ruled out by the user-reported symptom (constant, not burst-correlated → not underrun).

Fix: replaced linear (2-tap) with cubic Hermite (Catmull-Rom, 4-tap) interpolation in `AgentAudioRealtime.Process`. The state carried across Process calls grew from `{LastInputSample, ResampleFracPos}` to `{Prev0, Prev1, Prev2, ResampleFracPos}` — the 3 most recently consumed input samples become the left half of the interpolation window, with `Prev0` adjacent to the fractional cursor. Per-output-sample cost: 4 mono reads, 6 multiplies, 4 adds in nested-Horner form (`y(t) = ((a*t + b)*t + c)*t + p1`).

**Windowed-sinc polyphase FIR upgrade — added 2026-06-29.** Third A/B 1 attempt (post-Hermite): dust reduced but still present. User-supplied spectrogram confirmed substantial 8–16 kHz energy on speech segments — the source (16 kHz) is bandlimited to <8 kHz by Nyquist, so any energy above 8 kHz is artifact. Hermite's ~12–20 dB stopband isn't enough; ~40–60 dB is the speech-clean bar.

Unity utilities considered first (per the user's prompt): `AudioClip` resampler is tied to the streaming-clip pre-fill we replaced; `AudioLowPassFilter` is only 12 dB/oct (Butterworth biquad), too gentle as a post-filter; `Audio.IAudioProcessor` is the effect-side analog of `IAudioGenerator` (gives hooks, not content); non-realtime `IAudioGenerator` requires a finite stream (can't model our live socket). None fit; rolling our own polyphase FIR is the right call.

Fix: replaced Hermite with a **Kaiser-windowed sinc polyphase FIR resampler** (L=16 taps × P=256 phases, Kaiser β=8 → ~60 dB stopband). Kernel is precomputed on the control thread when `AgentAudioGeneratorBridge.DeviceSampleRate` is set (once per session, ~16 KB table, ~5 ms build time), published via the same `Volatile.Write` release fence that publishes the device rate so the audio thread sees both atomically. Carry grew from 3 floats to a `KernelTaps/2`-sized `float[]` (8 prev samples) for full kernel support across Process-call boundaries. Per-output-sample cost: 16 multiply-adds — still trivial on the audio thread (~770 k ops/sec at 48 kHz output, well below any DSP-budget concern). Cutoff falls at `min(inputRate, outputRate) / 2 × 0.95` (95% of the lower Nyquist) so both upsampling and arbitrary-rate downsampling are anti-aliased without manual tuning.

Edit-Mode tests: 449/449 still pass after each upgrade.

**Peek + Discard refactor — added 2026-06-29.** Fourth A/B 1 attempt (post-FIR): the user reported the audio sounded *worse* than under Hermite, and shared a spectrogram showing broadband vertical lines through the speech segments — the smoking-gun signature of buffer-boundary clicks, not imaging dust.

Diagnosis: a structural sample-loss bug shared by all three resampler iterations (linear, Hermite, polyphase FIR). `bridge.Drain` advanced the producer ring's read cursor by however many samples were drained, but the kernel always reads `half` samples *past* the cursor's consumed position (lookahead for the right taps). Those `half` lookahead samples were drained but never re-fed to the next Process call — the next call's drain started `half` samples ahead of where it should, leaving a gap in the input stream every ~5 ms (Process cadence).

Hermite's 4-tap kernel smeared the 2-sample gap into a tiny tick that perceptually blended with imaging — perceived as part of the "dust." The FIR's 16-tap kernel spread the same gap into clear broadband ringing at the Process cadence — visible as the spectrogram's vertical lines and audibly worse than Hermite despite better stopband.

Fix: added `Peek(Span<float>)` + `Discard(int)` primitives to [`AudioPcmRing`](../../Runtime/Native/AudioPcmRing.cs) — peek reads from the ring without advancing the cursor, discard advances it by an explicit count. Bridge gains `PeekOrPull` (peek with synchronous refill-on-underrun) + `Discard`. The resampler now peeks `maxSampleStart + taps` samples per call, walks all output frames, then discards only `consumed = floor(pos_after_loop)` samples. The `half` lookahead samples stay in the ring for the next call's peek — boundary continuity is exact. The `ResamplerCarry` field disappears: the producer ring itself is now the source of truth across call boundaries.

This is the right fix structurally — applies cleanly to any resampler kernel size, with no per-kernel carry bookkeeping. Edit-Mode tests: 449/449 still pass.

**Kernel quality bump — added 2026-06-29.** Fifth A/B 1 attempt (post peek+discard): vertical click lines gone, formants clean, but the user reported a "subtle rattle" correlated with sibilants. Spectrogram showed residual energy in the 8-18 kHz band at ~-75 to -100 dB — mirror-image leakage from speech content above the kernel's ~7.6 kHz cutoff that the L=16 / β=8 (~60 dB stopband) kernel didn't fully attenuate. Investigated upstream-side knobs: per a dive into the backend, the `agent_output_audio_format` field isn't marked as client-overridable, and no dashboard / admin PATCH endpoint exposes it. Asked the backend team whether client override could be exposed (would unblock device-rate matching, skipping the resampler entirely on 48 kHz devices). Pending backend response, pushed the resampler instead.

Fix: bumped resampler `KernelTaps` from 16 → 32 and Kaiser `β` from 8 → 12 in [`AgentAudioGeneratorBridge`](../../Runtime/Native/AgentAudioGeneratorComponent.cs). Combined that's ~+25-30 dB of stopband attenuation (~85-90 dB total). Per-output cost doubles to 32 multiply-adds — still audio-thread trivial (~1.5 M ops/sec at 48 kHz output). Kernel table grows from 16 KB → 32 KB; still negligible. Cold-start "pre-history absorption" grows from 7 samples (~0.5 ms at 16 kHz) to 15 samples (~1 ms), still imperceptible.

Edit-Mode tests: 449/449 still pass.

- [x] **A/B 1 — Turn 1 latency.** Confirmed near-instant first-audio (subjective sub-50ms gap from trigger to voice), vs the perceptible ~0.8 s gap under the legacy engine. Threshold-gate collapse from step 5 verified in practice. **Caveat:** the resampler that closes the input→device rate gap leaves a subtle high-frequency rattle correlated with loud sibilants — silence between turns is clean, only loud speech amplitudes show it. Spectrogram-confirmed. Documented as [#17](https://github.com/elevenlabs/unity/issues/17) for follow-up; not a regression vs the legacy engine, acceptable for v0.2 ship. Definitive fix is server-side rate negotiation (raised with the backend team).
- [ ] **A/B 2 — Turn 2+ drift.** Have a 3+ turn conversation through a single cube. The bob's peak should remain locked to the audible head across all turns (the residual ~tens-of-ms drift the bob redesign couldn't fully fix — should be gone since the wall-clock anchor stamps at zero pre-fill instead of ~800 ms).
- [ ] **A/B 3 — Voice clarity.** Listen for resampler artifacts (clicks, aliasing, pitch warble) on multi-syllable words. Unity's built-in resampler handles 16 → 48 kHz via the `IControl.Configure` Setup negotiation; should sound identical to the legacy `AudioClip`-resampled path or cleaner.
- [ ] **A/B 4 — Supplied-source spatialization.** Walk between two cubes during playback. The voice should pan / attenuate based on the cube's world position relative to `RobotCamera` (Unity's spatial blend on each `AudioSource`). The generator path keeps the user's literal AudioSource as the playback source (no child-GameObject re-parenting), so spatializer + mixer routing should work unchanged from a static `AudioClip.Play()`.
- [x] **A/B 5 — WebGL conversation smoke passes against the bumped floor.** `pnpm --dir IntegrationTests~ run test` against fresh WebGL builds (`bash TestProject/build-webgl.sh` + `bash TestProject/build-webgl-conversation.sh`) — 5/5 Vitest specs green (`bridge primitive smoke`, `conversation smoke`, `conversation spatial smoke`, plus the two harness fixtures). The two conversation specs hit the harness-allowed `[ConvSmoke] CONFIG MISSING` / `[SpatialSmoke] CONFIG MISSING` green path on this dev machine (per [`Samples/ConversationSmokeTest/README.md`](../../Samples/ConversationSmokeTest/README.md) — the per-developer `ConversationSmokeConfig.asset` either isn't being bundled into the WebGL Resources payload or isn't being deserialized at runtime on this clone; pre-existing behavior independent of the floor bump). Build succeeds against `6000.3.6f1` — no regression from the Unity 6.3 LTS floor.

---

## Tests

- `Tests/Runtime/Native/UnityGeneratorAudioOutputEngineCharacterizationTest.cs` — step 1, PlayMode
- `Tests/Editor/Native/AudioPcmRingTests.cs` — step 2, Edit-Mode unit tests
- `Tests/Editor/Native/UnityAudioSourceOutputTests.cs` — step 4, existing Fake suite re-run

## What this *doesn't* do

- Doesn't change the WebGL audio path (separate `web-audio-sink.ts` pipeline stays)
- Doesn't touch the `IAudioOutputEngine` interface (already the right shape)
- Doesn't delete the old `UnityAudioOutputEngine` (kept as fallback through v0.2 soak)
- Doesn't adopt `Audio.IAudioGenerator.Serializable` / inspector picker UX (an asset-based workflow would let users wire their own generators in the Inspector — useful but not v0.2 scope)

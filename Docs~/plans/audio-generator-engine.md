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
- **~~Drain-callback bridging.~~ Resolved:** the realtime struct accesses shared state via `Interlocked`/`Volatile` on an enclosing class's static fields (in the test) — for production, the same shape works with a heap-allocated handle to the SPSC ring captured by the control struct at `Configure` time and shared with the realtime via a field. Locked in for step 3.
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

- [ ] Engine class with all `IAudioOutputEngine` methods
- [ ] Generator MonoBehaviour wires `IAudioGenerator` correctly
- [ ] Realtime + Control structs Burst-compile (or build clean without Burst per step 0 finding)
- [ ] Owned-host + supplied-source paths both compile and behave (manually validated)

### Step 4 — Re-run Fake-driven test suite

Same shape as the filter-engine plan's step 4. The seam is implementation-agnostic; existing Fake-driven tests under `Tests/Editor/Native/UnityAudioSourceOutputTests.cs` should pass. The two `[Ignore]`d regression tests likely pass too (pre-fill is structurally zero — well within the wall-clock window).

- [ ] Fake-driven tests pass against `UnityGeneratorAudioOutputEngine` via engine constructor injection
- [ ] `[Ignore]`d regression tests un-ignored if they now pass
- [ ] FakeAudioOutputEngine defaults recalibrated if cadence differs from streaming-clip path

### Step 5 — Simplify `UnityAudioSourceOutput` audible-head model

Likely smaller delta than the bob-redesign work. With zero pre-fill, the [`_playbackStartStampTicks` / `_playbackStartRingPos`](../../Runtime/Native/UnityAudioSourceOutput.cs) anchor path can be a no-op (drain head ≈ audible head). The 900 ms threshold gate before `Start` can probably shrink to a single DSP buffer — the generator path doesn't need a primed ring before `Play()` (it just generates silence until the ring fills).

- [ ] Audible-head model simplified or kept (decision documented)
- [ ] Threshold gate sizing decision documented

### Step 6 — Production wiring swap (native only; WebGL unchanged)

Single line change at [`UnityAudioSourceOutput.CreateAsync`](../../Runtime/Native/UnityAudioSourceOutput.cs#L204): swap `new UnityAudioOutputEngine(...)` for `new UnityGeneratorAudioOutputEngine(...)` on native; WebGL preprocessor gate keeps its `web-audio-sink.ts` path.

Keep the old `UnityAudioOutputEngine` in tree as a fallback through v0.2 soak; mark deprecated in XML doc. Removable in a follow-up.

- [ ] Native wiring switched
- [ ] WebGL preprocessor gate verified (no accidental native engine on WebGL builds)
- [ ] Old engine retained + deprecation-marked

### Step 7 — Manual PlayMode A/B + WebGL build verification

- A/B 1 — Turn 1 latency (should be near-zero; was ~800 ms before the bob redesign)
- A/B 2 — Turn 2+ drift (the residual artifact the bob redesign couldn't fix — should be gone)
- A/B 3 — Voice clarity (no resampling artifacts — Unity's resampler handles 16→48 kHz)
- A/B 4 — Supplied-source spatialization (user's literal AudioSource plays through their mixer + spatializer — should "just work" since we're not creating a child GameObject anymore)
- A/B 5 — WebGL build still passes the existing `IntegrationTests~/` conversation smoke (no regression from the floor bump)

- [ ] All five A/B checks captured
- [ ] WebGL conversation smoke passes against the bumped floor

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

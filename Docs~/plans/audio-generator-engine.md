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

- **Does `IAudioGenerator` work on WebGL?** Almost certainly not (FMOD vs Web Audio split), but verify by checking the Unity 6.3 release notes and trying a build. If unsupported, WebGL keeps its `web-audio-sink.ts` path — easy preprocessor gate.
- **Binding mechanism for `audioSource.generator`.** The manual example sets `m_AudioSource = GetComponent<AudioSource>()` but never assigns `m_AudioSource.generator = this` — suggesting Unity binds via a different path (inspector picker via `[IAudioGenerator.Serializable]`? `AudioSource.generator` is a settable property per the [API ref](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.IAudioGenerator.html)?). Settle empirically in step 1.
- **Coexistence with `AudioSource.clip`.** When `audioSource.generator` is set, does `audioSource.clip` get ignored or do they layer? The user's supplied AudioSource may have a clip assigned — does setting the generator override or coexist?
- **Burst dependency.** [`[BurstCompile]`](https://docs.unity3d.com/Packages/com.unity.burst@latest) is in the example but not strictly required for `IRealtime` — confirm whether plain managed C# works (slower but no extra package). If Burst is mandatory, we add a `com.unity.burst` dependency to `package.json`; if optional, we ship without it and let downstream projects opt in. Burst is Unity-official and already pulled in transitively by Unity's own packages, so the cost is probably negligible.
- **Drain-callback bridging.** Our existing `IAudioOutputEngine.Start(FormatConfig, Func<float[], int> drainCallback)` takes a managed `Func<float[], int>`. The `IRealtime` struct can't capture a managed delegate (would defeat Burst). Bridge via a static map: `_engineInstanceId → drainCallback`, struct holds the int ID, Process method looks up + calls. Or: the struct just reads from the shared ring directly, and the control side wires the producer into the ring. The latter is cleaner if the ring API is right.
- **`ControlContext.builtIn` vs custom contexts.** Manual example uses `ControlContext.builtIn` everywhere; the [`AllocateGenerator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.ControlContext.AllocateGenerator.html) ref mentions creating contexts "purely in code." We probably use `builtIn` (it's the AudioSource-integrated context).

## WebGL — stretch goal, almost certainly out of reach

Unity's WebGL audio backend doesn't expose a scriptable pipeline (per the writeup at [`Docs~/unity-issues/webgl-scriptable-audio-pipeline.md`](../unity-issues/webgl-scriptable-audio-pipeline.md)). `AudioClip.PCMReaderCallback`, `OnAudioFilterRead`, `AudioRenderer`, `AudioListener.GetOutputData` are all unsupported there. The generator surface almost certainly falls in the same bucket — it's a FMOD-side concept, and WebGL uses Web Audio.

**WebGL stays on the existing `web-audio-sink.ts` path.** Step 0 verifies this assumption (Unity 6.3 release notes search + a WebGL build of a minimal generator). If it's miraculously supported, that's a follow-up plan; not blocking this one.

If unsupported: the SDK ships two output engines, gated by `#if UNITY_WEBGL && !UNITY_EDITOR` (or the equivalent preprocessor pattern already used in [`UnityAudioSourceOutput.cs`](../../Runtime/Native/UnityAudioSourceOutput.cs)). Same shape as today's split.

---

## Execution sequencing

### Step 0 — Floor bump + scope verification

- [ ] Update [`README.md`](../../README.md) min-version line
- [ ] Update [`COMPATIBILITY.md`](../../COMPATIBILITY.md) Unity version section (drop the obsolete 2023.1 framing; add Unity 6.3 rationale + audio-generator link)
- [ ] Update [`package.json`](../../package.json) `unity` field (`6000.0` → `6000.3`)
- [ ] Add `[Unreleased]` entry to [`CHANGELOG.md`](../../CHANGELOG.md) for the breaking floor bump
- [ ] Check CI workflows under `.github/workflows/` for any pinned Unity version; bump if needed
- [ ] Verify WebGL doesn't support `IAudioGenerator` (release notes search + minimal WebGL build attempt). Document the finding in this plan
- [ ] Verify `[BurstCompile]` is optional vs required for `GeneratorInstance.IRealtime`. If required, add `com.unity.burst` as a `package.json` dependency
- [ ] Confirm `AudioSource.generator` binding mechanism (set via property assignment, picker, or inspector?). Probe via unity-mcp using a 20-line MonoBehaviour driver from the [example](https://docs.unity3d.com/6000.3/Documentation/Manual/audio-scriptable-processors-example-creating-a-generator.html)

### Step 1 — Characterization test (PlayMode)

Mirror [`UnityAudioOutputEngineCharacterizationTest`](../../Tests/Runtime/Native/UnityAudioOutputEngineCharacterizationTest.cs) for the generator path at `Tests/Runtime/Native/UnityGeneratorAudioOutputEngineCharacterizationTest.cs`:

- Allocate a minimal `IAudioGenerator` MonoBehaviour with an `IRealtime` struct that counts `Process` calls
- Bind via `audioSource.generator = component`, `audioSource.Play()`
- Measure: sync fires inside `Play()` (should be 0), ongoing rate (`Process` calls per second), `ChannelBuffer.frameCount` per call, `ChannelBuffer.channelCount`
- Loose-band assertions; `Assert.Inconclusive` fallback for batchmode
- Log values for fake calibration

- [ ] Characterization test file written + .meta stamped via test runner
- [ ] PlayMode run captures empirical values via unity-mcp
- [ ] Plan updated with measured values; open questions resolved

### Step 2 — SPSC ring buffer (audio-thread safe)

Standalone helper at `Runtime/Native/AudioPcmRing.cs` (or extend the existing ring if it can be reused as-is):

- Backed by `NativeArray<float>` for Burst compatibility
- Producer (network thread): `Write(ReadOnlySpan<float>)` — non-blocking, drops on overflow
- Consumer (audio thread): `Read(Span<float>) → int` — non-blocking, returns sample count actually read
- Lock-free using atomic read/write indices (`Interlocked.Read` / `Interlocked.Exchange` if `NativeArray<long>` for the indices)
- Edit-Mode unit tests at `Tests/Editor/Native/AudioPcmRingTests.cs`: empty read, full write overflow, cross-boundary read/write, producer/consumer race smoke

- [ ] Ring implementation
- [ ] Unit tests cover empty/full/boundary/race cases

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

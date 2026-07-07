# Native AEC via OS voice-processing primitives

**Status:** Proposed, 2026-07-05. Branch: TBD (suggest `feat/native-aec-os-voice-processing` after sign-off).
**Driver:** On a laptop with integrated speaker + microphone, the agent's TTS output loops back through the mic loud enough (~-30 to -50 dBFS, full-bandwidth harmonic structure — see the spectrogram capture from the conversation that spawned this plan) to trip server-side VAD. The server interprets it as user speech, sends an interrupt, and the agent effectively interrupts itself mid-turn. Unity's [`Microphone`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Microphone.html) API opens the OS mic device raw — no AEC, noise suppression, or AGC — so today's native path has no defense against acoustic feedback. WebGL is unaffected because `@elevenlabs/client`'s `MediaDeviceInput` goes through `getUserMedia({ echoCancellation: true, noiseSuppression: true, autoGainControl: true })` and the browser hands us a cleaned stream.
**Assumes:** [`audio-generator-engine.md`](audio-generator-engine.md) has shipped (native output uses `IAudioGenerator` and pushes samples into the system output mix — the reference signal every OS AEC unit expects).
**Out of scope:** WebGL (already covered by the browser); Linux/iOS/Android desktop targets (own follow-up); a cross-platform software AEC fallback via `libwebrtc-audio-processing` (own follow-up, referenced at the end).

---

## Why OS-native, and why on this axis

The three practical directions and the reason we're picking OS-native for v1 (validated by the ecosystem research summarised in the conversation that spawned this plan — [Photon Voice's Recorder docs](https://doc.photonengine.com/voice/current/getting-started/recorder), [Vivox iOS AEC docs](https://docs.unity.com/en-us/vivox-core/developer-guide/ios/acoustic-echo-cancellation), [Dissonance AEC docs](https://placeholder-software.co.uk/dissonance/docs/Tutorials/Acoustic-Echo-Cancellation.html)):

| Approach | Effort | Quality | Cross-platform | Notes |
|---|---|---|---|---|
| Half-duplex gate (mute mic while agent speaks) | Trivial | Kills barge-in entirely | Yes | Not what users of a voice agent expect. |
| **OS-native voice processing** — [`AVAudioEngine.setVoiceProcessingEnabled`](https://developer.apple.com/documentation/avfaudio/avaudioinputnode/setvoiceprocessingenabled(_:)) on macOS, WASAPI [`eCommunications`](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/ne-mmdeviceapi-erole) role on Windows | Moderate | Best per-platform (OS unit has privileged access to speaker timing) | Per-platform, but each platform is one API call to enable | **Picked.** Photon Voice's "Photon Microphone Type" ships this exact shape and their explicit recommendation is *"Hardware processing using Photon microphone type (alone without WebRtcAudioDSP) for AEC / NS and AGC on supported platforms"*. |
| Bundled software AEC (`libwebrtc-audio-processing`) | Larger (per-platform binaries) | Uniform, tuned to Chrome-grade | Yes | Deferred to a follow-up as a Linux + degraded-device fallback. Photon's `WebRtcAudioDSP` is the same shape and they document that its main use case is when hardware AEC is unsuitable or the platform lacks one. |

The half-duplex option is a v1 fallback per-session (see the fallback section) but not the primary v1 story.

## Architecture: `IAudioInputEngine` seam mirroring `IAudioOutputEngine`

The output side already isolates every Unity-API touch behind a seam ([`IAudioOutputEngine`](../../Runtime/Native/IAudioOutputEngine.cs)) so `UnityAudioSourceOutput` can swap between the real engine and Edit-Mode fakes. Native input needs the same shape: an `IAudioInputEngine` interface with two production implementations (`UnityMicrophoneAudioInputEngine` — wraps today's `Microphone.Start` for the fallback path, and `OsVoiceProcessingAudioInputEngine` — wraps the platform-native plugin), one `NullAudioInputEngine` for construction-only tests, and a future `FakeAudioInputEngine` for cadence-driven ones.

```
                  ┌───────────────────────────────┐
Conversation ───▶ │ UnityMicrophoneInput          │
                  │  (encode, mute, RMS, FFT)     │
                  └───────────┬───────────────────┘
                              │  IAudioInputEngine.ReadFrame(float[])
                              ▼
              ┌───────────────────────────────────────┐
              │ engine impl (selected by launcher)    │
              ├───────────────────────────────────────┤
              │ UnityMicrophoneAudioInputEngine       │─ Microphone.Start + GetPosition
              │ OsVoiceProcessingAudioInputEngine     │─ P/Invoke into Plugins/macOS or Plugins/Windows
              │ NullAudioInputEngine (tests)          │
              └───────────────────────────────────────┘
```

`UnityMicrophoneInput` keeps every responsibility it has today (16-bit PCM encoding, mute semantics, `GetVolume` / `GetByteFrequencyData` — same 5 ms RMS window as the output side after [bob-alignment-redesign](bob-alignment-redesign.md)). The only thing that changes: instead of `Microphone.GetPosition` + `AudioClip.GetData`, it calls `engine.ReadFrame(_floatBuffer)` to get the next 25 ms chunk. The engine handles device open/close, threading, and any sample-rate resample.

The seam is the load-bearing design choice — it means the plugin can start life as a spike behind a feature flag, and the fallback path stays trivially exercisable in a `TestProject` scene by flipping the flag off.

## Platform 1 — macOS (`AVAudioEngine.setVoiceProcessingEnabled`)

**Primary API:** on macOS 10.15+, `AVAudioEngine`'s input node exposes [`setVoiceProcessingEnabled:error:`](https://developer.apple.com/documentation/avfaudio/avaudioinputnode/setvoiceprocessingenabled(_:)). Enabling it swaps the graph's audio unit from the generic HAL output to `kAudioUnitSubType_VoiceProcessingIO` internally, which:

- Cancels acoustic echo using the system output mix as reference — no Unity-side plumbing required to hand it the agent's TTS specifically. The reference-signal path is: agent samples → our `UnityGeneratorAudioOutputEngine` → Unity's audio graph → CoreAudio system mix → VPIO's built-in reference tap.
- Applies noise suppression + AGC by default. Configurable but the defaults are the "voice assistant" preset.
- Requires `AVAudioEngine.prepare()` + `start()` to activate the underlying audio unit; the flag must be set *before* start.

**The `-10876` gotcha ([Apple Developer Forums thread](https://developer.apple.com/forums/thread/128518), [OpenAI community note](https://community.openai.com/t/audio-notes-for-openai-realtime-on-apple-platforms/1108404)):** enabling voice processing on macOS can fail with `AUVoiceProcessor: couldn't create the aggregate device (err=-10876)`. Two known contributors:

1. Only setting `setVoiceProcessingEnabled` on the input node. The OpenAI note explicitly reports needing it on **both input and output nodes** — the API is per-node but the underlying audio unit is single. First remediation: set on both nodes symmetrically. Costs nothing if it turns out to be superstition; likely fixes the aggregate-device conflict when it's real.
2. Racing with Unity's own audio graph over the aggregate device. If (1) doesn't resolve it in the spike, the escape hatch is dropping to `AUAudioUnit(componentDescription:)` with `componentSubType = kAudioUnitSubType_VoiceProcessingIO` directly — same audio unit, no `AVAudioEngine` wrapper, no aggregate-device dance. More code but avoids the sharp corner.

**Sample-rate resample:** VPIO's device stream is whatever CoreAudio negotiates for the default input device (48 kHz on most Macs, 44.1 kHz on some, 16 kHz on a few USB conference mics). Our mic format at v0.1 is fixed at whatever the AsyncAPI handshake negotiates — usually 16 kHz. The plugin resamples device → SDK rate inside its capture callback and hands C# already-resampled 16 kHz frames. Keeps C# ignorant of device rate and matches how the output side handles Unity's DSP rate ≠ agent input rate.

**Threading:** the audio unit invokes its input callback on CoreAudio's HAL I/O thread. The plugin writes into a lock-free SPSC ring; C# pulls from it on the Unity main thread in the existing polling loop.

**File layout:**
- `Plugins/macOS/ElevenLabsVoiceProcessing.bundle` — built from an Xcode `Bundle` target (see [Unity's Native Audio Plug-in SDK](https://github.com/Unity-Technologies/NativeAudioPlugins) — same tooling, different loader). Not an audio plug-in (we don't want a mixer effect); a regular Unity native plugin with C exports.
- `NativePlugin~/macos/` — Xcode project source, checked in but excluded from the shipped package (`~` suffix). CI builds the `.bundle` and commits the artifact to `Plugins/macOS/`.

## Platform 2 — Windows (WASAPI `eCommunications` role)

**Primary API:** Windows Communications endpoints activate the built-in Audio Effects (AEC + NS + AGC) automatically. The flow:

1. `IMMDeviceEnumerator::GetDefaultAudioEndpoint(eCapture, eCommunications, &device)` — pick the mic in Communications role, not the general `eConsole` role Unity uses. The Communications role is what Windows exposes for VoIP apps; it's where the AEC audio effect chain lives.
2. `IAudioClient::Initialize(AUDCLNT_SHAREMODE_SHARED, ...)` — shared mode so we don't fight Unity's own audio graph. Don't pass `AUDCLNT_STREAMFLAGS_RAW` (that opt-outs of the effects chain).
3. `IAudioCaptureClient::GetBuffer` — pull samples on our capture thread.

The AEC is device-specific (laptops with integrated mic/speaker usually ship it as a device effect; USB conference mics vary). Windows exposes this state through `PKEY_AudioEndpoint_Association` + audio effect discovery APIs ([MSDN thread on WASAPI + AEC](https://social.msdn.microsoft.com/Forums/lync/en-US/c606cd22-93af-41fa-9536-380b36e0a857/wasapi-echo-cancellation?forum=windowspro-audiodevelopment)). We don't need to query — if the device has AEC it activates on the Communications endpoint; if not, we get raw capture and log a one-time warning that hardware AEC isn't available on this device.

**Sample-rate resample:** same story as macOS — WASAPI negotiates a device format (48 kHz on nearly every modern Windows machine), we resample on the plugin side to the SDK's 16 kHz.

**File layout:**
- `Plugins/Windows/x86_64/ElevenLabsVoiceProcessing.dll` — MSVC build, C exports only (no C++ ABI at the P/Invoke boundary).
- `NativePlugin~/windows/` — CMake project, checked in but excluded from the shipped package. CI builds the `.dll` and commits the artifact.

## The P/Invoke surface

One C-flat header shared by both platforms, plus a per-platform impl behind it. Names prefixed `el_vp_` to avoid symbol collisions in a project that already links other native audio libs.

```c
// One-time init. Returns 0 on success, negative on failure.
// requested_sample_rate: the SDK's negotiated mic format (e.g., 16000).
// requested_channels: always 1 for v0.1.
int el_vp_start(int requested_sample_rate, int requested_channels);

// Pull the next chunk of already-resampled + AEC'd samples.
// Returns the number of samples written into `out_samples` (up to `max_samples`).
// Returns 0 if no samples available (caller's polling loop yields and retries).
// Returns negative on unrecoverable error (caller falls back to raw Microphone).
int el_vp_read(float* out_samples, int max_samples);

// Stop capture and release the OS handles. Idempotent.
void el_vp_stop(void);

// Diagnostic — populated on failed start. Returns a stable NUL-terminated
// error code (e.g. "vp_start_aggregate_device_-10876") for logging.
const char* el_vp_last_error(void);
```

The `el_vp_last_error` return is a static string owned by the plugin; C# reads it via `Marshal.PtrToStringUTF8`. Stable string codes let the SDK log-tag failures without allocating and without a versioned error enum.

## `ConversationOptions.EchoCancellation` — surface + default

```csharp
public sealed record ConversationOptions
{
    // ...existing fields...

    /// <summary>
    /// Enable OS-native acoustic echo cancellation + noise suppression + AGC on
    /// the mic input path. Defaults to <c>true</c> — voice-agent workloads
    /// benefit universally, and the loopback-triggered self-interrupt case is
    /// unusable without it on laptop hardware. Set to <c>false</c> for raw
    /// capture when the mic is a lavalier / headset / other non-loopback rig,
    /// or when reproducing a bug against uncleaned audio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Honored on macOS + Windows via the OS voice-processing unit (see
    /// <c>Docs~/plans/native-aec-os-voice-processing.md</c>). Ignored on WebGL
    /// (browser <c>getUserMedia</c> constraints already apply AEC + NS + AGC
    /// unconditionally through the JS SDK's <c>MediaDeviceInput</c>). Ignored
    /// with a one-time warning on Linux / iOS / Android in v1 — the OS-native
    /// path for those platforms is tracked separately.
    /// </para>
    /// <para>
    /// If plugin init fails at session start (permission denied, aggregate-
    /// device error, missing hardware AEC on the device), the SDK logs one
    /// warning and transparently falls back to raw <see cref="Microphone"/>
    /// capture — the session still starts.
    /// </para>
    /// </remarks>
    public bool EchoCancellation { get; init; } = true;
}
```

Default-on is the right call: the design brief explicitly asked for this and voice-agent sessions are the only real `Microphone` consumer in this SDK. Existing users on macOS + Windows silently gain AEC — the only observable change from their perspective is that the agent stops interrupting itself.

**Tuning follow-up — auto-disable when output goes through headphones.** Headphones remove the acoustic loopback path, so running the OS voice-processing chain on a headset user is pure CPU + audio-quality tax (VPIO applies noise suppression + AGC even when there's no echo to cancel, and both are lossy). Both platforms expose the necessary device-role signal to detect this — CoreAudio's `kAudioDevicePropertyDataSource` + `kAudioDeviceTransportTypeBluetooth` on macOS, Windows' `PKEY_AudioEndpoint_FormFactor` (`Headphones` / `Headset`) on the Communications endpoint — so a heuristic like "default output is a headset endpoint → open the capture engine with voice-processing off, but still through the same plugin (Communications role on Windows, `AVAudioEngine` on macOS) so device-swap mid-session can toggle it back on" is tractable. Not in v1 — captured here so we don't lose it. Tracked separately when we get there.

## Fallback / graceful degradation

Three failure modes, each with a defined observable outcome:

1. **Plugin binary missing / wrong architecture.** `DllNotFoundException` on the first P/Invoke. Launcher catches, logs `[ElevenLabs] Native AEC plugin not found on this platform — falling back to raw microphone capture. (…)`, wires `UnityMicrophoneAudioInputEngine` instead of `OsVoiceProcessingAudioInputEngine`, session proceeds. One warning per session.
2. **Plugin start fails at runtime** (macOS `-10876`, Windows permission denied, mic hot-swapped mid-init). `el_vp_start` returns negative, `el_vp_last_error` gives the stable code. Same fallback path as (1), with the error code in the warning.
3. **Device has no hardware AEC** (Windows USB mic without effects). `el_vp_start` succeeds but the plugin knows the effects chain didn't attach (queryable via WASAPI); logs `[ElevenLabs] Communications endpoint on this device has no acoustic-echo effect — capture proceeds but loopback may still trigger self-interrupt.` and continues. Warning surfaces the limitation; session proceeds with AEC-less-but-still-communications-role capture.

The fallback is symmetric across platforms so a user hitting it on a Windows laptop and a user hitting it on a Mac see the same shape of log message and end up in the same working-but-uncleaned state.

## Empirical spikes (what we don't know yet)

Everything else in this plan is design; these three items need actual measurement before we commit to their implementation path. They're the first work in step 1 below.

1. **macOS: does `setVoiceProcessingEnabled(true)` on input + output both actually avoid `-10876` reliably in a Unity `[MonoPInvokeCallback]` context?** The OpenAI community note suggests it does; the Apple forum thread doesn't confirm. Spike: minimal Xcode Bundle + a `TestProject` Play Mode scene that calls `el_vp_start` and reports the result. If it works, we're done — ship AVAudioEngine. If it doesn't, drop to raw `AUAudioUnit` + `kAudioUnitSubType_VoiceProcessingIO`.
2. **macOS: is the reference signal actually populated when Unity's audio graph is the sole producer of system output?** The VPIO unit taps the default output device; Unity's `AudioSource` playback goes to the same device. This should Just Work. But the audio-generator engine is new enough that we haven't validated that its output shows up on the system mix in a way VPIO can see (vs. bypassing to a hidden route). Spike: play a known TTS chunk, capture the AEC'd mic in a quiet room, verify the residual is at least 20 dB below the raw loopback level.
3. **Windows: which mic devices actually have the Communications-role AEC on our developer hardware?** The MSDN thread is 15 years old and hardware has changed. Spike: enumerate devices via `IMMDeviceEnumerator`, log the effect chain on both `eConsole` and `eCommunications` roles, note which of the developer's laptops report AEC.

None of the spikes should take more than a few hours. If any of them blocks, we pause the plan and re-evaluate.

## Execution sequencing

Each step is independently committable. Steps 1–3 gate the rest; steps 4–7 can go in either order.

1. **[ ] Empirical spikes.** All three from the previous section, in a `spike/native-aec` branch that lands nothing to `main` but produces measurements + a short doc appended here.
2. **[ ] `IAudioInputEngine` seam extraction.** Introduce the interface, move `Microphone.Start` calls out of `UnityMicrophoneInput` into a new `UnityMicrophoneAudioInputEngine`, add `NullAudioInputEngine`, port the existing tests to construct through the fake. No behaviour change; sets the platform for step 4. Mirrors the [bob-alignment-redesign](bob-alignment-redesign.md)'s output-side seam pass.
3. **[ ] `ConversationOptions.EchoCancellation` field + wiring.** Default to `true`, plumb through `NativeSessionLauncher.StartAsync`, honor on WebGL as a no-op (browser already handles it), warn once on Linux/iOS/Android that the flag is ignored on this platform.
4. **[ ] macOS native plugin.** `NativePlugin~/macos/` Xcode project, C exports matching the P/Invoke surface, produces `Plugins/macOS/ElevenLabsVoiceProcessing.bundle`. Ship the pre-built bundle in the package.
5. **[ ] Windows native plugin.** `NativePlugin~/windows/` CMake project, C exports matching the P/Invoke surface, produces `Plugins/Windows/x86_64/ElevenLabsVoiceProcessing.dll`. Ship the pre-built DLL in the package.
6. **[ ] `OsVoiceProcessingAudioInputEngine` C# side.** P/Invoke declarations, engine impl calling into the C surface, integration with the launcher's fallback path. Platform-gated with `UNITY_STANDALONE_OSX` / `UNITY_STANDALONE_WIN` so the P/Invoke declarations don't pull DllImport resolution on unsupported platforms.
7. **[ ] Manual PlayMode validation.** Same scene the bob-alignment redesign used (`TestProject` + a live agent). Success criteria: agent no longer interrupts itself in a quiet room with laptop speaker + laptop mic at moderate volume. Capture a mic-input spectrogram before/after; residual loopback ≥ 20 dB below pre-AEC.
8. **[ ] CI plugin builds.** GitHub Actions steps that build the macOS bundle + Windows DLL from `NativePlugin~/` and assert no drift against the committed `Plugins/` artifacts (mirror `verify:primitives` / `verify:connection` shape from the WebGL side).
9. **[ ] `COMPATIBILITY.md` known-limitations entry.** Document: Linux desktop has no OS-native AEC path in v1; iOS + Android deferred to the follow-up plan; a device without hardware AEC on Windows will still see loopback issues but with the warning noted above; the macOS aggregate-device fallback path.
10. **[ ] Follow-up plan stub.** Land `Docs~/plans/native-aec-cross-platform.md` with the sequencing for iOS + Android OS-native (VPIO on iOS via `AVAudioSessionModeVoiceChat`, `VoiceCommunication` audio source on Android) + `libwebrtc-audio-processing` as the Linux + degraded-device fallback (the Photon `WebRtcAudioDSP` shape).

## What this doesn't do

- **Doesn't touch WebGL.** Browser AEC via `getUserMedia` constraints already runs; `EchoCancellation = false` is honored there through the JS SDK's `MediaDeviceInput` constraint bag (verify during step 3 that we're forwarding the flag, not always requesting AEC on).
- **Doesn't ship a software AEC.** `libwebrtc-audio-processing` is the natural cross-platform fallback for Linux + the "OS AEC is poor on this device" edge case; deferred to the follow-up plan so v1 stays scoped.
- **Doesn't tune AEC parameters.** OS defaults are the voice-assistant preset — good enough for a shipped voice agent. Aggressiveness / suppression-level tuning is a follow-up if users report specific device pathologies.
- **Doesn't change the mic RMS / FFT paths.** `UnityMicrophoneInput.GetVolume` + `GetByteFrequencyData` continue reading from the same 25 ms chunk buffer — after step 6 that buffer just happens to hold cleaned samples instead of raw ones. Visualisers keep working with no code change.
- **Doesn't affect `Microphone.GetPosition` consumers outside the SDK.** The plugin is scoped to the SDK's own capture; user code that opens its own `Microphone.Start` for game recording elsewhere in the project is untouched.

## Follow-ups already known

- `Docs~/plans/native-aec-cross-platform.md` (step 10): iOS + Android OS-native + `libwebrtc-audio-processing` for Linux + degraded devices.
- **Auto-disable voice processing when output is a headset** — see the tuning-follow-up paragraph in the `EchoCancellation` section above. Requires device-role detection on both platforms; captured to a dedicated plan once v1 lands.
- [`Docs~/unity-issues/microphone-no-voice-processing-toggle.md`](../unity-issues/microphone-no-voice-processing-toggle.md) — the observation that Unity's `Microphone` API has no AEC toggle across a decade of platform APIs that all ship one, written up for handoff to a Unity contact.

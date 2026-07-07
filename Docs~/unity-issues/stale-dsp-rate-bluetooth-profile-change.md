# Audio output pitch-shifts ~3× after a Bluetooth headset profile change (stale DSP sample rate)

**Unity version:** 6000.3.6f1 (Unity 6 LTS), macOS 26.5 arm64 Editor.
**Hardware observed:** Jabra Elite 4 Bluetooth headset (A2DP render at
44.1 kHz stereo; HFP capture+render at 16 kHz mono). The mechanism is
profile-generic — any Bluetooth headset that renegotiates A2DP ↔ HFP
when its microphone opens should reproduce.
**Severity:** Every sound the engine plays — not just SDK audio — is
rendered ~3× fast / pitch-shifted up until `AudioSettings.Reset` or an
editor restart. Any voice-chat or conversational-AI SDK triggers the
condition simply by opening the microphone, which is core
functionality. End users on Bluetooth headsets hit this in normal use.
**Discovered (this codebase):** 2026-07-07 while investigating a
report that agent speech "plays really sped up" in a project consuming
this SDK. The SDK's playback pipeline was ruled out with a plain
`AudioClip` probe (below).

## Summary

When a Bluetooth headset's microphone is opened (`Microphone.Start`),
macOS switches the headset from the A2DP profile (44.1/48 kHz stereo
render) to the hands-free profile (16 kHz mono capture + render).
Unity's audio system was observed running at the HFP rate afterwards:

```
AudioSettings.outputSampleRate == 16000
AudioSettings.driverCapabilities == AudioSpeakerMode.Mono
```

After the OS output path returned to a higher-rate profile, Unity kept
producing its 16 kHz DSP stream **without resampling or
reinitialising**, and the samples were consumed at the device's higher
rate — every sound played roughly 3× fast and pitch-shifted up. The
state persists indefinitely (across Play Mode sessions) until
`AudioSettings.Reset` is called or the editor restarts.

## Minimal repro

1. macOS with a Bluetooth headset connected and selected as the
   default output device (A2DP active — device reports 44.1/48 kHz).
2. Open any Unity project. Confirm
   `AudioSettings.outputSampleRate` is 44100/48000.
3. Enter Play Mode and call `Microphone.Start(<headset mic>, …)` —
   macOS flips the headset to HFP.
4. End the capture / exit Play Mode so the headset returns to A2DP.
5. Check `AudioSettings.outputSampleRate` — observed stuck at 16000
   with `driverCapabilities == Mono`.
6. Play any `AudioClip` authored at 16 kHz (or any rate — the DSP
   runs at 16 kHz regardless): audible playback is ~3× fast and
   pitch-shifted.

Probe used to isolate the engine from SDK code: generate a 3.0-second
440 Hz sine `AudioClip` at `AudioSettings.outputSampleRate` and play
it through a plain `AudioSource`. Observed duration ~1 second at a
higher pitch — no SDK code in the path.

**Confirmed vs inferred:** steps 5–6 (the stale 16 kHz DSP state, the
3× pitch-shifted playback of a plain `AudioClip`, and recovery via
`AudioSettings.Reset`) were observed live in the editor. The exact
transition timing in steps 3–4 (when Unity adopted 16 kHz, and whether
`OnAudioConfigurationChanged` fired on the flip-back) was not captured
— an event logger we installed did not survive the Play Mode domain
reload.

## Expected

Either of:

- Unity tracks the output device's native format; when the device
  renegotiates (Bluetooth profile change), the DSP is reinitialised —
  or its output resampled — so playback pitch/speed stays correct.
- `OnAudioConfigurationChanged(deviceWasChanged: true)` fires reliably
  on the profile flip-back, and the documented contract states that
  the application must call `AudioSettings.Reset` in response — with
  the Bluetooth HFP case called out, since it is by far the most
  common way users hit this.

## Actual

The DSP keeps running at the stale 16 kHz rate, its output is consumed
at the device's restored higher rate without rate conversion, and all
audio plays ~3× fast until the application manually calls
`AudioSettings.Reset` (with `sampleRate = 0` to re-read the driver
default) or the editor restarts.

## Workaround applied in this SDK

[`Runtime/Native/UnityGeneratorAudioOutputEngine.cs`](../../Runtime/Native/UnityGeneratorAudioOutputEngine.cs)
subscribes to `AudioSettings.OnAudioConfigurationChanged` while a
session is active: it logs the rate transition (the condition is
otherwise invisible — nothing in the player log records the stale
rate) and restarts the SDK's `AudioSource`, because Unity stops all
sources when the audio system reinitialises.
[`Runtime/Native/AgentAudioGeneratorComponent.cs`](../../Runtime/Native/AgentAudioGeneratorComponent.cs)
logs every negotiated-input-rate → device-rate publish for the same
reason.

The workaround **cannot** fix the pitch shift itself — that mismatch
lives between Unity's DSP and the OS audio device, below anything a
package can reach. Applications can recover by calling
`AudioSettings.Reset` from an `OnAudioConfigurationChanged` handler,
but doing so from SDK code is not viable: it is a process-wide setting
that stops every playing sound in the user's project.

## Asks for Unity

1. Reinitialise (or resample) the DSP output when the output device's
   native format changes, so a Bluetooth profile renegotiation cannot
   leave the engine rendering at a stale rate.
2. If the intended contract is that applications handle this via
   `OnAudioConfigurationChanged` + `AudioSettings.Reset`, document it
   explicitly (the `AudioSettings.Reset` docs do not mention device
   format changes) and confirm the event fires on Bluetooth profile
   flips in both directions.
3. Document that `AudioSource`s are stopped by audio-system
   reinitialisation — SDKs that stream continuously need to know to
   re-`Play()`.

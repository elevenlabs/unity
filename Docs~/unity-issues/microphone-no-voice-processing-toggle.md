# `Microphone` API has no voice-processing toggle on any platform

**Unity version:** 6000.3.6f1 (Unity 6 LTS). The API surface has been
identical since Unity 5.x; the constraint is at least a decade old.
**Platform reproduced:** every native target — macOS, Windows, Linux,
iOS, Android. The Editor uses the same `Microphone` code path as the
platform standalone builds. WebGL routes through `getUserMedia` in the
browser and is unaffected (see [Workaround applied](#workaround-applied-in-this-sdk)).
**Severity:** Undocumented behaviour + missing API surface. Forces any
SDK that captures voice for real-time streaming — voice chat, voice
agents, dictation, real-time transcription — to ship a native plugin
that bypasses [`Microphone`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Microphone.html)
entirely and opens the OS mic device directly, purely to enable a
one-line OS API on capture. The pattern is well-established (Photon
Voice, Vivox, Dissonance, and this SDK all ship or plan to ship a
native plugin for this reason).
**Discovered (this codebase):** 2026-07-05 while diagnosing why the
ElevenLabs voice agent interrupted itself mid-turn on a MacBook with
laptop speaker + laptop microphone. The agent's TTS output was
looping back through the mic loud enough (~-30 dBFS, full-bandwidth
harmonic structure) to trip server-side voice-activity detection.
Confirmed via spectrogram capture from the agent platform side.

## Summary

Every mainstream operating system Unity supports ships a first-party
acoustic echo cancellation (AEC) primitive as part of its audio stack,
and has for many years. Enabling it on a capture stream is one API
call per platform:

| Platform | API | Available since |
|---|---|---|
| macOS | [`AVAudioInputNode.setVoiceProcessingEnabled(_:)`](https://developer.apple.com/documentation/avfaudio/avaudioinputnode/setvoiceprocessingenabled(_:)) — or the lower-level `kAudioUnitSubType_VoiceProcessingIO` audio unit | macOS 10.15 (2019) / audio-unit form pre-dates that |
| iOS | Same `setVoiceProcessingEnabled` on `AVAudioEngine`, or `AVAudioSession` category `.playAndRecord` with mode `.voiceChat` | iOS 13 (2019) / mode-based form goes back to iOS 5 |
| Windows | WASAPI capture endpoint opened in the [`eCommunications`](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/ne-mmdeviceapi-erole) role activates the platform's AEC / NS / AGC audio effect chain automatically | Windows Vista (2007) |
| Android | [`MediaRecorder.AudioSource.VOICE_COMMUNICATION`](https://developer.android.com/reference/android/media/MediaRecorder.AudioSource#VOICE_COMMUNICATION) enables the platform AEC + NS + AGC for that capture stream | Android API 11 / Honeycomb (2011) |

Enabling any of these on capture cancels the audio the device speaker
is currently outputting, using the system mix as reference — no extra
plumbing needed by the caller. This is exactly the primitive that
makes FaceTime, Zoom, Meet, Discord, and every VoIP app usable on a
laptop with integrated mic + speaker.

Unity's `Microphone` API exposes exactly two capture-side controls:

```csharp
public static AudioClip Start(string deviceName, bool loop, int lengthSec, int frequency);
public static void      End(string deviceName);
```

Neither takes a hint about the *purpose* of the capture (voice vs.
music vs. environmental recording), and there is no
`Microphone.StartVoiceCapture` / `Microphone.SetVoiceProcessing`
overload. The device is opened with the OS's default capture role
(`eConsole` on Windows / raw `AVAudioInputNode` on macOS+iOS /
`MediaRecorder.AudioSource.MIC` on Android) which, on every platform,
explicitly opts *out* of the built-in voice-processing chain.

The result: every Unity SDK that needs voice capture for real-time
streaming ships its own native plugin that re-implements
`Microphone.Start` against the OS-native APIs listed above, purely to
flip the flag Unity doesn't expose. Photon Voice calls this their
"Photon Microphone Type"; Vivox does it under "Dynamic Voice
Processing Switching" on iOS; Dissonance solves the same problem in
software with an in-Unity AEC filter attached to the audio mixer.
Every one of those solutions is more code, more binary weight, and
more platform surface than a boolean parameter on `Microphone.Start`
would be.

## Minimal reproduction of the workaround-forcing behaviour

There is no reproducing "the bug" per se — the behaviour is that the
API doesn't exist. The minimal repro of the *symptom* it forces every
voice SDK to work around:

```csharp
public class MicLoopbackProbe : MonoBehaviour
{
    private AudioClip _mic;
    private AudioSource _output;
    private const int Rate = 16000;

    private void Start()
    {
        // 1. Capture the mic through Unity's Microphone API.
        _mic = Microphone.Start(null, loop: true, lengthSec: 1, frequency: Rate);

        // 2. Play a distinctive sound through the default output.
        _output = gameObject.AddComponent<AudioSource>();
        _output.clip = Resources.Load<AudioClip>("test_tone_or_speech");
        _output.loop = true;
        _output.Play();

        StartCoroutine(SampleLoopback());
    }

    private IEnumerator SampleLoopback()
    {
        var buf = new float[Rate]; // 1 s
        while (true)
        {
            yield return new WaitForSeconds(1f);
            _mic.GetData(buf, 0);
            float rms = 0f;
            for (int i = 0; i < buf.Length; i++) rms += buf[i] * buf[i];
            rms = Mathf.Sqrt(rms / buf.Length);
            Debug.Log($"mic RMS while output is playing: {rms:F4}");
        }
    }
}
```

On any laptop with integrated mic + speaker, `rms` will be non-zero
even when nothing is speaking into the mic — the mic is picking up
the output. Contrast with the same setup run through a native
plugin that opens the mic via `AVAudioEngine` + `setVoiceProcessingEnabled(true)`
or WASAPI + `eCommunications`: the RMS drops by 20+ dB (usually well
below the noise floor) even though the output is still audible from
the speaker at the same volume.

## Expected behaviour

Add a way to enable the platform voice-processing chain on
`Microphone.Start`. Any of the following would resolve this;
listed in decreasing order of ergonomic quality but any one of them
would collapse the workaround cost:

1. **A new overload with a purpose hint:**
   ```csharp
   public enum MicrophoneCaptureIntent { Default, VoiceCommunication }

   public static AudioClip Start(
       string deviceName,
       bool loop,
       int lengthSec,
       int frequency,
       MicrophoneCaptureIntent intent = MicrophoneCaptureIntent.Default);
   ```
   `VoiceCommunication` maps to `eCommunications` on Windows,
   `setVoiceProcessingEnabled(true)` on macOS+iOS,
   `MediaRecorder.AudioSource.VOICE_COMMUNICATION` on Android. Falls
   back to `Default` on Linux + WebGL with an editor warning. This
   mirrors how the OS APIs themselves are shaped (role-based rather
   than a stack of individual toggles).
2. **A boolean flag** — `enableVoiceProcessing: bool = false` on
   `Microphone.Start`. Less semantically rich than the intent enum
   but a smaller API surface.
3. **A separate API** — `Microphone.StartForVoiceCommunication(...)`
   that documents the difference and doesn't force existing
   `Microphone.Start` callers to think about it.

Any of these is a small addition to Unity's audio surface and
eliminates a whole class of native plugins that every voice-capable
Unity project ships today.

## Workaround applied in this SDK

Native plugin at
[`Plugins/macOS/ElevenLabsVoiceProcessing.bundle`](../../Plugins/macOS/)
and
[`Plugins/Windows/x86_64/ElevenLabsVoiceProcessing.dll`](../../Plugins/Windows/)
(both in flight — tracked at
[`Docs~/plans/native-aec-os-voice-processing.md`](../plans/native-aec-os-voice-processing.md)).
The plugin:

- Opens the mic device directly via `AVAudioEngine` (macOS) or
  `IAudioClient` in the `eCommunications` role (Windows), bypassing
  `Microphone.Start` entirely.
- Enables the platform voice-processing chain on that stream.
- Downsamples from the device's native rate (48 kHz typical) to the
  SDK's 16 kHz agent-input format on the plugin side.
- Exposes a flat C surface (`el_vp_start` / `el_vp_read` /
  `el_vp_stop`) that the C# side pumps from its existing main-thread
  polling loop, keeping the surface change to `UnityMicrophoneInput`
  minimal.
- Gracefully falls back to `Microphone.Start` when plugin
  initialisation fails (permission denied, macOS
  `AUVoiceProcessor: couldn't create the aggregate device` error,
  device without hardware AEC), so a fresh-install session still
  starts even if the platform-native path is unavailable.

WebGL is unaffected: `@elevenlabs/client`'s `MediaDeviceInput` opens
the mic through `navigator.mediaDevices.getUserMedia(...)` with
`echoCancellation: true`, `noiseSuppression: true`,
`autoGainControl: true` — the browser's WebRTC stack applies its own
AEC / NS / AGC pipeline (essentially `libwebrtc-audio-processing`).
This is exactly the primitive Unity's native `Microphone` API is
missing.

Total cost to us of this workaround (across the two shipped native
plugins, the P/Invoke seam, the fallback path, the CI plugin build,
the compatibility documentation, and the follow-up plan for iOS +
Android + Linux): significantly more than a Unity-side one-line
`enableVoiceProcessing` flag would be.

## Asks for Unity

1. **Expose the platform voice-processing chain on
   [`Microphone.Start`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Microphone.Start.html).**
   Any of the three API shapes in the [Expected behaviour](#expected-behaviour)
   section works. The `VoiceCommunication` intent form (option 1) is
   closest to how the OS APIs themselves are shaped and — importantly —
   also handles Android's `MediaRecorder.AudioSource.VOICE_COMMUNICATION`
   which is a *source* selection at capture-time rather than a
   post-open flag. A boolean would work but doesn't map as cleanly to
   Android.
2. **Document the current behaviour at minimum.** Add a "This API
   opens the mic in the platform's default capture role, which
   explicitly disables the OS voice-processing chain (AEC / NS / AGC).
   For real-time voice capture, use a native plugin that opens the
   mic via [`AVAudioEngine`](https://developer.apple.com/documentation/avfaudio/avaudioengine)
   (macOS/iOS), WASAPI [`eCommunications`](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/ne-mmdeviceapi-erole)
   (Windows), or `MediaRecorder.AudioSource.VOICE_COMMUNICATION`
   (Android)." note on the
   [`Microphone.Start`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Microphone.Start.html)
   scripting reference. The current page describes the return value
   and parameters but says nothing about the capture role or its
   implications for voice workloads; every voice-SDK author discovers
   this the way we did — by shipping a build against a laptop and
   hearing the app talk to itself.
3. **(Stretch — larger surface change)** Consider a first-party
   scriptable voice-capture surface analogous to Unity 6.3's
   [`Audio.IAudioGenerator` / `GeneratorInstance`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.IAudioGenerator.html)
   on the output side — an `IAudioProcessor` or `MicrophoneInstance`
   equivalent that gives SDK authors first-class capture with
   configurable audio effects, sample-rate negotiation, and a
   burst-compileable realtime callback. Would collapse both this ask
   and the [streaming-`AudioClip` pre-fill issue](./streaming-audioclip-prefill-depth.md)
   into a symmetric input/output scriptable-audio story.

# Unity issues — upstream bug & limitation reports

Self-contained writeups of Unity bugs, limitations, or unexpected
behaviours we've worked around in this SDK. Each file is structured so
it can be handed to Unity support / a Unity engineering contact
without further editing — version pinning, minimal repro, expected vs
actual, the workaround applied, and the location of the workaround in
our tree.

A new file lands here whenever we discover a workaround for a Unity-side
issue worth propagating upstream. The bar is "if we had a Unity contact
in the room, we'd want them to see this." Internal bug reproducers that
turn out to be our own bugs do **not** land here — those belong in code
comments or implementation plans.

See the "Reporting Unity issues" section of
[`.claude/CLAUDE.md`](../../.claude/CLAUDE.md) for the workflow.

## Index

- [`emscripten-jsdce-destructuring.md`](./emscripten-jsdce-destructuring.md)
  — Emscripten's JSDCE pass deletes `const { x } = obj` destructuring
  declarations from WebGL builds; references remain, producing a
  runtime `ReferenceError`. Fixed upstream in Emscripten 3.1.47
  (Oct 2023), but Unity 6.0–6.4 LTS ship pre-fix Emscripten 3.1.x.
- [`link-xml-autodiscovery-file-package.md`](./link-xml-autodiscovery-file-package.md)
  — UnityLinker silently drops package-internal `link.xml` files when
  the package is referenced as a local file-path package whose root is
  the embedded host project's parent.
- [`microphone-no-voice-processing-toggle.md`](./microphone-no-voice-processing-toggle.md)
  — `Microphone.Start` opens the mic device in the OS's default
  capture role on every native platform, explicitly opting out of the
  built-in voice-processing chain (AEC / NS / AGC) every mainstream
  OS ships. Forces every Unity voice SDK — Photon Voice, Vivox,
  Dissonance, this one — to ship a native plugin that re-implements
  `Microphone.Start` against the OS-native APIs purely to flip a flag
  Unity doesn't expose.
- [`streaming-audioclip-prefill-depth.md`](./streaming-audioclip-prefill-depth.md)
  — `AudioClip.Create(stream=true, pcmreadercallback=…)` reserves
  ~800 ms of pre-fill ahead of the speaker (12,800 samples at 16 kHz
  on the default Unity 6 audio config), invariant in `lengthSamples`,
  with no public API to shrink it. Forces low-latency PCM SDKs to ship
  a visual-sync workaround or bypass streaming `AudioClip` entirely
  via `OnAudioFilterRead`.
- [`webgl-scriptable-audio-pipeline.md`](./webgl-scriptable-audio-pipeline.md)
  — WebGL has no scriptable audio pipeline: `AudioClip.Create(stream=true,
  pcmreadercallback=…)`, `OnAudioFilterRead`, `AudioRenderer`, and
  `AudioListener.GetOutputData` are all unavailable. Forces every audio
  SDK to ship a parallel Web Audio graph; explains why
  `audioSource.GetOutputData` returns silence on WebGL.

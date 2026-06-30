# GettingStarted

A walk-up-and-talk demo. Walk a player capsule into a coloured cube and the
agent greets you in character; walk out and the session ends cleanly. The
cube bobs in time with the agent's voice.

This is the headline sample — it shows how the SDK lands in a real scene
(spatial audio, trigger-based session lifecycle, dynamic variables) in one
short MonoBehaviour. For the smallest possible smoke test with no graphics,
see the **QuickStart** sample instead.

## What it demonstrates

- Opening a session on `OnTriggerEnter` and tearing it down on `OnTriggerExit`,
  with a static "currently active" guard so multiple boxes in the same scene
  don't fight over the microphone.
- Passing per-instance **dynamic variables** (`color`, `mood`) via
  `ConversationOptions.DynamicVariables`, so each box can have its own
  personality from the same agent.
- Per-instance **overrides** for the agent's first message (with
  `{{color}}` / `{{mood}}` interpolation) and TTS voice id.
- Routing the agent's voice through a per-box `AudioSource` via
  `ConversationOptions.OutputAudioSource` so it spatialises in 3D.
- Driving a per-frame visual (the bob) from `Conversation.GetOutputVolume()`,
  which works identically on native and WebGL — unlike
  `AudioSource.GetOutputData`, which only sees the native playback path.

## Setup

Assumes the SDK is already installed in a Unity 6.3 LTS project — see the
[main README](../../README.md#install) for the one-line git URL install, or
the full [Getting Started walkthrough](../../Docs~/GETTING_STARTED.md) for
the end-to-end on-ramp (this sample is **Option A** there).

1. **Import the sample** via Window → Package Manager → ElevenAgents (in the
   left-hand package list) → Samples → GettingStarted → Import. The files
   land under `Assets/Samples/ElevenAgents/<version>/GettingStarted/`.

2. **Create your config asset**:
   - Right-click in the Project view → Create → ElevenLabs → Samples →
     Talking Box Agent Config.
   - Move the asset into any folder named `Resources` somewhere under
     `Assets/` (e.g. `Assets/Resources/TalkingBoxAgentConfig.asset`).
   - Fill in `Agent Id` — grab one from the
     [ElevenLabs dashboard](https://elevenlabs.io/app/agents), or have
     your AI coding assistant create one via the
     [`agents` skill](https://github.com/elevenlabs/skills/tree/main/agents)
     (`npx skills add elevenlabs/skills`). The agent must have
     **authentication disabled** in its security settings for the unsigned
     WebSocket transport to work.
   - To use the per-box `voiceIdOverride` field, the agent must also have
     **tts.voice_id overrides enabled** in the dashboard.

3. **Open the imported scene** (`Scenes/GettingStarted.unity`) and press
   Play. WASD walks, the mouse looks around. Walk into either cube — the
   one in front of you greets you and starts a conversation; walk out and
   it ends.

If the config asset is missing or the agent id is empty, the boxes log an
error to the console and skip opening a session — nothing else happens, so
your project still runs cleanly.

## Trying other sounds and characters

The two cubes in the scene differ only in their `color` / `mood` /
`firstMessage` inspector fields. Duplicate one, change those values, and
you have a third character on the same agent. Things to try:

- Change `mood` (`Happy` / `Sad` / `Angry`) — the agent uses the lower-cased
  string as `{{mood}}` in the first message.
- Add a `voiceIdOverride` (paste a voice ID from your ElevenLabs library)
  for per-box voices.
- Tune `peakOffsetY` and `volumeSensitivity` on the `TalkingBox` component
  to make the bob more or less expressive.

## Files

- `Scenes/GettingStarted.unity` — the playable scene.
- `Prefabs/TalkingBox.prefab` — a cube with the trigger collider,
  `AudioSource`, and `TalkingBox` wired up. Drag into your own scene to
  add another talking box.
- `Scripts/TalkingBox.cs` — the trigger-driven session lifecycle.
- `Scripts/TalkingBoxAgentConfig.cs` — the per-developer config asset.
- `Scripts/SimplePlayerController.cs` — minimal WASD + mouse-look
  controller so the sample doesn't depend on Unity's Starter Assets.

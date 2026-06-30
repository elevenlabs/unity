# QuickStart

A minimal scene that opens an ElevenLabs voice agent session, renders
the live transcript to a small UI, and closes cleanly on exit. Use this as the
starting point for your own scene — it shows the smallest set of API calls
needed to bring an agent into your game.

## What it demonstrates

- Loading agent credentials from a per-developer `QuickStartConfig` asset
  (gitignored so secrets stay on your machine).
- Opening a session with `Conversation.StartSessionAsync(...)` using the
  default WebSocket transport.
- Wiring the most useful session events: `Disconnected`, `ErrorOccurred`,
  plus `UserTranscriptReceived` and `AgentResponded` for the live transcript.
  Initial connection state is read directly from
  `Conversation.ConversationId` and `Conversation.Status` once
  `StartSessionAsync` returns (the `Connected` event fires synchronously
  inside the factory before the handle is handed back, so it can't be
  caught from a `StartSessionAsync` caller).
- Tearing the session down deterministically on `OnDestroy` (which fires on
  scene unload and when leaving Play mode in the Editor).

The same MonoBehaviour works on WebGL (via the bridged JS SDK transport) and
on the Editor / standalone / mobile targets (via the native `ClientWebSocket`
transport). The public API is identical across platforms.

The on-screen transcript is drawn with `OnGUI` to keep the dependency surface
minimal — no UGUI, TextMeshPro, or Canvas required to run the sample. For
production UI, replace the `OnGUI` block in `QuickStart.cs` with your own text
components and forward the values from the existing event handlers.

## Setup

Assumes the SDK is already installed in a Unity 6.3 LTS project — see the
[main README](../../README.md#install) for the one-line git URL install.

1. **Import the sample** via Window → Package Manager → ElevenAgents (in the
   left-hand package list) → Samples → QuickStart → Import. The files land
   under `Assets/Samples/ElevenAgents/<version>/QuickStart/`.

2. **Create your config asset**:
   - Right-click in the Project view → Create → ElevenLabs → Samples →
     QuickStart Config.
   - Move the asset into any folder named `Resources` somewhere under
     `Assets/` (e.g. `Assets/Resources/QuickStartConfig.asset`).
   - Fill in `Agent Id` — grab one from the
     [ElevenLabs dashboard](https://elevenlabs.io/app/agents), or have
     your AI coding assistant create one via the
     [`agents` skill](https://github.com/elevenlabs/skills/tree/main/agents)
     (`npx skills add elevenlabs/skills`). Leave `Signed Url` blank for
     public agents; fill it in for private agents that require a pre-signed
     URL.

3. **Open the imported scene** (`QuickStart.unity`) and press Play. The
   scene contains a single GameObject named `QuickStart` carrying the
   `QuickStart` MonoBehaviour. The transcript appears on the Game view via
   `OnGUI`; no Canvas or Camera setup is required beyond the scene defaults.

If the config asset is missing or the agent id is empty, the sample logs a
warning to the console and shows "No agent configured" in the status text —
nothing else happens, so your project still runs cleanly.

## Next steps

Once the QuickStart is running, the other samples in this package layer one
feature each on top of the same pattern: client tools, dynamic variables, and
a microphone visualizer. The full conversation API surface lives on the
`Conversation` class (`ElevenLabs.Agents`); see the XML documentation comments
in `Runtime/Core/Conversation.cs` for the complete event and method list.

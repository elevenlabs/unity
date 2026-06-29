# ElevenAgents Unity SDK

The ElevenAgents SDK for Unity.

## Requirements

- Unity 6.3 LTS or later (6000.3.0f1+)
- Player Settings → Player → Other Settings → **Api Compatibility Level** = .NET Standard 2.1 or higher

### Additional WebGL requirements

- Player Settings → WebGL → Publishing Settings → **Use WebAssembly.Table** enabled

See [COMPATIBILITY.md](./COMPATIBILITY.md) for the rationale behind each requirement and the failure modes you'll hit if a setting is wrong.

## Samples

The package ships an importable **QuickStart** sample — a single scene with a MonoBehaviour that opens a session, wires the lifecycle and transcript events, and tears down on scene unload. Import via the Unity Package Manager UI (ElevenAgents → Samples → QuickStart → Import), open the imported `QuickStart.unity` scene, and create a `QuickStartConfig` asset under any `Resources/` folder with your agent id. Full setup in [`Samples/QuickStart/README.md`](./Samples/QuickStart/README.md).

## Error handling

The SDK keeps the user-facing exception surface intentionally small. Catch exceptions from awaitable methods, and subscribe to `Conversation.ErrorOccurred` for runtime errors during an open session. Full surface — exception types, the `ErrorOccurred` event, and a mapping from the `@elevenlabs/client` JS SDK — in [`Docs~/ERROR_HANDLING.md`](./Docs~/ERROR_HANDLING.md).

# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - Unreleased

### Added

- `Conversation` session API: `Conversation.StartSessionAsync(ConversationOptions)` / `EndSession()`, with `ConversationId`, `Status`, `Mode`, and `CanSendFeedback`. Async members return Unity's `Awaitable`.
- `ConversationOptions`: `AgentId` / `SignedUrl` / `ConversationToken`, `ConnectionType` (WebSocket, or WebRTC on WebGL), `Input` / `Output` device config, `OutputAudioSource`, `DynamicVariables`, `Overrides`, `UserId`, `CustomLlmExtraBody`, `EnableDebugLogging`.
- Events: `Connected`, `Disconnected`, `ErrorOccurred`, `StatusChanged`, `ModeChanged`, `CanSendFeedbackChanged`, `InitiationMetadataReceived`, `UserTranscriptReceived`, `AgentResponded`, `AgentResponseCorrected`, `AgentResponseCompleted`, `AgentChatResponsePartReceived`, `AudioReceived`, `Interrupted`, `VadScoreUpdated`, `GuardrailTriggered`, `UnhandledClientToolCall`, `AgentToolRequested`, `AgentToolResponded`, `MCPToolCallReceived`, `MCPConnectionStatusChanged`.
- Messaging: `SendUserMessage`, `SendContextualUpdate`, `SendUserActivity`, `SendMultimodalMessage`, `UploadFileAsync`, `SendFeedback`, `SendMCPToolApprovalResult`.
- Client tools: `RegisterTool<TParams, TResult>` (sync and `Awaitable` handlers), `UnregisterTool`, and `ClientToolException` for reporting tool errors to the agent.
- Audio control: `SetVolume`, `SetMicMuted`, `ChangeInputDevice`, `ChangeOutputDevice`, `GetInputVolume` / `GetOutputVolume`, `GetInputByteFrequencyData` / `GetOutputByteFrequencyData`.
- Native transport for the Editor and desktop standalone players: WebSocket via `ClientWebSocket`, microphone capture via `UnityEngine.Microphone`, and low-latency playback through Unity 6.3's `IAudioGenerator`, optionally through a supplied (spatial) `AudioSource`.
- WebGL transport wrapping `@elevenlabs/client` (WebSocket and WebRTC). WebSocket audio plays through a Web Audio graph that mirrors the supplied `AudioSource`'s volume, position, and spatial settings.
- WebGL JS↔C# bridge primitives: `JsBridge`, `JsObject`, `JsFunction`, `BridgeCallback`, `BridgeException`.
- Protocol DTOs and typed event-args records (`ElevenLabs.Protocol`), generated from the Agents AsyncAPI spec.
- Samples: GettingStarted (walk-up-and-talk with spatial audio, dynamic variables, and overrides) and QuickStart (minimal transcript).
- UPM distribution via git URL. Requires Unity 6.3 LTS (`6000.3`) or later; depends on Input System 1.18.0 and Newtonsoft.Json 3.2.1.

[Unreleased]: https://github.com/elevenlabs/unity/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/elevenlabs/unity/releases/tag/v0.1.0

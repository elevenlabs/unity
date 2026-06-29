# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- **Breaking:** Minimum Unity version bumped from `6000.0` to `6000.3` (Unity 6.3 LTS). Required for the new native audio output engine built on [`Audio.IAudioGenerator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.IAudioGenerator.html) / [`Audio.GeneratorInstance`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.GeneratorInstance.html), which eliminates the streaming-`AudioClip` pre-fill latency that older Unity versions imposed structurally. Unity 6.3 LTS is supported through December 2027.

## [0.1.0] - Unreleased

### Added

- Core session management (`StartSession`, `EndSession`)
- WebSocket transport for native platforms (desktop, mobile, XR)
- `@elevenlabs/client` bridge transport for WebGL
- Audio capture and playback (default mode) across all platforms
- Core conversation events: `OnConnect`, `OnDisconnect`, `OnMessage`, `OnError`, `OnStatusChange`, `OnModeChange`
- WebGL bridge primitives: Promise-as-Task, Observer, JS-initiated Promise
- UPM distribution via git URL

[Unreleased]: https://github.com/elevenlabs/unity/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/elevenlabs/unity/releases/tag/v0.1.0

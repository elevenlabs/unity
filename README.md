# ElevenAgents Unity SDK

> [!CAUTION]
> ⚠️ **Early-stage SDK:** This package is under active development and APIs may change without notice. If you hit a bug or rough edge, please [open a GitHub issue](https://github.com/elevenlabs/unity/issues/new) - your reports directly shape what we fix next.

Drop ElevenLabs voice agents into a Unity scene. Walk up to a cube, it greets you in character; walk out, the session ends — all driven by the same `Conversation` API on standalone, mobile, the Editor, and WebGL.

<!-- Landing media: 30s screen recording of the GettingStarted sample (walk up → greet → walk out, with audio). Tracked separately; drop the file in here once recorded. -->

## Install

In Unity, open **Window → Package Manager → + → Install package from git URL…** and paste:

```text
https://github.com/elevenlabs/unity.git
```

The package targets Unity 6.3 LTS (6000.3.0f1+). See [`COMPATIBILITY.md`](./COMPATIBILITY.md) for the full requirement matrix and the Player Settings flips WebGL needs.

## Get started

The **[Getting Started walkthrough](./Docs~/GETTING_STARTED.md)** is the on-ramp: install, create an agent, drop a talking cube into a scene, press Play. ~10 minutes end-to-end with two routes — import the sample as-is, or paste the components into your own scene.

## Samples

Import via **Window → Package Manager → ElevenAgents → Samples**.

| Sample                                                    | What it shows                                                                                                                                           |
| --------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **[GettingStarted](./Samples~/GettingStarted/README.md)** | Walk-up-and-talk demo: trigger-based session lifecycle, per-instance dynamic variables, spatial audio, and a volume-driven bob. This is the headline.   |
| **[QuickStart](./Samples~/QuickStart/README.md)**         | Smallest possible smoke test — one GameObject, one MonoBehaviour, transcript via `OnGUI`. Use when you want the API surface without any scene dressing. |

## Documentation

- [`Docs~/GETTING_STARTED.md`](./Docs~/GETTING_STARTED.md) — the 10-minute walkthrough.
- [`COMPATIBILITY.md`](./COMPATIBILITY.md) — supported Unity versions, platform matrix, WebGL-specific Player Settings, and the cross-platform vs. native-only audio API table.
- [`Docs~/ERROR_HANDLING.md`](./Docs~/ERROR_HANDLING.md) — exception surface, `Conversation.ErrorOccurred`, and the mapping from the `@elevenlabs/client` JS SDK.
- [`Docs~/ARCHITECTURE.md`](./Docs~/ARCHITECTURE.md) — internal layout (transports, audio pipeline, codegen) for contributors.
- [`CONTRIBUTING.md`](./CONTRIBUTING.md) — how to build, test, and submit changes.
- [`CHANGELOG.md`](./CHANGELOG.md) — release notes.

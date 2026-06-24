# ElevenAgents Unity SDK

The ElevenAgents SDK for Unity.

## Requirements

- Unity 2023.1 or later (Unity 6 LTS recommended)
- Player Settings → Player → Other Settings → **Api Compatibility Level** = .NET Standard 2.1 or higher

### Additional WebGL requirements

- Player Settings → WebGL → Publishing Settings → **Use WebAssembly.Table** enabled

See [COMPATIBILITY.md](./COMPATIBILITY.md) for the rationale behind each requirement and the failure modes you'll hit if a setting is wrong.

## Error types

The SDK keeps the user-facing exception surface intentionally small. Use standard `try` / `catch` for awaitable methods that can fail, and subscribe to `Conversation.ErrorOccurred` for runtime errors that surface during an open session.

### Exception types

| Type | Namespace | When it's thrown |
| --- | --- | --- |
| `ClientToolException` | `ElevenLabs.Agents` | A client-tool handler throws this to surface a structured `error_type` back to the agent (e.g. `"unauthorized"`, `"timeout"`). Any other exception thrown by a handler turns into `is_error: true` on the wire with no `error_type` field — `ClientToolException` is only needed when the handler wants to influence that field. |
| `BridgeException` | `ElevenLabs.WebGL` | A JavaScript error crossed the WebGL bridge — wraps the JS-side `Error.message`. WebGL builds only. Surfaces from `Conversation.StartSessionAsync` (and from JS-side controller methods invoked during the session) when the JS SDK rejects the underlying promise. |
| `ArgumentException` / `ArgumentNullException` / `InvalidOperationException` | `System` | Programmer-error paths: passing `null` where a value is required, asking for an unsupported transport, or calling `StartSessionAsync` before any platform launcher has registered itself. These follow standard BCL conventions; catch them only if your UI needs to recover, otherwise let them propagate. |

Transport-layer failures on native builds (mid-session WebSocket close, DNS errors, TLS handshake failures) propagate as their native types — typically `System.Net.WebSockets.WebSocketException`, `System.IO.IOException`, or `System.OperationCanceledException` — from `StartSessionAsync`. These are not wrapped in an SDK-specific type today.

### `ErrorOccurred` event

`Conversation.ErrorOccurred` is the catch-all signal for errors that surface **after** `StartSessionAsync` has returned. The current payload is a `string` (human-readable message); it will widen to a structured `ErrorArgs` record once the upstream `error` wire frame stabilises (tracked in [`Docs~/plans/method-event-parity-investigation.md`](./Docs~/plans/method-event-parity-investigation.md#resolved-gap-error-type-hierarchy)).

It fires for:

- Server `error` frames received over the open session.
- A client-tool handler that threw without being caught by the dispatcher (the error is also reported back to the agent as `is_error: true`).
- The agent invoking a tool name with no registered handler — unless `UnhandledClientToolCall` has at least one subscriber, in which case the unhandled-call event fires instead.
- Transport teardown errors observed while the session is otherwise alive.

### Mapping from the `@elevenlabs/client` JS SDK

If you're porting code or following JS docs, here's how the JS SDK's error surface lines up with C#:

| `@elevenlabs/client` (JS) | ElevenLabs Unity SDK (C#) |
| --- | --- |
| `SessionConnectionError` thrown from `Conversation.startSession(...)` | Exception thrown from `Conversation.StartSessionAsync(...)` — `BridgeException` on WebGL, transport-layer `System.Net.*` / `System.IO.*` exceptions on native. The `closeCode` / `closeReason` JS fields are folded into the exception message. |
| `onError(message, context)` callback option | `Conversation.ErrorOccurred` event (`Action<string>`). The JS `context` argument is currently flattened into the message; the planned `ErrorArgs` record surfaces it separately. |
| `onUnhandledClientToolCall` callback option | `Conversation.UnhandledClientToolCall` event (`Action<ClientToolCallArgs>`). Subscriber attached → SDK suppresses both `ErrorOccurred` and the automatic `client_tool_result is_error=true` send for the unknown tool. |
| Client tool handler returns `{ status: "error", error_type, error }` | Client tool handler throws `ClientToolException(message, errorType: "...")`. Any other thrown exception → `is_error: true` without an `error_type`. |
| `extractApiErrorMessage` (HTTP upload error parsing helper) | `InvalidOperationException` from `Conversation.UploadFileAsync(...)` — the message carries the HTTP status code and the server's response body. |

The JS SDK historically exported `ConversationError`, `AudioConcatProcessorError`, and `RawAudioProcessorError`; only `SessionConnectionError` survives in `@elevenlabs/client` today. The Unity SDK never mirrored those names — there's nothing to migrate from.

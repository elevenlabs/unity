#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
using Newtonsoft.Json;
using UnityEngine;

namespace ElevenLabs.Native
{
    /// <summary>
    /// <see cref="IConnection"/> implementation backed by
    /// <see cref="ClientWebSocket"/>. Drives the conversation initiation
    /// handshake inside <see cref="CreateAsync(ConversationOptions, CancellationToken)"/>
    /// so the public <see cref="InputFormat"/> / <see cref="OutputFormat"/> /
    /// <see cref="ConversationId"/> properties are populated synchronously by
    /// the time the connection handle is returned — the launcher (in #9b) can
    /// then hand the connection to <see cref="Conversation"/> without any
    /// additional async fan-out for format negotiation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read loop runs on the thread pool; each parsed event is marshalled
    /// back to the Unity main thread via <see cref="Awaitable.MainThreadAsync"/>
    /// before any C# subscriber sees it, so handlers can freely touch Unity
    /// APIs without hitting the main-thread guard.
    /// </para>
    /// <para>
    /// <see cref="Close"/> is sync + idempotent; it cancels the read loop, fires
    /// a best-effort close frame, and disposes the underlying socket. Mirrors
    /// the <c>BridgedWebSocketConnection</c> "sync-then-disposed" discipline so
    /// error-path cleanup in <see cref="Conversation.EndSession"/> behaves
    /// identically across transports.
    /// </para>
    /// </remarks>
    internal sealed class NativeWebSocketConnection : IConnection
    {
        // Wire endpoint per @elevenlabs/client WebSocketConnection.create —
        // append `/v1/convai/conversation?agent_id=<id>` for the agent-id flow,
        // use the signed URL verbatim otherwise.
        private const string DefaultWssOrigin = "wss://api.elevenlabs.io";
        private const string ConversationPathname = "/v1/convai/conversation?agent_id=";

        // Subprotocol negotiated with the server; the JS SDK uses "convai" too
        // (see WebSocketConnection.js — MAIN_PROTOCOL).
        private const string SubProtocol = "convai";

        // Bound the read buffer per receive. The SDK sends individual JSON
        // events that fit comfortably within 8 KiB; larger messages are
        // assembled across multiple receive calls in the read loop below.
        private const int ReceiveChunkSize = 8 * 1024;

        // Cap how long Close() waits for the graceful close handshake before
        // forcibly aborting the socket. Two seconds matches "user wants the
        // session gone now" — a server that won't drop a TCP connection in
        // two seconds is wedged.
        private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(2);

        private static readonly IncomingSocketEventConverter MessageConverter = new();

        private readonly WebSocket _socket;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Task _readTask;

        // Interlocked guard so Close() is idempotent under concurrent calls
        // (e.g., user EndSession + read-loop-detected disconnect racing).
        private int _closed;

        // Set when Close() is the source of the read-loop exit so we don't
        // raise OnDisconnect on the user-initiated path — Conversation has
        // already marked status=Disconnecting and will fire its own
        // Disconnected event with reason=User.
        private int _disconnectSuppressed;

        // Captures the in-flight cleanup work spawned from Close() so test
        // code can deterministically await teardown — Close() itself must
        // remain sync to honour the IConnection.Close contract, but a test
        // that synchronously asserts "the socket is gone" needs a join point.
        private Task? _cleanupTask;

        // Exposed for tests only — production callers route everything through
        // the user-facing Conversation.EndSession, which awaits its own
        // input/output controller closes and doesn't need to peek at the
        // connection's internal cleanup.
        internal Task? CleanupTask => _cleanupTask;

        // Multicast handler list runs through these private fields; Conversation
        // subscribes during construction.
        public event Action<IncomingSocketEvent>? OnMessage;
        public event Action<DisconnectionDetails>? OnDisconnect;

#pragma warning disable CS0067 // Reserved for #9c/d once UnityMicrophoneInput / UnityAudioSourceOutput drive speaking/listening transitions; the wire transport itself doesn't emit mode flips.
        public event Action<Mode>? OnModeChange;
#pragma warning restore CS0067

        public string ConversationId { get; }
        public FormatConfig InputFormat { get; }
        public FormatConfig OutputFormat { get; }

        private NativeWebSocketConnection(
            WebSocket socket,
            string conversationId,
            FormatConfig inputFormat,
            FormatConfig outputFormat
        )
        {
            _socket = socket;
            ConversationId = conversationId;
            InputFormat = inputFormat;
            OutputFormat = outputFormat;
            // Task.Run lifts the long-running receive off the calling thread —
            // the launcher's await returns the moment construction completes
            // even though the loop keeps draining the socket in the background.
            _readTask = Task.Run(ReadLoopAsync);
        }

        // URL builder used by both the production CreateAsync overload and
        // the launcher in #9b. Mirrors WebSocketConnection.js's URL flow:
        // signedUrl wins over agentId; one of the two is required.
        internal static Uri BuildUrl(ConversationOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (!string.IsNullOrEmpty(options.SignedUrl))
                return new Uri(options.SignedUrl);
            if (!string.IsNullOrEmpty(options.AgentId))
                return new Uri($"{DefaultWssOrigin}{ConversationPathname}{options.AgentId}");
            throw new ArgumentException(
                "ConversationOptions must specify either SignedUrl or AgentId for the native WebSocket transport.",
                nameof(options)
            );
        }

        // The native v0.1 transport is WebSocket-only — livekit-client is
        // JS-only, so a ConversationToken / WebRTC session has nowhere to land
        // outside the bridged path. Surfacing this here lets the launcher in
        // #9b reuse the same validation without re-litigating per call site.
        internal static void ValidateTransport(ConversationOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (options.ConnectionType == ConnectionType.WebRTC)
                throw new NotSupportedException(
                    "The native WebSocket transport does not support WebRTC sessions; "
                        + "WebRTC is WebGL-only at v0.1. Use ConnectionType.WebSocket or run on WebGL."
                );
        }

        // Builds the typed ConversationInitiationClientData from a
        // ConversationOptions. Each null / empty container is left unset so
        // Newtonsoft's NullValueHandling.Ignore drops it from the wire payload
        // — matches the omission discipline BridgedSession.BuildSessionConfig
        // applies on the WebGL path. SourceInfo and ToolMockConfig are
        // deliberately not populated yet (see v0.1-parity.md #10).
        internal static ConversationInitiationClientData BuildInitiationData(
            ConversationOptions options
        )
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            return new ConversationInitiationClientData
            {
                ConversationConfigOverride = options.Overrides,
                CustomLlmExtraBody = ToDynamicDictionary(options.CustomLlmExtraBody),
                DynamicVariables = ToDynamicDictionary(options.DynamicVariables),
                UserId = string.IsNullOrEmpty(options.UserId) ? null : options.UserId,
            };
        }

        /// <summary>
        /// Production entry point: validates, builds the URL, opens a
        /// <see cref="ClientWebSocket"/>, sends the initiation event, awaits
        /// <c>conversation_initiation_metadata</c>, and returns a live
        /// connection.
        /// </summary>
        internal static async Task<NativeWebSocketConnection> CreateAsync(
            ConversationOptions options,
            CancellationToken cancellationToken = default
        )
        {
            ValidateTransport(options);
            Uri url = BuildUrl(options);
            ConversationInitiationClientData initiationData = BuildInitiationData(options);

            ClientWebSocket clientSocket = new();
            clientSocket.Options.AddSubProtocol(SubProtocol);
            try
            {
                await clientSocket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                clientSocket.Dispose();
                throw;
            }

            try
            {
                return await CreateInternalAsync(clientSocket, initiationData, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                SafeAbortAndDispose(clientSocket);
                throw;
            }
        }

        /// <summary>
        /// Test seam — drives the handshake on a pre-connected
        /// <see cref="WebSocket"/>. Production callers use
        /// <see cref="CreateAsync(ConversationOptions, CancellationToken)"/>;
        /// tests pair a <see cref="ClientWebSocket"/> with an in-memory server
        /// fixture and pass the client end straight in.
        /// </summary>
        internal static async Task<NativeWebSocketConnection> CreateInternalAsync(
            WebSocket socket,
            ConversationInitiationClientData initiationData,
            CancellationToken cancellationToken = default
        )
        {
            if (socket == null)
                throw new ArgumentNullException(nameof(socket));
            if (initiationData == null)
                throw new ArgumentNullException(nameof(initiationData));

            string initiationJson = JsonConvert.SerializeObject(
                initiationData,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }
            );
            byte[] initiationBytes = Encoding.UTF8.GetBytes(initiationJson);
            await socket
                .SendAsync(
                    new ArraySegment<byte>(initiationBytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken
                )
                .ConfigureAwait(false);

            ConversationInitiationMetadata metadata = await ReadInitiationMetadataAsync(
                    socket,
                    cancellationToken
                )
                .ConfigureAwait(false);

            ConversationInitiationMetadataEvent evt = metadata.ConversationInitiationMetadataEvent;
            // Match the JS SDK: when the server omits user_input_audio_format
            // (older agents), fall back to pcm_16000 so the input controller
            // has a concrete format to negotiate against.
            FormatConfig inputFormat = ParseFormatString(
                string.IsNullOrEmpty(evt.UserInputAudioFormat)
                    ? "pcm_16000"
                    : evt.UserInputAudioFormat
            );
            FormatConfig outputFormat = ParseFormatString(evt.AgentOutputAudioFormat);

            return new NativeWebSocketConnection(
                socket,
                evt.ConversationId,
                inputFormat,
                outputFormat
            );
        }

        public void Send(OutgoingSocketEvent message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            // Fire-and-forget — user code calls Send from the main thread and
            // shouldn't pay the latency of the actual socket write. The lock
            // inside SendInternalAsync serialises concurrent calls because
            // ClientWebSocket.SendAsync is not safe under interleaved writes.
            _ = SendInternalAsync(message);
        }

        private async Task SendInternalAsync(OutgoingSocketEvent message)
        {
            string json = JsonConvert.SerializeObject(
                message,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }
            );
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (_socket.State != WebSocketState.Open)
                    return;
                await _socket
                    .SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        _cts.Token
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                try
                {
                    _sendLock.Release();
                }
                catch (ObjectDisposedException) { }
            }
        }

        public void Close()
        {
            if (Interlocked.CompareExchange(ref _closed, 1, 0) != 0)
                return;
            // Suppress OnDisconnect on the user-initiated path — Conversation
            // already drove EndSessionWithDetails(reason=User) and is about to
            // fire its own Disconnected event with that reason.
            Interlocked.Exchange(ref _disconnectSuppressed, 1);
            // Cancel any pending sends/receives first so the abort can't race
            // a concurrent send mid-frame.
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            // Best-effort graceful close. Fire-and-forget so user code returns
            // immediately; the read loop exits once the close handshake
            // completes (or the abort below kicks in).
            _cleanupTask = GracefulCloseAndCleanupAsync();
        }

        private async Task GracefulCloseAndCleanupAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var timeoutCts = new CancellationTokenSource(CloseHandshakeTimeout);
                    await _socket
                        .CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "User ended conversation",
                            timeoutCts.Token
                        )
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Server-side may already be gone — fall through to the abort.
            }
            finally
            {
                SafeAbortAndDispose(_socket);
                try
                {
                    await _readTask.ConfigureAwait(false);
                }
                catch
                {
                    // Read loop exits via abort; any exception it surfaces is
                    // the symptom, not the cause.
                }
                _cts.Dispose();
                _sendLock.Dispose();
            }
        }

        private async Task ReadLoopAsync()
        {
            byte[] buffer = new byte[ReceiveChunkSize];
            using var assembly = new MemoryStream();
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await _socket
                            .ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (WebSocketException ex)
                    {
                        await RaiseDisconnectAsync(
                                new DisconnectionDetails(
                                    DisconnectionReason.Error,
                                    Message: ex.Message
                                )
                            )
                            .ConfigureAwait(false);
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Echo the close so the server-side CloseAsync completes
                        // its handshake instead of hanging on a missing reply.
                        await TrySendCloseAckAsync(result).ConfigureAwait(false);
                        await RaiseDisconnectAsync(BuildCloseDisconnect(result))
                            .ConfigureAwait(false);
                        return;
                    }

                    assembly.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                        continue;

                    string payload = Encoding.UTF8.GetString(
                        assembly.GetBuffer(),
                        0,
                        (int)assembly.Length
                    );
                    assembly.SetLength(0);

                    IncomingSocketEvent? evt;
                    try
                    {
                        evt = JsonConvert.DeserializeObject<IncomingSocketEvent>(
                            payload,
                            MessageConverter
                        );
                    }
                    catch (JsonException)
                    {
                        // Match the JS SDK's "drop malformed messages" debug
                        // path — invalid frames don't tear the session down.
                        continue;
                    }

                    if (evt == null)
                        continue;

                    // Marshal onto Unity's main thread before invoking user
                    // handlers — subscribers expect to touch Unity APIs.
                    await Awaitable.MainThreadAsync();
                    try
                    {
                        OnMessage?.Invoke(evt);
                    }
                    catch (Exception ex)
                    {
                        // A subscriber throwing should not poison the loop;
                        // surface via Debug.LogException and keep draining.
                        Debug.LogException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                // Any uncaught failure in the loop counts as a transport-level
                // error; surface so Conversation can tear the session down.
                await RaiseDisconnectAsync(
                        new DisconnectionDetails(DisconnectionReason.Error, Message: ex.Message)
                    )
                    .ConfigureAwait(false);
            }
        }

        // Reads exactly one Text message off the socket and parses it as the
        // conversation_initiation_metadata event. The JS SDK does the same in
        // WebSocketConnection.create — the server may deliver other event
        // types out-of-band, but the first valid one MUST be the metadata.
        private static async Task<ConversationInitiationMetadata> ReadInitiationMetadataAsync(
            WebSocket socket,
            CancellationToken cancellationToken
        )
        {
            byte[] buffer = new byte[ReceiveChunkSize];
            using var assembly = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new IOException(
                        "WebSocket closed before conversation_initiation_metadata was received: "
                            + $"{result.CloseStatus} {result.CloseStatusDescription}"
                    );
                assembly.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;
                string payload = Encoding.UTF8.GetString(
                    assembly.GetBuffer(),
                    0,
                    (int)assembly.Length
                );
                assembly.SetLength(0);

                IncomingSocketEvent? evt = JsonConvert.DeserializeObject<IncomingSocketEvent>(
                    payload,
                    MessageConverter
                );
                if (evt is ConversationInitiationMetadata metadata)
                    return metadata;
                // Anything else before the metadata is unexpected. The SDK's
                // JS path warns ("First received message is not conversation
                // metadata.") but keeps waiting; mirror that — drop and read
                // the next frame.
                Debug.LogWarning(
                    $"NativeWebSocketConnection: skipping pre-handshake event of type "
                        + $"'{evt?.GetType().Name ?? "null"}' while awaiting metadata."
                );
            }
        }

        // Sends a close frame in response to a server-initiated close so the
        // WebSocket protocol handshake actually completes. Wrapped in the send
        // lock so it can't interleave with a user-initiated Send mid-frame.
        private async Task TrySendCloseAckAsync(WebSocketReceiveResult result)
        {
            try
            {
                await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (_socket.State == WebSocketState.CloseReceived)
                {
                    await _socket
                        .CloseOutputAsync(
                            result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            result.CloseStatusDescription,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // The peer may already be gone; best-effort.
            }
            finally
            {
                try
                {
                    _sendLock.Release();
                }
                catch (ObjectDisposedException) { }
            }
        }

        // Mirrors the JS SDK union: closeCode 1000 ⇒ reason=Agent, otherwise
        // reason=Error. The context carries the wire-level discriminator so
        // user handlers can pattern-match on it just like the bridged path.
        private static DisconnectionDetails BuildCloseDisconnect(WebSocketReceiveResult result)
        {
            int? closeCode = result.CloseStatus.HasValue
                ? (int)result.CloseStatus.Value
                : (int?)null;
            string? closeReason = result.CloseStatusDescription;
            DisconnectionContext context = new(Type: "close", Reason: closeReason, Code: closeCode);
            if (closeCode == 1000)
            {
                return new DisconnectionDetails(
                    DisconnectionReason.Agent,
                    Context: context,
                    CloseCode: closeCode,
                    CloseReason: closeReason
                );
            }
            return new DisconnectionDetails(
                DisconnectionReason.Error,
                Message: closeReason ?? "The connection was closed by the server.",
                Context: context,
                CloseCode: closeCode,
                CloseReason: closeReason
            );
        }

        private async Task RaiseDisconnectAsync(DisconnectionDetails details)
        {
            // Suppression bit is set inside Close() before _cts.Cancel runs,
            // so by the time the read loop notices, this check is reliable.
            if (Volatile.Read(ref _disconnectSuppressed) != 0)
                return;
            await Awaitable.MainThreadAsync();
            try
            {
                OnDisconnect?.Invoke(details);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        // Parses the SDK's "<codec>_<sampleRate>" format string into the Core
        // FormatConfig record. Throws on unrecognised codecs to surface a
        // contract violation rather than silently mishandling audio.
        internal static FormatConfig ParseFormatString(string format)
        {
            if (string.IsNullOrEmpty(format))
                throw new ArgumentException("Format string must be non-empty.", nameof(format));
            int sep = format.IndexOf('_');
            if (sep <= 0 || sep == format.Length - 1)
                throw new FormatException(
                    $"Invalid format '{format}': expected '<codec>_<sampleRate>'."
                );
            string codec = format.Substring(0, sep);
            if (codec != "pcm" && codec != "ulaw")
                throw new FormatException($"Invalid codec '{codec}' in format '{format}'.");
            if (!int.TryParse(format.Substring(sep + 1), out int sampleRate))
                throw new FormatException(
                    $"Invalid sample rate '{format.Substring(sep + 1)}' in format '{format}'."
                );
            return new FormatConfig(codec, sampleRate);
        }

        private static Dictionary<string, dynamic>? ToDynamicDictionary(
            IReadOnlyDictionary<string, object>? source
        )
        {
            if (source == null || source.Count == 0)
                return null;
            var dict = new Dictionary<string, dynamic>(source.Count);
            foreach (var pair in source)
                dict[pair.Key] = pair.Value;
            return dict;
        }

        private static void SafeAbortAndDispose(WebSocket socket)
        {
            try
            {
                socket.Abort();
            }
            catch { }
            try
            {
                socket.Dispose();
            }
            catch { }
        }
    }
}

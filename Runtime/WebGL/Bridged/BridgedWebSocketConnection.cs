#nullable enable

using System;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ElevenLabs.WebGL.Bridged
{
    /// <summary>
    /// <see cref="IConnection"/> implementation backed by an
    /// <c>@elevenlabs/client</c> <c>WebSocketConnection</c> handle obtained via
    /// the JS factory <c>createWebSocketConnection</c>. Translates Core
    /// outgoing events into <c>connection.sendMessage(msg)</c> JS calls, and
    /// fans the JS-side <c>onMessage</c> / <c>onDisconnect</c> /
    /// <c>onModeChange</c> callbacks back into typed Core events.
    /// </summary>
    /// <remarks>
    /// Owns the supplied <see cref="JsObject"/>: <see cref="Close"/> disposes
    /// the handle along with the three <see cref="BridgeCallback"/>s wired in
    /// the constructor. After <see cref="Close"/> the instance is single-use
    /// — callers should construct a new one for a new session.
    /// </remarks>
    internal sealed class BridgedWebSocketConnection : IConnection
    {
        private static readonly IncomingSocketEventConverter MessageConverter = new();

        private readonly IJsObject _connection;
        private readonly BridgeCallback _onMessageCallback;
        private readonly BridgeCallback _onDisconnectCallback;
        private readonly BridgeCallback _onModeChangeCallback;
        private IJsFunction? _audioDetach;
        private bool _disposed;

        // Exposed for BridgedSession.StartAsync — attachDefaultAudio re-wires
        // JS-side `connection.onMessage` through `withoutAudioPayload`, and
        // passing the SAME BridgeCallback (rather than allocating a parallel
        // one) keeps one C# delegate / one disposal site even though the JS
        // subscription is set twice during setup (once in this constructor,
        // once by attachDefaultAudio — the latter wins).
        internal BridgeCallback IncomingMessageCallback => _onMessageCallback;

        public string ConversationId => _connection.Get<string>("conversationId") ?? string.Empty;

        public FormatConfig InputFormat =>
            BridgedConnectionMarshalling.ParseFormatConfig(_connection.Get<JObject>("inputFormat"));

        public FormatConfig OutputFormat =>
            BridgedConnectionMarshalling.ParseFormatConfig(
                _connection.Get<JObject>("outputFormat")
            );

        public event Action<IncomingSocketEvent>? OnMessage;
        public event Action<DisconnectionDetails>? OnDisconnect;
        public event Action<Mode>? OnModeChange;

        /// <summary>
        /// Wraps an already-created JS <c>WebSocketConnection</c> handle. The
        /// caller is responsible for constructing <paramref name="connection"/>
        /// via <c>JsBridge.InvokeFactoryAsync&lt;JsObject&gt;("createWebSocketConnection", config)</c>
        /// — this wrapper takes ownership of the handle from that point on.
        /// Typed against <see cref="IJsObject"/> so router-level tests can
        /// substitute a stub without going through the WebGL primitives.
        /// </summary>
        internal BridgedWebSocketConnection(IJsObject connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));

            // Wire one BridgeCallback per JS-side event; the C# multicast
            // events fan out to all subscribers without round-tripping through
            // additional JS subscriptions.
            _onMessageCallback = BridgeCallback.Wrap(HandleJsMessage);
            _connection.Call("onMessage", _onMessageCallback);

            _onDisconnectCallback = BridgeCallback.Wrap(HandleJsDisconnect);
            _connection.Call("onDisconnect", _onDisconnectCallback);

            _onModeChangeCallback = BridgeCallback.Wrap<string>(HandleJsModeChange);
            _connection.Call("onModeChange", _onModeChangeCallback);
        }

        public void Send(OutgoingSocketEvent message)
        {
            // BridgeArgEncoder serialises the typed event via Newtonsoft using
            // the [JsonProperty] attributes on each OutgoingSocketEvent subclass
            // — wire shape lines up with the SDK's expected message format.
            _connection.Call("sendMessage", message);
        }

        /// <summary>
        /// Register the audio-wiring detach handle returned from the
        /// JS <c>attachDefaultAudio</c> factory. <see cref="Close"/> invokes it
        /// before the JS-side <c>connection.close()</c> so
        /// <c>attachInputToConnection</c> / <c>attachConnectionToOutput</c>
        /// see a live transport during their detach. Idempotent at most once
        /// per instance; calling twice is a setup bug.
        /// </summary>
        internal void AttachAudioDetach(IJsFunction detach)
        {
            if (detach == null)
                throw new ArgumentNullException(nameof(detach));
            if (_audioDetach != null)
                throw new InvalidOperationException(
                    "Audio detach handle already attached to this connection."
                );
            _audioDetach = detach;
        }

        public void Close()
        {
            if (_disposed)
                return;
            _disposed = true;

            // The JS side may throw if close() runs against a half-torn-down
            // transport; always release the C# handles afterwards so we don't
            // leak entries in the JS registry.
            try
            {
                // Sever JS-internal audio wiring first so the input controller
                // stops feeding bytes into the connection before we tear it
                // down. Failure here doesn't block the close — finally still
                // runs and releases every other handle.
                if (_audioDetach != null)
                {
                    try
                    {
                        _audioDetach.Call();
                    }
                    finally
                    {
                        _audioDetach.Dispose();
                        _audioDetach = null;
                    }
                }
                _connection.Call("close");
            }
            finally
            {
                _onMessageCallback.Dispose();
                _onDisconnectCallback.Dispose();
                _onModeChangeCallback.Dispose();
                _connection.Dispose();
            }
        }

        private void HandleJsMessage(string payload)
        {
            // JS-side bridge wraps the IncomingSocketEvent object as a JSON
            // string; the polymorphic converter dispatches on the wire `type`
            // discriminator and returns the matching concrete subtype.
            IncomingSocketEvent? evt = JsonConvert.DeserializeObject<IncomingSocketEvent>(
                payload,
                MessageConverter
            );
            if (evt == null)
                return;
            OnMessage?.Invoke(evt);
        }

        private void HandleJsDisconnect(string payload)
        {
            // The SDK delivers a union {reason, message?, context?, closeCode?,
            // closeReason?}; marshalling flattens it onto the Core record.
            JObject obj = JObject.Parse(payload);
            DisconnectionDetails details = BridgedConnectionMarshalling.ParseDisconnectionDetails(
                obj
            );
            OnDisconnect?.Invoke(details);
        }

        private void HandleJsModeChange(string modeString)
        {
            // Unknown modes are silently dropped — a server-side rename
            // shouldn't crash the conversation; subscribers that care about
            // mode-flips will just miss one tick.
            Mode? mode = BridgedConnectionMarshalling.ParseMode(modeString);
            if (mode.HasValue)
                OnModeChange?.Invoke(mode.Value);
        }
    }
}

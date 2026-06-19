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
    /// <c>@elevenlabs/client</c> <c>WebRTCConnection</c> handle obtained via
    /// the JS factory <c>createWebRTCConnection</c>. Behaves the same as
    /// <see cref="BridgedWebSocketConnection"/> on the messaging surface;
    /// the difference is that WebRTC connections expose their coupled audio
    /// I/O via <see cref="GetCoupledInput"/> / <see cref="GetCoupledOutput"/>
    /// (the JS SDK creates them eagerly because livekit-client wires the
    /// peer connection at construction).
    /// </summary>
    /// <remarks>
    /// The coupled controller accessors return fresh wrappers each call —
    /// the underlying JS objects are the same instance every time, but the
    /// C# <see cref="JsObject"/> handle wrapping them is created per Get.
    /// <see cref="BridgedSession"/> (Phase 5.3) calls each exactly once at
    /// session start and shares the wrapper through its lifetime.
    /// </remarks>
    internal sealed class BridgedWebRTCConnection : IConnection
    {
        private static readonly IncomingSocketEventConverter MessageConverter = new();

        private readonly IJsObject _connection;
        private readonly BridgeCallback _onMessageCallback;
        private readonly BridgeCallback _onDisconnectCallback;
        private readonly BridgeCallback _onModeChangeCallback;
        private bool _disposed;

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
        /// Wraps an already-created JS <c>WebRTCConnection</c> handle. Typed
        /// against <see cref="IJsObject"/> so router-level tests can substitute
        /// a stub without going through the WebGL primitives.
        /// </summary>
        internal BridgedWebRTCConnection(IJsObject connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));

            _onMessageCallback = BridgeCallback.Wrap(HandleJsMessage);
            _connection.Call("onMessage", _onMessageCallback);

            _onDisconnectCallback = BridgeCallback.Wrap(HandleJsDisconnect);
            _connection.Call("onDisconnect", _onDisconnectCallback);

            _onModeChangeCallback = BridgeCallback.Wrap<string>(HandleJsModeChange);
            _connection.Call("onModeChange", _onModeChangeCallback);
        }

        public void Send(OutgoingSocketEvent message)
        {
            _connection.Call("sendMessage", message);
        }

        public void Close()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
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

        /// <summary>
        /// Returns a bridged wrapper over the connection's pre-wired input
        /// controller. Consumed by <see cref="BridgedSession"/> (Phase 5.3)
        /// to assemble the <c>(connection, input, output)</c> triple
        /// <see cref="Conversation"/> expects.
        /// </summary>
        internal BridgedInputController GetCoupledInput() =>
            new(_connection.Get<IJsObject>("input"));

        /// <summary>
        /// Returns a bridged wrapper over the connection's pre-wired output
        /// controller. See <see cref="GetCoupledInput"/> for context.
        /// </summary>
        internal BridgedOutputController GetCoupledOutput() =>
            new(_connection.Get<IJsObject>("output"));

        private void HandleJsMessage(string payload)
        {
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
            JObject obj = JObject.Parse(payload);
            DisconnectionDetails details = BridgedConnectionMarshalling.ParseDisconnectionDetails(
                obj
            );
            OnDisconnect?.Invoke(details);
        }

        private void HandleJsModeChange(string modeString)
        {
            Mode? mode = BridgedConnectionMarshalling.ParseMode(modeString);
            if (mode.HasValue)
                OnModeChange?.Invoke(mode.Value);
        }
    }
}

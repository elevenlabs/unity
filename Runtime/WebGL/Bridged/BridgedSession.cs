#nullable enable

using System;
using ElevenLabs.Agents;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ElevenLabs.WebGL.Bridged
{
    /// <summary>
    /// WebGL session orchestrator. Builds the
    /// <see cref="IConnection"/> / <see cref="IInputController"/> /
    /// <see cref="IOutputController"/> triple that the cross-platform
    /// <see cref="Conversation"/> expects, by driving the JS-side factories
    /// registered by <c>Bridge~/src/connection/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two branches keyed on
    /// <see cref="ConversationOptions.ConnectionType"/>:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>WebSocket</c> — creates the connection, input, and output
    /// independently; calls the <c>attachDefaultAudio</c> factory to glue
    /// them inside JavaScript (so audio bytes never cross the bridge); stashes
    /// the returned detach handle on the connection so its
    /// <see cref="BridgedWebSocketConnection.Close"/> tears the wiring down.
    /// </description></item>
    /// <item><description>
    /// <c>WebRTC</c> — creates the connection only; reads the coupled input
    /// and output that the SDK pre-wired inside livekit-client. No
    /// <c>attachDefaultAudio</c> call.
    /// </description></item>
    /// </list>
    /// <para>
    /// Phase 5.4 wires <see cref="Conversation.StartSessionAsync"/> to delegate
    /// here under <c>#if UNITY_WEBGL</c>; until then this class is consumed
    /// only by tests and any explicit WebGL launcher experiments.
    /// </para>
    /// </remarks>
    internal sealed class BridgedSession
    {
        /// <summary>The <see cref="IConnection"/> implementation to hand to <see cref="Conversation"/>.</summary>
        internal IConnection Connection { get; }

        /// <summary>The <see cref="IInputController"/> implementation to hand to <see cref="Conversation"/>.</summary>
        internal IInputController Input { get; }

        /// <summary>The <see cref="IOutputController"/> implementation to hand to <see cref="Conversation"/>.</summary>
        internal IOutputController Output { get; }

        private BridgedSession(
            IConnection connection,
            IInputController input,
            IOutputController output
        )
        {
            Connection = connection;
            Input = input;
            Output = output;
        }

        /// <summary>
        /// Open a JS-backed session against the agent identified by
        /// <paramref name="options"/>. Awaits the connection factory, then —
        /// for WebSocket — the media-device factories and the audio glue;
        /// for WebRTC, reads the coupled input/output the SDK pre-wired.
        /// </summary>
        /// <param name="options">Session inputs forwarded to the JS factories.</param>
        /// <returns>An assembled session ready to drive a <see cref="Conversation"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
        internal static Awaitable<BridgedSession> StartAsync(ConversationOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            return options.ConnectionType == ConnectionType.WebRTC
                ? StartWebRtcAsync(options)
                : StartWebSocketAsync(options);
        }

        // WebRTC arm: connection brings its own coupled I/O via the livekit
        // peer wiring; no separate factories, no attachDefaultAudio.
        private static async Awaitable<BridgedSession> StartWebRtcAsync(ConversationOptions options)
        {
            JObject sessionConfig = BuildSessionConfig(options);
            JsObject connectionHandle = await JsBridge.InvokeFactoryAsync<JsObject>(
                "createWebRTCConnection",
                sessionConfig
            );
            BridgedWebRTCConnection? connection = null;
            try
            {
                connection = new BridgedWebRTCConnection(connectionHandle);
                BridgedInputController input = connection.GetCoupledInput();
                BridgedOutputController output = connection.GetCoupledOutput();
                return new BridgedSession(connection, input, output);
            }
            catch
            {
                // Best-effort cleanup — Close is sync + idempotent and its
                // try/finally releases every handle even if the JS close throws.
                SafeClose(() => connection?.Close());
                if (connection == null)
                    connectionHandle?.Dispose();
                throw;
            }
        }

        // WebSocket arm: connection + separate input + output, glued by
        // attachDefaultAudio. The detach handle is registered on the
        // connection so Close tears the JS wiring down before close().
        private static async Awaitable<BridgedSession> StartWebSocketAsync(
            ConversationOptions options
        )
        {
            JObject sessionConfig = BuildSessionConfig(options);
            JsObject connectionHandle = await JsBridge.InvokeFactoryAsync<JsObject>(
                "createWebSocketConnection",
                sessionConfig
            );

            BridgedWebSocketConnection? connection = null;
            JsObject? inputHandle = null;
            JsObject? outputHandle = null;
            BridgedInputController? input = null;
            BridgedOutputController? output = null;
            JsFunction? detach = null;
            try
            {
                connection = new BridgedWebSocketConnection(connectionHandle);

                // The SDK's WebSocketConnection.create() awaits the
                // conversation_initiation_metadata event before resolving, so
                // inputFormat / outputFormat are populated by the time we read
                // them here.
                JObject inputConfig = BuildInputConfig(options, connection.InputFormat);
                JObject outputConfig = BuildOutputConfig(options, connection.OutputFormat);

                inputHandle = await JsBridge.InvokeFactoryAsync<JsObject>(
                    "createMediaDeviceInput",
                    inputConfig
                );
                outputHandle = await JsBridge.InvokeFactoryAsync<JsObject>(
                    "createMediaDeviceOutput",
                    outputConfig
                );
                input = new BridgedInputController(inputHandle);
                output = new BridgedOutputController(outputHandle);

                // Pass the connection's own IncomingMessageCallback so the JS
                // side's withoutAudioPayload wrapper fires the same C# delegate
                // BridgedWebSocketConnection wired in its ctor — one delegate,
                // one disposal site, even though connection.onMessage is set
                // twice (the second call wins).
                detach = await JsBridge.InvokeFactoryAsync<JsFunction>(
                    "attachDefaultAudio",
                    connectionHandle,
                    inputHandle,
                    outputHandle,
                    connection.IncomingMessageCallback
                );
                connection.AttachAudioDetach(detach);

                return new BridgedSession(connection, input, output);
            }
            catch
            {
                SafeClose(() => connection?.Close());
                detach?.Dispose();
                outputHandle?.Dispose();
                inputHandle?.Dispose();
                if (connection == null)
                    connectionHandle?.Dispose();
                throw;
            }
        }

        // Build the SessionConfig object the JS factories accept. JObject keeps
        // null properties out of the wire shape — the SDK's union types
        // (PublicSessionConfig / PrivateWebSocketSessionConfig /
        // PrivateWebRTCSessionConfig) reject mutually-exclusive fields when
        // both are present, so omission is required, not just preferred.
        internal static JObject BuildSessionConfig(ConversationOptions options)
        {
            var obj = new JObject
            {
                ["connectionType"] =
                    options.ConnectionType == ConnectionType.WebRTC ? "webrtc" : "websocket",
            };
            if (!string.IsNullOrEmpty(options.AgentId))
                obj["agentId"] = options.AgentId;
            if (!string.IsNullOrEmpty(options.SignedUrl))
                obj["signedUrl"] = options.SignedUrl;
            if (!string.IsNullOrEmpty(options.ConversationToken))
                obj["conversationToken"] = options.ConversationToken;
            // Omit on null/empty: upstream's session-config union types tolerate
            // missing fields cleanly, and an empty `dynamic_variables` object
            // adds wire noise without changing behaviour.
            if (options.DynamicVariables != null && options.DynamicVariables.Count > 0)
                obj["dynamicVariables"] = JObject.FromObject(options.DynamicVariables);
            return obj;
        }

        // MediaDeviceInputConfig = FormatConfig & InputConfig & AudioWorkletConfig
        // on the JS side; we flatten the connection's negotiated format plus the
        // device-id selection from ConversationOptions.Input. Worklet paths /
        // chunk duration stay on SDK defaults for v0.1.
        private static JObject BuildInputConfig(
            ConversationOptions options,
            FormatConfig connectionInputFormat
        )
        {
            var obj = new JObject
            {
                ["format"] = connectionInputFormat.Format,
                ["sampleRate"] = connectionInputFormat.SampleRate,
            };
            InputDeviceConfig? device = options.Input;
            if (device != null)
            {
                if (!string.IsNullOrEmpty(device.InputDeviceId))
                    obj["inputDeviceId"] = device.InputDeviceId;
                if (device.PreferHeadphonesForIosDevices.HasValue)
                    obj["preferHeadphonesForIosDevices"] = device
                        .PreferHeadphonesForIosDevices
                        .Value;
            }
            return obj;
        }

        // MediaDeviceOutputConfig mirrors the input shape — format + sample
        // rate from the connection, optional output device id from options.
        private static JObject BuildOutputConfig(
            ConversationOptions options,
            FormatConfig connectionOutputFormat
        )
        {
            var obj = new JObject
            {
                ["format"] = connectionOutputFormat.Format,
                ["sampleRate"] = connectionOutputFormat.SampleRate,
            };
            OutputDeviceConfig? device = options.Output;
            if (device != null && !string.IsNullOrEmpty(device.OutputDeviceId))
                obj["outputDeviceId"] = device.OutputDeviceId;
            return obj;
        }

        // Swallows cleanup-path errors so the original setup failure surfaces
        // intact instead of getting overwritten by a follow-on JS exception.
        private static void SafeClose(Action close)
        {
            try
            {
                close();
            }
            catch
            {
                // Best-effort cleanup; the outer throw carries the real error.
            }
        }
    }
}

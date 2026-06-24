#nullable enable

using System;
using System.Collections.Generic;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
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
            // OutputAudioSource is honored on the WebSocket arm but not on
            // WebRTC: the audio path goes through livekit-client's
            // RemoteAudioTrack, which doesn't expose a hook for an external
            // sink. A v0.3 WebRTCAudioAdapter will close this gap; until
            // then surface a one-time warning so users don't silently miss
            // the spatial routing they expected.
            if (options.OutputAudioSource != null)
            {
                UnityEngine.Debug.LogWarning(
                    "[ElevenLabs] ConversationOptions.OutputAudioSource is ignored on the "
                        + "WebRTC transport: livekit-client owns audio playback and the SDK "
                        + "can't redirect it to a Unity AudioSource yet. Coming in v0.3."
                );
            }

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
            WebAudioBackedOutput? output = null;
            JsFunction? detach = null;
            try
            {
                connection = new BridgedWebSocketConnection(connectionHandle);

                // The SDK's WebSocketConnection.create() awaits the
                // conversation_initiation_metadata event before resolving, so
                // inputFormat / outputFormat are populated by the time we read
                // them here.
                JObject inputConfig = BuildInputConfig(options, connection.InputFormat);
                JObject outputConfig = BuildOutputSinkConfig(connection.OutputFormat);

                if (options.Output != null && !string.IsNullOrEmpty(options.Output.OutputDeviceId))
                {
                    UnityEngine.Debug.LogWarning(
                        "[ElevenLabs] OutputDeviceConfig.OutputDeviceId is ignored on WebGL: "
                            + "Web Audio doesn't expose per-context output device selection in the "
                            + "stable browser API. Falling back to the system default device."
                    );
                }

                inputHandle = await JsBridge.InvokeFactoryAsync<JsObject>(
                    "createMediaDeviceInput",
                    inputConfig
                );
                outputHandle = await JsBridge.InvokeFactoryAsync<JsObject>(
                    "createWebAudioSink",
                    outputConfig
                );
                input = new BridgedInputController(inputHandle);
                // OutputAudioSource is honored on WebGL's WebSocket arm:
                // WebAudioBackedOutput polls the supplied AudioSource's
                // properties per frame and mirrors them onto the JS sink's
                // Web Audio graph. When null, the sink plays unspatialized
                // mono at unit volume.
                output = new WebAudioBackedOutput(outputHandle, options.OutputAudioSource);

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
        //
        // Key casing note: JS SDK's SessionConfig is camelCase at the top
        // levels (`firstMessage`, `voiceId`, `textOnly`, …) and the SDK
        // re-emits it as snake_case on the wire inside constructOverrides().
        // The nested `agent.prompt` object is a pass-through to the wire and
        // stays in its generated snake_case shape. The hand-built JObject
        // below mirrors that contract exactly.
        internal static JObject BuildSessionConfig(ConversationOptions options)
        {
            var obj = new JObject
            {
                ["connectionType"] =
                    options.ConnectionType == ConnectionType.WebRTC ? "webrtc" : "websocket",
            };
            SetIfPresent(obj, "agentId", options.AgentId);
            SetIfPresent(obj, "signedUrl", options.SignedUrl);
            SetIfPresent(obj, "conversationToken", options.ConversationToken);
            SetIfPresent(obj, "userId", options.UserId);
            SetIfPresent(obj, "dynamicVariables", FromDictionary(options.DynamicVariables));
            SetIfPresent(obj, "customLlmExtraBody", FromDictionary(options.CustomLlmExtraBody));
            SetIfPresent(obj, "overrides", BuildOverrides(options.Overrides));
            return obj;
        }

        // JS SDK's SessionConfig.overrides shape: top-level keys are
        // camelCase, `agent.prompt` is the wire-shape pass-through object.
        // Fields the JS SDK does not surface (e.g. `turn.soft_timeout_config`)
        // are silently dropped here — the typed property still appears on the
        // Unity SDK's public surface so call sites compile against the eventual
        // native transport, but the bridged handshake will not carry them
        // until #9 lands a non-JS-SDK code path. See ConversationOptions.Overrides
        // docs for the user-facing description.
        private static JObject? BuildOverrides(ConversationConfigOverride? overrides)
        {
            if (overrides == null)
                return null;
            var obj = new JObject();
            JObject? agent = BuildAgentOverride(overrides.Agent);
            JObject? tts = BuildTtsOverride(overrides.Tts);
            JObject? conv = BuildConversationOverride(overrides.Conversation);
            SetIfPresent(obj, "agent", agent);
            SetIfPresent(obj, "tts", tts);
            SetIfPresent(obj, "conversation", conv);
            return obj.Count == 0 ? null : obj;
        }

        private static JObject? BuildAgentOverride(ConversationConfigOverrideAgent? agent)
        {
            if (agent == null)
                return null;
            var obj = new JObject();
            SetIfPresent(obj, "firstMessage", agent.FirstMessage);
            SetIfPresent(obj, "language", agent.Language);
            // agent.prompt rides the wire shape straight through — JS SDK
            // doesn't rename its inner fields, so JObject.FromObject's
            // snake_case [JsonProperty] output is what the server expects.
            SetIfPresent(
                obj,
                "prompt",
                agent.Prompt == null ? null : JObject.FromObject(agent.Prompt)
            );
            return obj.Count == 0 ? null : obj;
        }

        private static JObject? BuildTtsOverride(ConversationConfigOverrideTts? tts)
        {
            if (tts == null)
                return null;
            var obj = new JObject();
            SetIfPresent(obj, "voiceId", tts.VoiceId);
            if (tts.Stability.HasValue)
                obj["stability"] = tts.Stability.Value;
            if (tts.Speed.HasValue)
                obj["speed"] = tts.Speed.Value;
            if (tts.SimilarityBoost.HasValue)
                obj["similarityBoost"] = tts.SimilarityBoost.Value;
            return obj.Count == 0 ? null : obj;
        }

        private static JObject? BuildConversationOverride(
            ConversationConfigOverrideConversation? conversation
        )
        {
            if (conversation == null)
                return null;
            var obj = new JObject();
            if (conversation.TextOnly.HasValue)
                obj["textOnly"] = conversation.TextOnly.Value;
            return obj.Count == 0 ? null : obj;
        }

        private static JObject? FromDictionary(IReadOnlyDictionary<string, object>? dict)
        {
            return dict == null || dict.Count == 0 ? null : JObject.FromObject(dict);
        }

        // Centralises the "omit on null / empty" rule used by every field
        // forwarded into the SessionConfig: upstream's union types tolerate
        // missing fields cleanly, and an empty container adds wire noise
        // without changing behaviour. Treats a null JToken (or one wrapping a
        // JSON null) as absent, plus empty strings / objects / arrays.
        private static void SetIfPresent(JObject obj, string key, JToken? value)
        {
            if (value == null || value.Type == JTokenType.Null)
                return;
            if (value.Type == JTokenType.String && string.IsNullOrEmpty(value.Value<string>()))
                return;
            if (value is JObject jo && jo.Count == 0)
                return;
            if (value is JArray ja && ja.Count == 0)
                return;
            obj[key] = value;
        }

        private static void SetIfPresent(JObject obj, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                obj[key] = value;
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

        // WebAudioSinkConfig from Bridge~/src/connection/web-audio-sink.ts is
        // just `{ sampleRate: number }`. Format isn't passed because the sink
        // is PCM-only at v0.1 (matches the SDK's audio wire contract).
        // OutputDeviceId isn't represented because Web Audio doesn't expose
        // per-context output device selection in the stable browser API; the
        // caller is warned at session start if they set one.
        private static JObject BuildOutputSinkConfig(FormatConfig connectionOutputFormat)
        {
            return new JObject { ["sampleRate"] = connectionOutputFormat.SampleRate };
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

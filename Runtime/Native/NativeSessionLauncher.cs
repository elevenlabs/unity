#nullable enable

using System;
using ElevenLabs.Agents;
using UnityEngine;

namespace ElevenLabs.Native
{
    /// <summary>
    /// Platform glue that hands <see cref="Conversation.StartSessionAsync"/>
    /// a native <see cref="System.Net.WebSockets.ClientWebSocket"/>-backed
    /// implementation. Registers <see cref="StartAsync"/> as
    /// <see cref="Conversation.SessionFactory"/> so cross-platform Core code
    /// can open a session on Editor / standalone / mobile without referencing
    /// the native asmdef directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors <c>BridgedSessionLauncher</c>: two registration triggers — one
    /// for runtime / Play Mode, one for the Editor — so the factory is present
    /// in every context where <see cref="Conversation"/> could legitimately be
    /// reached, including Edit Mode tests that exercise the static entry point.
    /// </para>
    /// <para>
    /// The <c>#if !UNITY_WEBGL</c> guard around <see cref="Register"/> is
    /// load-bearing: the WebGL asmdef ships with
    /// <c>includePlatforms: ["Editor", "WebGL"]</c> so the ~200 Bridged Edit
    /// Mode tests can compile in the Editor regardless of active build target,
    /// which means both this launcher and <c>BridgedSessionLauncher</c> get
    /// loaded when the active platform is Editor + non-WebGL. The guard makes
    /// sure only one launcher actually assigns
    /// <see cref="Conversation.SessionFactory"/>.
    /// </para>
    /// <para>
    /// Input is captured by <see cref="UnityMicrophoneInput"/> at the
    /// negotiated user-input format; output is played by
    /// <see cref="UnityAudioSourceOutput"/> at the negotiated agent-output
    /// format. Tests inject their own controllers via the
    /// <see cref="BuildConversation"/> overload that takes them explicitly.
    /// </para>
    /// </remarks>
    internal static class NativeSessionLauncher
    {
#if !UNITY_WEBGL
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RuntimeInit() => Register();
#endif

#if UNITY_EDITOR && !UNITY_WEBGL
        // Mirrors RuntimeInit for the Editor / Edit Mode test domain, where
        // RuntimeInitializeOnLoadMethod is not invoked unless Play Mode is
        // entered. UnityEditor.dll is only available when the asmdef compiles
        // for the Editor target — guarded by UNITY_EDITOR so non-Editor
        // platforms never see this attribute.
        [UnityEditor.InitializeOnLoadMethod]
        private static void EditorInit() => Register();
#endif

        private static void Register()
        {
            Conversation.SessionFactory = StartAsync;
        }

        // Drives NativeWebSocketConnection.CreateAsync, opens the microphone
        // at the negotiated input format and the AudioSource at the
        // negotiated output format, then hands all three to BuildConversation
        // to construct the cross-platform Conversation and mark it Connected.
        // Status.Connecting is skipped on purpose: callers learn they're
        // connecting from the awaited Awaitable, not from a transitory state
        // on a Conversation reference they don't yet hold. Mirrors
        // BridgedSessionLauncher.StartAsync's shape so behaviour stays
        // uniform across transports.
        internal static async Awaitable<Conversation> StartAsync(ConversationOptions options)
        {
            ValidateOptions(options);
            NativeWebSocketConnection connection = await NativeWebSocketConnection.CreateAsync(
                options
            );
            UnityMicrophoneInput input;
            try
            {
                input = await UnityMicrophoneInput.CreateAsync(connection.InputFormat);
            }
            catch
            {
                // Mic init failed (no permission, no device, …). The
                // connection is already open; close it cleanly so the
                // user's catch site doesn't have to hand-roll cleanup.
                connection.Close();
                throw;
            }
            UnityAudioSourceOutput output;
            try
            {
                output = await UnityAudioSourceOutput.CreateAsync(
                    connection.OutputFormat,
                    audioSource: options.OutputAudioSource
                );
            }
            catch
            {
                // Output init failed — tear down the mic + connection we
                // already opened so a user catch site doesn't leak either.
                await input.Close();
                connection.Close();
                throw;
            }
            return BuildConversation(connection, input, output, options);
        }

        // Post-handshake wiring split out as an internal seam so tests can
        // pair NativeWebSocketConnection.CreateInternalAsync (with a
        // PairedWebSocket fixture) directly against the launcher's
        // Conversation construction — exercising the launcher's wiring
        // shape without dialling a real TCP server, and without needing a
        // real microphone in the test environment.
        internal static Conversation BuildConversation(
            NativeWebSocketConnection connection,
            IInputController input,
            IOutputController output,
            ConversationOptions options
        )
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            if (output == null)
                throw new ArgumentNullException(nameof(output));
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            // The uploader needs the negotiated conversation id, which is only
            // populated after the WebSocket handshake completes — so we wire
            // it up here rather than at connection-construction time.
            var fileUploader = new HttpFileUploader(
                HttpFileUploader.DeriveHttpsOrigin(options),
                connection.ConversationId
            );
            var conversation = new Conversation(connection, input, output, fileUploader, options);
            conversation.UpdateStatus(Status.Connected);
            conversation.RaiseConnected(conversation.ConversationId);
            return conversation;
        }

        // Layered validation: NativeWebSocketConnection.ValidateTransport
        // rejects WebRTC outright (no native LiveKit at v0.1), then the same
        // "exactly one credential" XOR check the Bridged launcher applies
        // catches the rest. ConversationToken with ConnectionType.WebSocket is
        // a user error worth surfacing explicitly — it's only meaningful with
        // the LiveKit transport, which doesn't exist on this path.
        private static void ValidateOptions(ConversationOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            NativeWebSocketConnection.ValidateTransport(options);

            int credentials =
                (string.IsNullOrEmpty(options.AgentId) ? 0 : 1)
                + (string.IsNullOrEmpty(options.SignedUrl) ? 0 : 1)
                + (string.IsNullOrEmpty(options.ConversationToken) ? 0 : 1);
            if (credentials != 1)
            {
                throw new ArgumentException(
                    "Exactly one of AgentId, SignedUrl, or ConversationToken must be set "
                        + $"on ConversationOptions (got {credentials}).",
                    nameof(options)
                );
            }
            if (!string.IsNullOrEmpty(options.ConversationToken))
            {
                throw new ArgumentException(
                    "ConversationToken is only valid with the WebRTC transport, which "
                        + "the native build does not support; use AgentId or SignedUrl for "
                        + "WebSocket sessions.",
                    nameof(options)
                );
            }
        }
    }
}

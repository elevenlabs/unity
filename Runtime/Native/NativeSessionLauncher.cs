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
    /// I/O controllers are <see cref="NullInputController"/> / <see cref="NullOutputController"/>
    /// at v0.1 — the launcher delivers a text-only session against a real
    /// agent. <c>UnityMicrophoneInput</c> (#9c) and <c>UnityAudioSourceOutput</c>
    /// (#9d) replace these in subsequent PRs without touching the launcher's
    /// wiring shape.
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

        // Drives NativeWebSocketConnection.CreateAsync, then hands the
        // resulting connection to BuildConversation to construct the
        // cross-platform Conversation around the (connection, null input,
        // null output) triple and mark it Connected. Status.Connecting is
        // skipped on purpose: callers learn they're connecting from the
        // awaited Awaitable, not from a transitory state on a Conversation
        // reference they don't yet hold. Mirrors BridgedSessionLauncher.StartAsync's
        // shape so behaviour stays uniform across transports.
        internal static async Awaitable<Conversation> StartAsync(ConversationOptions options)
        {
            ValidateOptions(options);
            NativeWebSocketConnection connection = await NativeWebSocketConnection.CreateAsync(
                options
            );
            return BuildConversation(connection, options);
        }

        // Post-handshake wiring split out as an internal seam so tests can
        // pair NativeWebSocketConnection.CreateInternalAsync (with a
        // PairedWebSocket fixture) directly against the launcher's
        // Conversation construction — exercising the launcher's wiring
        // shape without dialling a real TCP server.
        internal static Conversation BuildConversation(
            NativeWebSocketConnection connection,
            ConversationOptions options
        )
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            var input = new NullInputController();
            var output = new NullOutputController();
            var conversation = new Conversation(connection, input, output, options);
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

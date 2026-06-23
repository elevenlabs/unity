#nullable enable

using System;
using ElevenLabs.Agents;
using UnityEngine;

namespace ElevenLabs.WebGL.Bridged
{
    /// <summary>
    /// Platform glue that hands <see cref="Conversation.StartSessionAsync"/>
    /// a WebGL implementation. Registers <see cref="StartAsync"/> as
    /// <see cref="Conversation.SessionFactory"/> so cross-platform Core code
    /// can open a JS-backed session without referencing the WebGL asmdef
    /// directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two registration triggers — one for runtime / Play Mode / WebGL
    /// builds, one for the Editor — so the factory is present in every
    /// context where Conversation could legitimately be reached, including
    /// Edit Mode tests that exercise the static entry point.
    /// </para>
    /// <para>
    /// Phase 7's native asmdef will register its own factory the same way;
    /// last-write-wins is fine because each platform asmdef ships under an
    /// <c>includePlatforms</c> that picks at most one launcher per build.
    /// </para>
    /// </remarks>
    internal static class BridgedSessionLauncher
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RuntimeInit() => Register();

#if UNITY_EDITOR
        // Mirrors RuntimeInit for the Editor / Edit Mode test domain, where
        // RuntimeInitializeOnLoadMethod is not invoked unless Play Mode is
        // entered. UnityEditor.dll is only available when the asmdef compiles
        // for the Editor target — guarded by UNITY_EDITOR so WebGL builds
        // never see this attribute.
        [UnityEditor.InitializeOnLoadMethod]
        private static void EditorInit() => Register();
#endif

        private static void Register()
        {
            // Pull the connection .jslib's `$EL_ConnectionInit` bundle into
            // the build. The bundle self-registers each factory with the
            // primitives dispatcher at runtime, but Emscripten only includes
            // the library entry if a real DllImport pins it — see
            // EL_EnsureConnectionFactoriesLoaded.
#if UNITY_WEBGL && !UNITY_EDITOR
            ElevenLabsBridgeNative.EL_EnsureConnectionFactoriesLoaded();
#endif
            Conversation.SessionFactory = StartAsync;
        }

        // Drives BridgedSession.StartAsync, then constructs the cross-platform
        // Conversation around the (connection, input, output) triple and marks
        // it Connected. Status.Connecting is skipped on purpose: callers learn
        // they're connecting from the awaited Awaitable, not from a transitory
        // state on a Conversation reference they don't yet hold.
        internal static async Awaitable<Conversation> StartAsync(ConversationOptions options)
        {
            ValidateOptions(options);
            BridgedSession session = await BridgedSession.StartAsync(options);
            // Bridged path's IFileUploader lands in a follow-up commit — wire
            // a not-implemented stub for now so the public surface compiles
            // and WebGL game code only sees the failure on actual call.
            var conversation = new Conversation(
                session.Connection,
                session.Input,
                session.Output,
                new NotImplementedFileUploader(),
                options
            );
            conversation.UpdateStatus(Status.Connected);
            conversation.RaiseConnected(conversation.ConversationId);
            return conversation;
        }

        private sealed class NotImplementedFileUploader : IFileUploader
        {
            public Awaitable<string> UploadFileAsync(
                byte[] bytes,
                string mimeType,
                string? filename = null
            )
            {
                throw new NotImplementedException(
                    "UploadFileAsync is not yet implemented on the bridged (WebGL) transport. "
                        + "See Docs~/plans/v0.1-parity.md#11b for tracking."
                );
            }
        }

        // Lives on the launcher rather than on ConversationOptions itself so
        // each transport can layer its own constraints (NativeSessionLauncher
        // in #9 will additionally reject WebRTC entirely). Mirrors the JS
        // SDK's PublicSessionConfig / PrivateWebSocketSessionConfig /
        // PrivateWebRTCSessionConfig union: exactly one credential field is
        // set, and ConversationToken is only valid on the WebRTC transport.
        private static void ValidateOptions(ConversationOptions options)
        {
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
            if (options.ConnectionType == ConnectionType.WebRTC)
            {
                if (string.IsNullOrEmpty(options.ConversationToken))
                    throw new ArgumentException(
                        "WebRTC transport requires ConversationToken.",
                        nameof(options)
                    );
            }
            else if (!string.IsNullOrEmpty(options.ConversationToken))
            {
                throw new ArgumentException(
                    "ConversationToken is only valid with the WebRTC transport; "
                        + "use AgentId or SignedUrl for WebSocket sessions.",
                    nameof(options)
                );
            }
        }
    }
}

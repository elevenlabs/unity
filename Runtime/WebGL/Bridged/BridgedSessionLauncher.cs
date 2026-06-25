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
    /// The <c>#if UNITY_WEBGL</c> guard around <see cref="Register"/> is
    /// load-bearing and mirrors <c>NativeSessionLauncher</c>'s
    /// <c>#if !UNITY_WEBGL</c>: the WebGL asmdef ships with
    /// <c>includePlatforms: ["Editor", "WebGL"]</c> so this launcher and
    /// <c>NativeSessionLauncher</c> are both loaded in the Editor when the
    /// active build target is non-WebGL. Without the guard, both would race
    /// to assign <see cref="Conversation.SessionFactory"/> and the
    /// last-write-wins outcome is non-deterministic — leaving Editor Play
    /// Mode on a Standalone target with the WebGL factory and the
    /// "WebGL bridge is not available outside WebGL builds" runtime error.
    /// The guard makes sure only the platform-appropriate launcher
    /// registers.
    /// </para>
    /// </remarks>
    internal static class BridgedSessionLauncher
    {
#if UNITY_WEBGL
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RuntimeInit() => Register();
#endif

#if UNITY_EDITOR && UNITY_WEBGL
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

        // Test seam: lets the Editor Play Mode rejection be exercised from Edit
        // Mode tests without actually entering Play Mode. Default reads
        // Application.isPlaying at call time on Editor builds and is hardcoded
        // false in built players, so production behaviour is unchanged.
        internal static Func<bool> EditorPlayModeProbe { get; set; } =
            static () =>
#if UNITY_EDITOR
                UnityEngine.Application.isPlaying;
#else
                false;
#endif

        internal const string EditorPlayModeRejectionMessage =
            "ElevenLabs Agents cannot run in the Unity Editor when WebGL is the active build target — "
            + "the WebGL bridge only resolves inside a built WebGL output (browser). To test in the "
            + "Editor, switch the active build target to Mac / Windows / Linux via Build Profiles; the "
            + "native transport runs in Play Mode and supports the same Conversation API. Re-select "
            + "WebGL when you're ready to build for the browser.";

        // Drives BridgedSession.StartAsync, then constructs the cross-platform
        // Conversation around the (connection, input, output) triple and marks
        // it Connected. Status.Connecting is skipped on purpose: callers learn
        // they're connecting from the awaited Awaitable, not from a transitory
        // state on a Conversation reference they don't yet hold.
        internal static async Awaitable<Conversation> StartAsync(ConversationOptions options)
        {
            // Editor Play Mode under a WebGL active target reaches this factory
            // (the Native asmdef is excluded from WebGL targets, so only the
            // bridged launcher registers), but the JS bridge's `__Internal`
            // DllImports never resolve in the Editor process — the call would
            // otherwise surface as the cryptic "WebGL bridge is not available
            // outside WebGL builds" from the synthetic Native stub. Reject up
            // front with an actionable hint about switching active build target.
            if (EditorPlayModeProbe())
            {
                throw new InvalidOperationException(EditorPlayModeRejectionMessage);
            }
            ValidateOptions(options);
            BridgedSession session = await BridgedSession.StartAsync(options);
            // HttpFileUploader is platform-neutral: on WebGL it runs through
            // UnityWebRequest → XMLHttpRequest, so the same code path that
            // serves the native launcher works here too. No jslib primitive
            // needed unless the server's CORS posture rejects the browser
            // request — at which point the failure surfaces from the same
            // UnityWebRequest path the user can inspect.
            var fileUploader = new HttpFileUploader(
                HttpFileUploader.DeriveHttpsOrigin(options),
                session.Connection.ConversationId
            );
            var conversation = new Conversation(
                session.Connection,
                session.Input,
                session.Output,
                fileUploader,
                options
            );
            conversation.UpdateStatus(Status.Connected);
            conversation.RaiseConnected(conversation.ConversationId);
            return conversation;
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

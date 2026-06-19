#nullable enable

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
            Conversation.SessionFactory = StartAsync;
        }

        // Drives BridgedSession.StartAsync, then constructs the cross-platform
        // Conversation around the (connection, input, output) triple and marks
        // it Connected. Status.Connecting is skipped on purpose: callers learn
        // they're connecting from the awaited Awaitable, not from a transitory
        // state on a Conversation reference they don't yet hold.
        internal static async Awaitable<Conversation> StartAsync(ConversationOptions options)
        {
            BridgedSession session = await BridgedSession.StartAsync(options);
            var conversation = new Conversation(
                session.Connection,
                session.Input,
                session.Output,
                options
            );
            conversation.UpdateStatus(Status.Connected);
            conversation.RaiseConnected(conversation.ConversationId);
            return conversation;
        }
    }
}

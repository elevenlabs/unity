#nullable enable

using ElevenLabs.Agents;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Smoke coverage for <see cref="BridgedSessionLauncher"/>. Verifies the
    /// <c>InitializeOnLoadMethod</c> hook (Editor) wired
    /// <see cref="Conversation.SessionFactory"/> at script load, and that the
    /// launcher routes through <see cref="BridgedSession"/> end-to-end so the
    /// non-WebGL bridge surfaces a <see cref="BridgeException"/> through
    /// <see cref="Conversation.StartSessionAsync"/>.
    /// </summary>
    public class BridgedSessionLauncherTests
    {
        [TearDown]
        public void TearDown()
        {
            // StartAsync allocates a PromiseRegistry entry + a BridgeCallback
            // registration per factory call before the DllImport throws on
            // non-WebGL; reset so partial registrations don't bleed across
            // tests.
            PromiseRegistry.ResetForTests();
            CallbackRegistry.ResetForTests();
        }

        [Test]
        public void SessionFactory_IsRegisteredAtEditorLoad()
        {
            // [InitializeOnLoadMethod] runs at every script reload — by the
            // time Edit Mode tests execute, the launcher has already pointed
            // SessionFactory at BridgedSession.StartAsync. If this assertion
            // fails, the Editor hook is missing or the gating ifdefs have
            // drifted.
            Assert.IsNotNull(Conversation.SessionFactory);
        }

        [Test]
        public void StartSessionAsync_WebSocket_RoutesThroughLauncher_ToBridgeException()
        {
            // Conversation → launcher → BridgedSession → first DllImport
            // throws PlatformNotSupportedException → PromiseRegistry settles
            // as BridgeException. Same shape BridgedSessionSmokeTests uses;
            // duplicated here so the failure mode points at the launcher
            // wiring rather than the orchestrator alone.
            var task = Conversation.StartSessionAsync(
                new ConversationOptions
                {
                    AgentId = "agent-test",
                    ConnectionType = ConnectionType.WebSocket,
                }
            );
            Assert.Throws<BridgeException>(() => task.GetAwaiter().GetResult());
        }

        [Test]
        public void StartSessionAsync_WebRtc_RoutesThroughLauncher_ToBridgeException()
        {
            var task = Conversation.StartSessionAsync(
                new ConversationOptions
                {
                    ConversationToken = "token-test",
                    ConnectionType = ConnectionType.WebRTC,
                }
            );
            Assert.Throws<BridgeException>(() => task.GetAwaiter().GetResult());
        }
    }
}

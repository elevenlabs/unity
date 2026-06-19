#nullable enable

using System;
using ElevenLabs.Agents;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Smoke coverage for <see cref="BridgedSession"/>'s static entry point.
    /// Edit Mode runs without the WebGL DllImports, so the first factory call
    /// inside <see cref="BridgedSession.StartAsync"/> hits
    /// <see cref="PlatformNotSupportedException"/>; that's enough to confirm
    /// the orchestrator dispatches to the right arm and starts the bridge
    /// handshake. Full stub-driven coverage lands with Phase 5.5.
    /// </summary>
    public class BridgedSessionSmokeTests
    {
        [TearDown]
        public void TearDown()
        {
            // StartAsync allocates a PromiseRegistry entry per factory call
            // before the DllImport throws; reset so partial registrations
            // don't bleed into sibling tests.
            CallbackRegistry.ResetForTests();
            PromiseRegistry.ResetForTests();
        }

        [Test]
        public void StartAsync_NullOptions_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() => BridgedSession.StartAsync(null!));
        }

        [Test]
        public void StartAsync_WebSocket_OnNonWebGL_PropagatesBridgeException()
        {
            var options = new ConversationOptions
            {
                AgentId = "agent-test",
                ConnectionType = ConnectionType.WebSocket,
            };
            var task = BridgedSession.StartAsync(options);
            // PromiseRegistry settles the awaitable with the DllImport's
            // PlatformNotSupportedException.Message via BridgeException so the
            // caller observes a bridge-layer failure rather than a dangling
            // promise. If this assertion ever changes shape, the orchestrator
            // is no longer routing through the bridge at all.
            Assert.Throws<BridgeException>(() => task.GetAwaiter().GetResult());
        }

        [Test]
        public void StartAsync_WebRtc_OnNonWebGL_PropagatesBridgeException()
        {
            var options = new ConversationOptions
            {
                ConversationToken = "token-test",
                ConnectionType = ConnectionType.WebRTC,
            };
            var task = BridgedSession.StartAsync(options);
            Assert.Throws<BridgeException>(() => task.GetAwaiter().GetResult());
        }
    }
}

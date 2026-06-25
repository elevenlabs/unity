#nullable enable

using System;
using ElevenLabs.Agents;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;
using UnityEngine;

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
        // After NativeSessionLauncher landed (#9b), the Editor with a non-WebGL
        // active build target loads both launchers and the last one to run its
        // [InitializeOnLoadMethod] wins SessionFactory. Pin the bridged factory
        // for the duration of every test in this class so we're asserting on the
        // launcher under test, not on whichever happened to register last.
        private Func<ConversationOptions, Awaitable<Conversation>>? _previousFactory;

        [SetUp]
        public void SetUp()
        {
            _previousFactory = Conversation.SessionFactory;
            Conversation.SessionFactory = BridgedSessionLauncher.StartAsync;
        }

        [TearDown]
        public void TearDown()
        {
            // StartAsync allocates a PromiseRegistry entry + a BridgeCallback
            // registration per factory call before the DllImport throws on
            // non-WebGL; reset so partial registrations don't bleed across
            // tests.
            PromiseRegistry.ResetForTests();
            CallbackRegistry.ResetForTests();
            Conversation.SessionFactory = _previousFactory;
        }

        [Test]
        public void SessionFactory_IsRegisteredAtEditorLoad()
        {
            // [InitializeOnLoadMethod] runs at every script reload — by the
            // time Edit Mode tests execute, *some* launcher has pointed
            // SessionFactory at a real factory (Native or Bridged depending on
            // domain-load order). If this assertion fails, neither launcher's
            // Editor hook fired.
            Assert.IsNotNull(_previousFactory);
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

        [Test]
        public void StartSessionAsync_RejectsZeroCredentials()
        {
            // No AgentId / SignedUrl / ConversationToken set — JS SDK's union
            // types would reject this anyway, but surfacing it at the C#
            // boundary keeps the error close to the call site.
            var task = Conversation.StartSessionAsync(
                new ConversationOptions { ConnectionType = ConnectionType.WebSocket }
            );
            var ex = Assert.Throws<ArgumentException>(() => task.GetAwaiter().GetResult());
            StringAssert.Contains("Exactly one of", ex!.Message);
        }

        [Test]
        public void StartSessionAsync_RejectsConflictingCredentials()
        {
            var task = Conversation.StartSessionAsync(
                new ConversationOptions
                {
                    AgentId = "agent-test",
                    SignedUrl = "wss://example/signed",
                }
            );
            var ex = Assert.Throws<ArgumentException>(() => task.GetAwaiter().GetResult());
            StringAssert.Contains("Exactly one of", ex!.Message);
        }

        [Test]
        public void StartSessionAsync_RejectsConversationToken_OnWebSocketTransport()
        {
            // ConversationToken is the WebRTC credential; passing it on a
            // WebSocket session is the JS SDK's PrivateWebRTCSessionConfig
            // mismatch, but caught here before reaching the bridge.
            var task = Conversation.StartSessionAsync(
                new ConversationOptions
                {
                    ConversationToken = "token-test",
                    ConnectionType = ConnectionType.WebSocket,
                }
            );
            var ex = Assert.Throws<ArgumentException>(() => task.GetAwaiter().GetResult());
            StringAssert.Contains("WebRTC", ex!.Message);
        }

        [Test]
        public void StartSessionAsync_RejectsWebRtc_WithoutConversationToken()
        {
            var task = Conversation.StartSessionAsync(
                new ConversationOptions
                {
                    AgentId = "agent-test",
                    ConnectionType = ConnectionType.WebRTC,
                }
            );
            var ex = Assert.Throws<ArgumentException>(() => task.GetAwaiter().GetResult());
            StringAssert.Contains("WebRTC transport requires ConversationToken", ex!.Message);
        }

        [Test]
        public void StartSessionAsync_RejectsEditorPlayMode_WithActionableMessage()
        {
            // Editor Play Mode under WebGL active target would otherwise reach
            // the `__Internal` DllImport stub and surface a cryptic
            // "WebGL bridge is not available outside WebGL builds". The
            // launcher catches this up front. Edit Mode tests can't enter Play
            // Mode without major scaffolding, so the launcher exposes
            // EditorPlayModeProbe as a swappable predicate that defaults to
            // Application.isPlaying; this test flips it to true and asserts the
            // actionable error surfaces in place of the bridge propagation.
            var previousProbe = BridgedSessionLauncher.EditorPlayModeProbe;
            BridgedSessionLauncher.EditorPlayModeProbe = () => true;
            try
            {
                var task = Conversation.StartSessionAsync(
                    new ConversationOptions
                    {
                        AgentId = "agent-test",
                        ConnectionType = ConnectionType.WebSocket,
                    }
                );
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    task.GetAwaiter().GetResult()
                );
                StringAssert.Contains("Editor", ex!.Message);
                StringAssert.Contains("Build Profiles", ex.Message);
                StringAssert.Contains("native transport", ex.Message);
            }
            finally
            {
                BridgedSessionLauncher.EditorPlayModeProbe = previousProbe;
            }
        }
    }
}

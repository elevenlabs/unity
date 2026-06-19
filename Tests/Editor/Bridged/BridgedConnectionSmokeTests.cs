#nullable enable

using System;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Smoke coverage for the bridged class constructors + null-arg guards.
    /// Edit Mode runs without the WebGL DllImports, so the constructors that
    /// reach JS via <see cref="JsObject"/>.<see cref="JsObject.Call"/> all
    /// raise <see cref="PlatformNotSupportedException"/>; that's enough to
    /// confirm the wire-up sequence actually attempts the JS-side
    /// subscription. Full router-level coverage with a stubbable
    /// <see cref="JsObject"/> lands with Phase 5.5.
    /// </summary>
    public class BridgedConnectionSmokeTests
    {
        [TearDown]
        public void TearDown()
        {
            // The connection constructors call BridgeCallback.Wrap three times
            // before the first JS subscription throws — the wrappers register
            // in the C# callback registry whether or not the JS Call goes
            // through. Reset so the partial registrations don't bleed into
            // sibling tests.
            CallbackRegistry.ResetForTests();
            PromiseRegistry.ResetForTests();
        }

        // BridgedWebSocketConnection -----------------------------------------

        [Test]
        public void BridgedWebSocketConnection_NullJsObject_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() => new BridgedWebSocketConnection(null!));
        }

        [Test]
        public void BridgedWebSocketConnection_OnNonWebGL_CtorThrowsPlatformNotSupported()
        {
            // The constructor's first JS subscription Call triggers the
            // DllImport which throws on non-WebGL. If this assertion ever
            // changes shape, the wire-up sequence has changed and 5.5 mocks
            // need to follow.
            Assert.Throws<PlatformNotSupportedException>(() =>
                new BridgedWebSocketConnection(new JsObject(1))
            );
        }

        // BridgedWebRTCConnection --------------------------------------------

        [Test]
        public void BridgedWebRTCConnection_NullJsObject_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() => new BridgedWebRTCConnection(null!));
        }

        [Test]
        public void BridgedWebRTCConnection_OnNonWebGL_CtorThrowsPlatformNotSupported()
        {
            Assert.Throws<PlatformNotSupportedException>(() =>
                new BridgedWebRTCConnection(new JsObject(1))
            );
        }

        // BridgedInputController ---------------------------------------------

        [Test]
        public void BridgedInputController_NullJsObject_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() => new BridgedInputController(null!));
        }

        [Test]
        public void BridgedInputController_Ctor_DoesNotThrowOnValidJsObject()
        {
            // The input controller doesn't subscribe at construction, so the
            // ctor stays clean of JS calls. Method calls (Close / SetDevice /
            // …) hit the bridge and throw on non-WebGL.
            Assert.DoesNotThrow(() =>
            {
                var ctrl = new BridgedInputController(new JsObject(1));
                _ = ctrl; // suppress unused warning
            });
        }

        // BridgedOutputController --------------------------------------------

        [Test]
        public void BridgedOutputController_NullJsObject_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() => new BridgedOutputController(null!));
        }

        [Test]
        public void BridgedOutputController_Ctor_DoesNotThrowOnValidJsObject()
        {
            Assert.DoesNotThrow(() =>
            {
                var ctrl = new BridgedOutputController(new JsObject(1));
                _ = ctrl;
            });
        }
    }
}

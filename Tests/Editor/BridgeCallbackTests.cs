using System;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Tests
{
    public class BridgeCallbackTests
    {
        [TearDown]
        public void TearDown()
        {
            CallbackRegistry.ResetForTests();
        }

        [Test]
        public void Wrap_RegistersHandlerInRegistry()
        {
            using var cb = BridgeCallback.Wrap(_ => { });
            Assert.AreEqual(1, CallbackRegistry.Count);
        }

        [Test]
        public void Handle_IsPositiveAndUnique()
        {
            using var a = BridgeCallback.Wrap(_ => { });
            using var b = BridgeCallback.Wrap(_ => { });
            Assert.Greater(a.Handle, 0);
            Assert.Greater(b.Handle, 0);
            Assert.AreNotEqual(a.Handle, b.Handle);
        }

        [Test]
        public void Dispose_RemovesHandlerFromRegistry()
        {
            var cb = BridgeCallback.Wrap(_ => { });
            cb.Dispose();
            Assert.AreEqual(0, CallbackRegistry.Count);
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            var cb = BridgeCallback.Wrap(_ => { });
            cb.Dispose();
            Assert.DoesNotThrow(() => cb.Dispose());
            Assert.AreEqual(0, CallbackRegistry.Count);
        }

        [Test]
        public void Invoke_DispatchesThroughCallbackRegistry()
        {
            string captured = null;
            using var cb = BridgeCallback.Wrap(payload => captured = payload);

            CallbackRegistry.TryDispatch(cb.Handle, "hello");

            Assert.AreEqual("hello", captured);
        }

        [Test]
        public void Invoke_IsMultiShot()
        {
            int count = 0;
            using var cb = BridgeCallback.Wrap(_ => count++);

            CallbackRegistry.TryDispatch(cb.Handle, "one");
            CallbackRegistry.TryDispatch(cb.Handle, "two");

            Assert.AreEqual(2, count);
        }

        [Test]
        public void Invoke_AfterDispose_NoOpsSilently()
        {
            int count = 0;
            var cb = BridgeCallback.Wrap(_ => count++);
            cb.Dispose();

            bool dispatched = CallbackRegistry.TryDispatch(cb.Handle, "late");

            Assert.IsFalse(dispatched);
            Assert.AreEqual(0, count);
        }

        [Test]
        public void WrapNull_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => BridgeCallback.Wrap((Action<string>)null));
        }

        [Test]
        public void WrapTyped_DeserializesJsonPayloadBeforeInvokingDelegate()
        {
            int captured = 0;
            using var cb = BridgeCallback.Wrap<int>(value => captured = value);

            CallbackRegistry.TryDispatch(cb.Handle, "42");

            Assert.AreEqual(42, captured);
        }

        [Test]
        public void WrapTypedNull_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => BridgeCallback.Wrap<int>(null));
        }
    }
}

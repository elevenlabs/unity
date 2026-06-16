using System.Collections.Generic;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Tests.Internal
{
    public class CallbackRegistryTests
    {
        [TearDown]
        public void TearDown()
        {
            CallbackRegistry.ResetForTests();
        }

        [Test]
        public void Register_ReturnsUniqueHandles()
        {
            int firstHandle = CallbackRegistry.Register(_ => { });
            int secondHandle = CallbackRegistry.Register(_ => { });

            Assert.AreNotEqual(firstHandle, secondHandle);
            Assert.AreEqual(2, CallbackRegistry.Count);
        }

        [Test]
        public void TryDispatch_InvokesRegisteredHandlerWithPayload()
        {
            string captured = null;
            int handle = CallbackRegistry.Register(payload => captured = payload);

            bool dispatched = CallbackRegistry.TryDispatch(handle, "ping");

            Assert.IsTrue(dispatched);
            Assert.AreEqual("ping", captured);
        }

        [Test]
        public void TryDispatch_IsMultiShot()
        {
            var seen = new List<string>();
            int handle = CallbackRegistry.Register(seen.Add);

            Assert.IsTrue(CallbackRegistry.TryDispatch(handle, "one"));
            Assert.IsTrue(CallbackRegistry.TryDispatch(handle, "two"));
            Assert.IsTrue(CallbackRegistry.TryDispatch(handle, "three"));

            Assert.AreEqual(new[] { "one", "two", "three" }, seen);
            Assert.AreEqual(1, CallbackRegistry.Count);
        }

        [Test]
        public void TryDispatch_UnknownHandleReturnsFalse()
        {
            bool dispatched = CallbackRegistry.TryDispatch(int.MaxValue, "ignored");

            Assert.IsFalse(dispatched);
        }

        [Test]
        public void TryRemove_RemovesEntryAndReturnsTrue()
        {
            int handle = CallbackRegistry.Register(_ => { });

            Assert.IsTrue(CallbackRegistry.TryRemove(handle));
            Assert.AreEqual(0, CallbackRegistry.Count);
        }

        [Test]
        public void TryRemove_DoubleRemoveIsIdempotent()
        {
            int handle = CallbackRegistry.Register(_ => { });

            Assert.IsTrue(CallbackRegistry.TryRemove(handle));
            Assert.IsFalse(CallbackRegistry.TryRemove(handle));
        }

        [Test]
        public void TryDispatch_AfterRemoveNoOpsSilently()
        {
            int callCount = 0;
            int handle = CallbackRegistry.Register(_ => callCount++);

            Assert.IsTrue(CallbackRegistry.TryRemove(handle));
            bool dispatched = CallbackRegistry.TryDispatch(handle, "ignored");

            Assert.IsFalse(dispatched);
            Assert.AreEqual(0, callCount);
        }

        [Test]
        public void TryRemove_DuringDispatchHandlerStillCompletes()
        {
            // Re-entrant scenario: the handler itself removes its own registration
            // mid-dispatch. The handler must still complete (already pulled from the
            // dictionary under the lookup lock); the subsequent remove returns false
            // because the lookup-then-invoke pair already drained nothing extra —
            // we explicitly want multi-shot dispatch to *not* tombstone the entry,
            // so the handler-side remove is the authoritative teardown.
            int handle = 0;
            int callCount = 0;
            handle = CallbackRegistry.Register(_ =>
            {
                callCount++;
                CallbackRegistry.TryRemove(handle);
            });

            Assert.IsTrue(CallbackRegistry.TryDispatch(handle, "first"));
            // The handler removed itself — second dispatch misses.
            Assert.IsFalse(CallbackRegistry.TryDispatch(handle, "second"));
            Assert.AreEqual(1, callCount);
        }
    }
}

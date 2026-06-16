using ElevenLabs.WebGL.Internal;
using NUnit.Framework;
using UnityEngine;

namespace ElevenLabs.WebGL.Tests.Internal
{
    public class PromiseRegistryTests
    {
        [TearDown]
        public void TearDown()
        {
            PromiseRegistry.ResetForTests();
        }

        [Test]
        public void Register_ReturnsUniqueIds()
        {
            int firstId = PromiseRegistry.Register(new AwaitableCompletionSource<string>());
            int secondId = PromiseRegistry.Register(new AwaitableCompletionSource<string>());

            Assert.AreNotEqual(firstId, secondId);
            Assert.AreEqual(2, PromiseRegistry.Count);
        }

        [Test]
        public void TrySettle_OkResolvesAwaitableAndRemovesEntry()
        {
            var source = new AwaitableCompletionSource<string>();
            int id = PromiseRegistry.Register(source);

            bool settled = PromiseRegistry.TrySettle(id, ok: true, payload: "hello");

            Assert.IsTrue(settled);
            Assert.AreEqual(0, PromiseRegistry.Count);

            var awaiter = source.Awaitable.GetAwaiter();
            Assert.IsTrue(awaiter.IsCompleted);
            Assert.AreEqual("hello", awaiter.GetResult());
        }

        [Test]
        public void TrySettle_ErrFaultsAwaitableWithBridgeException()
        {
            var source = new AwaitableCompletionSource<string>();
            int id = PromiseRegistry.Register(source);

            bool settled = PromiseRegistry.TrySettle(id, ok: false, payload: "boom");

            Assert.IsTrue(settled);

            var awaiter = source.Awaitable.GetAwaiter();
            Assert.IsTrue(awaiter.IsCompleted);
            var ex = Assert.Throws<BridgeException>(() => awaiter.GetResult());
            Assert.AreEqual("boom", ex.Message);
        }

        [Test]
        public void TrySettle_UnknownIdReturnsFalse()
        {
            // Use a deliberately large ID that BridgeIdGenerator is extremely unlikely
            // to reach during the test run (it allocates monotonically from 1).
            bool settled = PromiseRegistry.TrySettle(int.MaxValue, ok: true, payload: "");

            Assert.IsFalse(settled);
        }

        [Test]
        public void TrySettle_TwiceFirstWinsSecondReturnsFalse()
        {
            var source = new AwaitableCompletionSource<string>();
            int id = PromiseRegistry.Register(source);

            bool first = PromiseRegistry.TrySettle(id, ok: true, payload: "first");
            bool second = PromiseRegistry.TrySettle(id, ok: true, payload: "second");

            Assert.IsTrue(first);
            Assert.IsFalse(second);

            // The first settle won — Awaitable holds "first", not "second".
            Assert.AreEqual("first", source.Awaitable.GetAwaiter().GetResult());
        }

        [Test]
        public void TrySettle_NullPayloadCoercesToEmptyMessageOnError()
        {
            var source = new AwaitableCompletionSource<string>();
            int id = PromiseRegistry.Register(source);

            Assert.IsTrue(PromiseRegistry.TrySettle(id, ok: false, payload: null));

            var ex = Assert.Throws<BridgeException>(() =>
                source.Awaitable.GetAwaiter().GetResult()
            );
            Assert.AreEqual(string.Empty, ex.Message);
        }
    }
}

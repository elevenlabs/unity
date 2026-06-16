using System;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Tests
{
    public class JsBridgeTests
    {
        [TearDown]
        public void TearDown()
        {
            PromiseRegistry.ResetForTests();
        }

        [Test]
        public void InvokeFactory_OnNonWebGL_ThrowsPlatformNotSupportedException()
        {
            Assert.Throws<PlatformNotSupportedException>(() =>
                JsBridge.InvokeFactory<int>("mathFactory")
            );
        }

        [Test]
        public void InvokeFactoryAsync_OnNonWebGL_DoesNotThrowSynchronously()
        {
            // The async method catches the DllImport throw internally and settles the
            // promise, so no exception propagates to the caller synchronously.
            Assert.DoesNotThrow(() =>
            {
                _ = JsBridge.InvokeFactoryAsync<int>("mathFactory");
            });
        }

        [Test]
        public void InvokeFactoryAsync_OnNonWebGL_CleansUpPromiseRegistry()
        {
            _ = JsBridge.InvokeFactoryAsync<int>("mathFactory");
            // The DllImport stub throws PlatformNotSupportedException; the async
            // method catches it and calls TrySettle before reaching the await, which
            // atomically removes the entry from the registry.
            Assert.AreEqual(0, PromiseRegistry.Count);
        }

        [Test]
        public void DecodeSyncResult_NullPtr_ReturnsDefaultForValueType()
        {
            Assert.AreEqual(0, JsBridge.DecodeSyncResult<int>(IntPtr.Zero));
        }

        [Test]
        public void DecodeSyncResult_NullPtr_ReturnsNullForReferenceType()
        {
            Assert.IsNull(JsBridge.DecodeSyncResult<string>(IntPtr.Zero));
        }
    }
}

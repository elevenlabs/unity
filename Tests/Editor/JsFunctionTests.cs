using System;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Tests
{
    public class JsFunctionTests
    {
        [TearDown]
        public void TearDown() => PromiseRegistry.ResetForTests();

        [Test]
        public void Handle_ReflectsConstructorArgument()
        {
            var fn = new JsFunction(42);
            Assert.AreEqual(42, fn.Handle);
        }

        [Test]
        public void Call_OnNonWebGL_ThrowsPlatformNotSupportedException()
        {
            var fn = new JsFunction(1);
            Assert.Throws<PlatformNotSupportedException>(() => fn.Call<int>(1, 2));
        }

        [Test]
        public void CallVoid_OnNonWebGL_ThrowsPlatformNotSupportedException()
        {
            var fn = new JsFunction(1);
            Assert.Throws<PlatformNotSupportedException>(() => fn.Call());
        }

        [Test]
        public void CallAsync_OnNonWebGL_DoesNotThrowSynchronously()
        {
            var fn = new JsFunction(1);
            Assert.DoesNotThrow(() =>
            {
                _ = fn.CallAsync<int>(1, 2);
            });
        }

        [Test]
        public void CallAsync_OnNonWebGL_CleansUpPromiseRegistry()
        {
            var fn = new JsFunction(1);
            _ = fn.CallAsync<int>(1, 2);
            Assert.AreEqual(0, PromiseRegistry.Count);
        }

        [Test]
        public void CallAsyncVoid_OnNonWebGL_DoesNotThrowSynchronously()
        {
            var fn = new JsFunction(1);
            Assert.DoesNotThrow(() =>
            {
                _ = fn.CallAsync();
            });
        }

        [Test]
        public void CallAsyncVoid_OnNonWebGL_CleansUpPromiseRegistry()
        {
            var fn = new JsFunction(1);
            _ = fn.CallAsync();
            Assert.AreEqual(0, PromiseRegistry.Count);
        }

        [Test]
        public void Dispose_DoesNotThrow()
        {
            var fn = new JsFunction(1);
            Assert.DoesNotThrow(() => fn.Dispose());
        }

        [Test]
        public void Dispose_CalledTwice_IsIdempotent()
        {
            var fn = new JsFunction(1);
            fn.Dispose();
            Assert.DoesNotThrow(() => fn.Dispose());
        }
    }
}

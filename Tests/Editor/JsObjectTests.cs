using System;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Tests
{
    public class JsObjectTests
    {
        [TearDown]
        public void TearDown() => PromiseRegistry.ResetForTests();

        [Test]
        public void Handle_ReflectsConstructorArgument()
        {
            var obj = new JsObject(42);
            Assert.AreEqual(42, obj.Handle);
        }

        [Test]
        public void Call_OnNonWebGL_ThrowsPlatformNotSupportedException()
        {
            var obj = new JsObject(1);
            Assert.Throws<PlatformNotSupportedException>(() => obj.Call<int>("add", 1, 2));
        }

        [Test]
        public void CallVoid_OnNonWebGL_ThrowsPlatformNotSupportedException()
        {
            var obj = new JsObject(1);
            Assert.Throws<PlatformNotSupportedException>(() => obj.Call("doSomething"));
        }

        [Test]
        public void CallAsync_OnNonWebGL_DoesNotThrowSynchronously()
        {
            var obj = new JsObject(1);
            Assert.DoesNotThrow(() =>
            {
                _ = obj.CallAsync<int>("add", 1, 2);
            });
        }

        [Test]
        public void CallAsync_OnNonWebGL_CleansUpPromiseRegistry()
        {
            var obj = new JsObject(1);
            _ = obj.CallAsync<int>("add", 1, 2);
            Assert.AreEqual(0, PromiseRegistry.Count);
        }

        [Test]
        public void CallAsyncVoid_OnNonWebGL_DoesNotThrowSynchronously()
        {
            var obj = new JsObject(1);
            Assert.DoesNotThrow(() =>
            {
                _ = obj.CallAsync("doSomething");
            });
        }

        [Test]
        public void CallAsyncVoid_OnNonWebGL_CleansUpPromiseRegistry()
        {
            var obj = new JsObject(1);
            _ = obj.CallAsync("doSomething");
            Assert.AreEqual(0, PromiseRegistry.Count);
        }

        [Test]
        public void Get_OnNonWebGL_ThrowsPlatformNotSupportedException()
        {
            var obj = new JsObject(1);
            Assert.Throws<PlatformNotSupportedException>(() => obj.Get<double>("pi"));
        }

        [Test]
        public void Dispose_DoesNotThrow()
        {
            var obj = new JsObject(1);
            Assert.DoesNotThrow(() => obj.Dispose());
        }

        [Test]
        public void Dispose_CalledTwice_IsIdempotent()
        {
            var obj = new JsObject(1);
            obj.Dispose();
            Assert.DoesNotThrow(() => obj.Dispose());
        }
    }
}

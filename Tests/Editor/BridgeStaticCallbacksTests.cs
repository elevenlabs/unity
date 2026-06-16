using System;
using System.Runtime.InteropServices;
using System.Text;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;
using UnityEngine;

namespace ElevenLabs.WebGL.Tests
{
    public class BridgeStaticCallbacksTests
    {
        [TearDown]
        public void TearDown()
        {
            PromiseRegistry.ResetForTests();
        }

        [Test]
        public void OnSettleFromJs_StatusCodeZero_ResolvesPromiseWithPayload()
        {
            var source = new AwaitableCompletionSource<string>();
            int id = PromiseRegistry.Register(source);
            IntPtr ptr = AllocUtf8("hello");
            try
            {
                BridgeStaticCallbacks.OnSettleFromJs(id, statusCode: 0, ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            var awaiter = source.Awaitable.GetAwaiter();
            Assert.IsTrue(awaiter.IsCompleted);
            Assert.AreEqual("hello", awaiter.GetResult());
        }

        [Test]
        public void OnSettleFromJs_StatusCodeOne_FaultsPromiseWithBridgeException()
        {
            var source = new AwaitableCompletionSource<string>();
            int id = PromiseRegistry.Register(source);
            IntPtr ptr = AllocUtf8("boom");
            try
            {
                BridgeStaticCallbacks.OnSettleFromJs(id, statusCode: 1, ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            var awaiter = source.Awaitable.GetAwaiter();
            Assert.IsTrue(awaiter.IsCompleted);
            var ex = Assert.Throws<BridgeException>(() => awaiter.GetResult());
            Assert.AreEqual("boom", ex.Message);
        }

        [Test]
        public void OnSettleFromJs_NullPtr_TreatsPayloadAsNull()
        {
            var source = new AwaitableCompletionSource<string>();
            int id = PromiseRegistry.Register(source);

            BridgeStaticCallbacks.OnSettleFromJs(id, statusCode: 1, IntPtr.Zero);

            var ex = Assert.Throws<BridgeException>(() =>
                source.Awaitable.GetAwaiter().GetResult()
            );
            Assert.AreEqual(string.Empty, ex.Message);
        }

        [Test]
        public void OnSettleFromJs_UnknownPromiseId_NoOps()
        {
            // Use a deliberately large ID that BridgeIdGenerator is extremely unlikely
            // to reach during the test run (it allocates monotonically from 1).
            Assert.DoesNotThrow(() =>
                BridgeStaticCallbacks.OnSettleFromJs(int.MaxValue, statusCode: 0, IntPtr.Zero)
            );
        }

        [Test]
        public void OnSettleFromJs_FirstWins_SecondNoOps()
        {
            var source = new AwaitableCompletionSource<string>();
            int id = PromiseRegistry.Register(source);
            IntPtr ptr = AllocUtf8("first");
            try
            {
                BridgeStaticCallbacks.OnSettleFromJs(id, statusCode: 0, ptr);
                // Second settle on the same ID must not throw and must not change the result.
                BridgeStaticCallbacks.OnSettleFromJs(id, statusCode: 1, ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            Assert.AreEqual("first", source.Awaitable.GetAwaiter().GetResult());
        }

        private static IntPtr AllocUtf8(string str)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(str);
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0);
            return ptr;
        }
    }
}

#nullable enable

using System;
using System.Linq;
using ElevenLabs.Agents;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Router-level tests for <see cref="BridgedInputController"/>. Drives the
    /// wrapper through a stub <see cref="IJsObject"/> so each public method's
    /// JS-side method-name + arg shape is asserted in Edit Mode.
    /// </summary>
    public class BridgedInputControllerTests
    {
        [TearDown]
        public void TearDown()
        {
            CallbackRegistry.ResetForTests();
            PromiseRegistry.ResetForTests();
        }

        [Test]
        public void Ctor_NullJsObject_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() => new BridgedInputController(null!));
        }

        [Test]
        public void IsMuted_ReadsFromJsProperty()
        {
            var fake = new FakeJsObject();
            fake.PropertyValues["isMuted"] = true;
            var ctrl = new BridgedInputController(fake);

            Assert.IsTrue(ctrl.IsMuted);

            var get = fake.Invocations.FirstOrDefault(i =>
                i.Kind == InvocationKind.Get && i.Name == "isMuted"
            );
            Assert.IsNotNull(get);
            Assert.AreEqual(typeof(bool), get!.ResultType);
        }

        [Test]
        public void GetVolume_CallsGetVolumeReturningFloat()
        {
            var fake = new FakeJsObject();
            fake.CallReturnValues["getVolume"] = 0.6f;
            var ctrl = new BridgedInputController(fake);

            float vol = ctrl.GetVolume();

            Assert.AreEqual(0.6f, vol, 0.001f);
            var inv = fake.Invocations.FirstOrDefault(i => i.Name == "getVolume");
            Assert.IsNotNull(inv);
            Assert.AreEqual(InvocationKind.CallResult, inv!.Kind);
            Assert.AreEqual(typeof(float), inv.ResultType);
        }

        [Test]
        public void GetByteFrequencyData_PassesBufferLength_AndCopiesResultInto()
        {
            var fake = new FakeJsObject();
            // Wrapper requests buffer.Length samples; JS returns a fresh array.
            fake.CallReturnValues["getByteFrequencyData"] = new byte[] { 10, 20, 30, 40 };
            var ctrl = new BridgedInputController(fake);
            var buffer = new byte[4];

            ctrl.GetByteFrequencyData(buffer);

            var inv = fake.Invocations.FirstOrDefault(i => i.Name == "getByteFrequencyData");
            Assert.IsNotNull(inv);
            Assert.AreEqual(InvocationKind.CallResult, inv!.Kind);
            Assert.AreEqual(1, inv.Args.Length);
            Assert.AreEqual(4, inv.Args[0]);
            CollectionAssert.AreEqual(new byte[] { 10, 20, 30, 40 }, buffer);
        }

        [Test]
        public void GetByteFrequencyData_RemoteShorterThanBuffer_CopiesRemoteLengthOnly()
        {
            // Defensive: the wrapper clamps to min(remote.Length, buffer.Length)
            // so a JS regression that returns fewer bands than asked doesn't
            // throw IndexOutOfRange in user code.
            var fake = new FakeJsObject();
            fake.CallReturnValues["getByteFrequencyData"] = new byte[] { 1, 2 };
            var ctrl = new BridgedInputController(fake);
            var buffer = new byte[4];

            ctrl.GetByteFrequencyData(buffer);

            Assert.AreEqual(1, buffer[0]);
            Assert.AreEqual(2, buffer[1]);
            Assert.AreEqual(0, buffer[2]);
            Assert.AreEqual(0, buffer[3]);
        }

        [Test]
        public void GetByteFrequencyData_RemoteNull_LeavesBufferUntouched()
        {
            var fake = new FakeJsObject();
            // Property not configured → returns null
            var ctrl = new BridgedInputController(fake);
            var buffer = new byte[] { 7, 7, 7 };

            ctrl.GetByteFrequencyData(buffer);

            CollectionAssert.AreEqual(new byte[] { 7, 7, 7 }, buffer);
        }

        [Test]
        public void SetMuted_True_CallsSetMutedAsyncWithTrue()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedInputController(fake);

            ctrl.SetMuted(true).GetAwaiter().GetResult();

            var inv = fake.Invocations.FirstOrDefault(i => i.Name == "setMuted");
            Assert.IsNotNull(inv);
            Assert.AreEqual(InvocationKind.CallAsyncVoid, inv!.Kind);
            Assert.AreEqual(1, inv.Args.Length);
            Assert.AreEqual(true, inv.Args[0]);
        }

        [Test]
        public void SetMuted_False_CallsSetMutedAsyncWithFalse()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedInputController(fake);

            ctrl.SetMuted(false).GetAwaiter().GetResult();

            var inv = fake.Invocations.FirstOrDefault(i => i.Name == "setMuted");
            Assert.AreEqual(false, inv!.Args[0]);
        }

        [Test]
        public void SetDevice_PassesConfigAndFormat()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedInputController(fake);
            var device = new InputDeviceConfig
            {
                InputDeviceId = "mic-7",
                PreferHeadphonesForIosDevices = true,
            };
            var format = new FormatConfig("pcm", 16000);

            ctrl.SetDevice(device, format).GetAwaiter().GetResult();

            var inv = fake.Invocations.FirstOrDefault(i => i.Name == "setDevice");
            Assert.IsNotNull(inv);
            Assert.AreEqual(InvocationKind.CallAsyncVoid, inv!.Kind);
            Assert.AreEqual(2, inv.Args.Length);
            Assert.AreSame(device, inv.Args[0]);
            Assert.AreSame(format, inv.Args[1]);
        }

        [Test]
        public void SetDevice_NoArgs_PassesNullsThrough()
        {
            // Null is the wrapper's "leave the current value" signal — the JS
            // SDK treats JSON null the same way.
            var fake = new FakeJsObject();
            var ctrl = new BridgedInputController(fake);

            ctrl.SetDevice().GetAwaiter().GetResult();

            var inv = fake.Invocations.First(i => i.Name == "setDevice");
            Assert.AreEqual(2, inv.Args.Length);
            Assert.IsNull(inv.Args[0]);
            Assert.IsNull(inv.Args[1]);
        }

        [Test]
        public void Close_CallsCloseAsync_AndDisposes()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedInputController(fake);

            ctrl.Close().GetAwaiter().GetResult();

            var inv = fake.Invocations.FirstOrDefault(i => i.Name == "close");
            Assert.IsNotNull(inv);
            Assert.AreEqual(InvocationKind.CallAsyncVoid, inv!.Kind);
            Assert.IsTrue(fake.IsDisposed);
        }

        [Test]
        public void Close_IsIdempotent()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedInputController(fake);

            ctrl.Close().GetAwaiter().GetResult();
            ctrl.Close().GetAwaiter().GetResult();

            Assert.AreEqual(1, fake.Invocations.Count(i => i.Name == "close"));
        }
    }
}

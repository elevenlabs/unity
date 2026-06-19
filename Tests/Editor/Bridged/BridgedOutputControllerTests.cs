#nullable enable

using System;
using System.Linq;
using ElevenLabs.Agents;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Router-level tests for <see cref="BridgedOutputController"/>. Mirrors
    /// <see cref="BridgedInputControllerTests"/> and additionally locks the
    /// optional-arg shape of <c>Interrupt(int? resetDurationMs)</c>.
    /// </summary>
    public class BridgedOutputControllerTests
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
            Assert.Throws<ArgumentNullException>(() => new BridgedOutputController(null!));
        }

        [Test]
        public void SetVolume_CallsSetVolumeWithFloat()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedOutputController(fake);

            ctrl.SetVolume(0.25f);

            var inv = fake.Invocations.FirstOrDefault(i => i.Name == "setVolume");
            Assert.IsNotNull(inv);
            Assert.AreEqual(InvocationKind.CallVoid, inv!.Kind);
            Assert.AreEqual(1, inv.Args.Length);
            Assert.AreEqual(0.25f, (float)inv.Args[0], 0.001f);
        }

        [Test]
        public void GetVolume_CallsGetVolumeReturningFloat()
        {
            var fake = new FakeJsObject();
            fake.CallReturnValues["getVolume"] = 0.9f;
            var ctrl = new BridgedOutputController(fake);

            Assert.AreEqual(0.9f, ctrl.GetVolume(), 0.001f);
        }

        [Test]
        public void GetByteFrequencyData_PassesBufferLength_AndCopiesResultInto()
        {
            var fake = new FakeJsObject();
            fake.CallReturnValues["getByteFrequencyData"] = new byte[] { 1, 2, 3 };
            var ctrl = new BridgedOutputController(fake);
            var buffer = new byte[3];

            ctrl.GetByteFrequencyData(buffer);

            var inv = fake.Invocations.First(i => i.Name == "getByteFrequencyData");
            Assert.AreEqual(3, inv.Args[0]);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, buffer);
        }

        // Interrupt has two arg shapes — locking both because Phase 6 smoke
        // would silently swallow a regression that calls the no-arg path
        // when the user did supply a duration.

        [Test]
        public void Interrupt_NoArg_CallsInterruptWithoutOptions()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedOutputController(fake);

            ctrl.Interrupt();

            var inv = fake.Invocations.FirstOrDefault(i => i.Name == "interrupt");
            Assert.IsNotNull(inv);
            Assert.AreEqual(InvocationKind.CallVoid, inv!.Kind);
            Assert.AreEqual(0, inv.Args.Length);
        }

        [Test]
        public void Interrupt_WithDuration_PassesOptionsObject()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedOutputController(fake);

            ctrl.Interrupt(250);

            var inv = fake.Invocations.First(i => i.Name == "interrupt");
            Assert.AreEqual(1, inv.Args.Length);
            // The wrapper passes an anonymous { resetDurationMs = N }; reflect
            // on it rather than newing up a matching anonymous type so the
            // assertion survives a future refactor that swaps the shape's
            // class identity.
            object opts = inv.Args[0];
            var prop = opts.GetType().GetProperty("resetDurationMs");
            Assert.IsNotNull(prop);
            Assert.AreEqual(250, (int)prop!.GetValue(opts)!);
        }

        [Test]
        public void SetDevice_PassesConfigAndFormat()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedOutputController(fake);
            var device = new OutputDeviceConfig { OutputDeviceId = "spkr-1" };
            var format = new FormatConfig("pcm", 24000);

            ctrl.SetDevice(device, format).GetAwaiter().GetResult();

            var inv = fake.Invocations.First(i => i.Name == "setDevice");
            Assert.AreEqual(InvocationKind.CallAsyncVoid, inv.Kind);
            Assert.AreEqual(2, inv.Args.Length);
            Assert.AreSame(device, inv.Args[0]);
            Assert.AreSame(format, inv.Args[1]);
        }

        [Test]
        public void SetDevice_NoArgs_PassesNullsThrough()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedOutputController(fake);

            ctrl.SetDevice().GetAwaiter().GetResult();

            var inv = fake.Invocations.First(i => i.Name == "setDevice");
            Assert.IsNull(inv.Args[0]);
            Assert.IsNull(inv.Args[1]);
        }

        [Test]
        public void Close_CallsCloseAsync_AndDisposes()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedOutputController(fake);

            ctrl.Close().GetAwaiter().GetResult();

            Assert.IsTrue(fake.Invocations.Any(i => i.Name == "close"));
            Assert.IsTrue(fake.IsDisposed);
        }

        [Test]
        public void Close_IsIdempotent()
        {
            var fake = new FakeJsObject();
            var ctrl = new BridgedOutputController(fake);

            ctrl.Close().GetAwaiter().GetResult();
            ctrl.Close().GetAwaiter().GetResult();

            Assert.AreEqual(1, fake.Invocations.Count(i => i.Name == "close"));
        }
    }
}

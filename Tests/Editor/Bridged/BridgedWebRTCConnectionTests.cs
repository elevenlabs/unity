#nullable enable

using System;
using System.Linq;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
using ElevenLabs.WebGL.Internal;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Router-level tests for <see cref="BridgedWebRTCConnection"/>. Mirrors the
    /// shape of <see cref="BridgedWebSocketConnectionTests"/> but additionally
    /// exercises <c>GetCoupledInput</c> / <c>GetCoupledOutput</c> — the WebRTC
    /// arm's substitute for the WebSocket <c>attachDefaultAudio</c> step.
    /// </summary>
    public class BridgedWebRTCConnectionTests
    {
        [TearDown]
        public void TearDown()
        {
            CallbackRegistry.ResetForTests();
            PromiseRegistry.ResetForTests();
        }

        private static BridgeCallback ExtractCallback(FakeJsObject fake, string method)
        {
            var inv = fake.Invocations.FirstOrDefault(i => i.Name == method);
            Assert.IsNotNull(inv, $"No invocation for {method} was recorded.");
            Assert.AreEqual(1, inv!.Args.Length);
            return (BridgeCallback)inv.Args[0];
        }

        // Constructor wiring -------------------------------------------------

        [Test]
        public void Ctor_NullJsObject_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() => new BridgedWebRTCConnection(null!));
        }

        [Test]
        public void Ctor_RegistersOnMessageOnDisconnectOnModeChange_ExactlyOnce()
        {
            var fake = new FakeJsObject();
            _ = new BridgedWebRTCConnection(fake);

            CollectionAssert.AreEqual(
                new[] { "onMessage", "onDisconnect", "onModeChange" },
                fake.CalledMethods().ToList()
            );
        }

        // Send ---------------------------------------------------------------

        [Test]
        public void Send_CallsSendMessageWithTypedEvent()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebRTCConnection(fake);
            var message = new UserMessage { Text = "hi" };

            conn.Send(message);

            var inv = fake.Invocations.FirstOrDefault(i => i.Name == "sendMessage");
            Assert.IsNotNull(inv);
            Assert.AreEqual(InvocationKind.CallVoid, inv!.Kind);
            Assert.AreSame(message, inv.Args[0]);
        }

        // OnMessage / OnDisconnect / OnModeChange dispatch -------------------

        [Test]
        public void OnMessageCallback_DispatchesTypedIncomingSocketEvent()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebRTCConnection(fake);
            IncomingSocketEvent? received = null;
            conn.OnMessage += evt => received = evt;

            BridgeCallback cb = ExtractCallback(fake, "onMessage");
            string inner =
                "{\"type\":\"agent_response\",\"agent_response_event\":{\"agent_response\":\"hi\"}}";
            CallbackRegistry.TryDispatch(cb.Handle, inner);

            Assert.IsInstanceOf<AgentResponse>(received);
        }

        [Test]
        public void OnDisconnectCallback_DispatchesFlattenedDisconnectionDetails()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebRTCConnection(fake);
            DisconnectionDetails? received = null;
            conn.OnDisconnect += d => received = d;

            BridgeCallback cb = ExtractCallback(fake, "onDisconnect");
            CallbackRegistry.TryDispatch(cb.Handle, "{\"reason\":\"user\"}");

            Assert.AreEqual(DisconnectionReason.User, received!.Reason);
        }

        [Test]
        public void OnModeChangeCallback_DispatchesMappedMode()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebRTCConnection(fake);
            Mode? received = null;
            conn.OnModeChange += m => received = m;

            BridgeCallback cb = ExtractCallback(fake, "onModeChange");
            CallbackRegistry.TryDispatch(cb.Handle, "\"listening\"");

            Assert.AreEqual(Mode.Listening, received);
        }

        // Property reads -----------------------------------------------------

        [Test]
        public void ConversationId_ReadsFromJsProperty()
        {
            var fake = new FakeJsObject();
            fake.PropertyValues["conversationId"] = "rtc-1";
            var conn = new BridgedWebRTCConnection(fake);

            Assert.AreEqual("rtc-1", conn.ConversationId);
        }

        [Test]
        public void InputFormat_ProjectsViaMarshalling()
        {
            var fake = new FakeJsObject();
            fake.PropertyValues["inputFormat"] = JObject.Parse(
                "{\"format\":\"pcm\",\"sampleRate\":48000}"
            );
            var conn = new BridgedWebRTCConnection(fake);

            FormatConfig fmt = conn.InputFormat;
            Assert.AreEqual("pcm", fmt.Format);
            Assert.AreEqual(48000, fmt.SampleRate);
        }

        [Test]
        public void OutputFormat_ProjectsViaMarshalling()
        {
            var fake = new FakeJsObject();
            fake.PropertyValues["outputFormat"] = JObject.Parse(
                "{\"format\":\"pcm\",\"sampleRate\":48000}"
            );
            var conn = new BridgedWebRTCConnection(fake);

            FormatConfig fmt = conn.OutputFormat;
            Assert.AreEqual(48000, fmt.SampleRate);
        }

        // Coupled controllers ------------------------------------------------

        [Test]
        public void GetCoupledInput_ReadsInputViaGetIJsObject_AndReturnsWrapper()
        {
            // WebRTC pre-wires audio inside livekit-client; the wrapper reads
            // the controller off the connection rather than calling separate
            // factories. Verifying the Get<IJsObject>("input") call locks the
            // contract.
            var inputHandle = new FakeJsObject(handle: 11);
            var fake = new FakeJsObject();
            fake.PropertyValues["input"] = inputHandle;
            var conn = new BridgedWebRTCConnection(fake);

            BridgedInputController input = conn.GetCoupledInput();

            Assert.IsNotNull(input);
            var getInv = fake.Invocations.FirstOrDefault(i =>
                i.Kind == InvocationKind.Get && i.Name == "input"
            );
            Assert.IsNotNull(getInv, "Get<IJsObject>(\"input\") must be issued exactly once.");
            Assert.AreEqual(typeof(IJsObject), getInv!.ResultType);

            // The returned wrapper genuinely sits on top of the handle the
            // stub supplied — IsMuted reads back through the same FakeJsObject.
            inputHandle.PropertyValues["isMuted"] = true;
            Assert.IsTrue(input.IsMuted);
        }

        [Test]
        public void GetCoupledOutput_ReadsOutputViaGetIJsObject_AndReturnsWrapper()
        {
            var outputHandle = new FakeJsObject(handle: 12);
            var fake = new FakeJsObject();
            fake.PropertyValues["output"] = outputHandle;
            var conn = new BridgedWebRTCConnection(fake);

            BridgedOutputController output = conn.GetCoupledOutput();

            Assert.IsNotNull(output);
            var getInv = fake.Invocations.FirstOrDefault(i =>
                i.Kind == InvocationKind.Get && i.Name == "output"
            );
            Assert.IsNotNull(getInv, "Get<IJsObject>(\"output\") must be issued exactly once.");
            Assert.AreEqual(typeof(IJsObject), getInv!.ResultType);

            outputHandle.CallReturnValues["getVolume"] = 0.42f;
            Assert.AreEqual(0.42f, output.GetVolume(), 0.001f);
        }

        // Close --------------------------------------------------------------

        [Test]
        public void Close_CallsClose_DisposesCallbacksAndJsObject()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebRTCConnection(fake);
            BridgeCallback msgCb = ExtractCallback(fake, "onMessage");

            conn.Close();

            Assert.IsTrue(fake.Invocations.Any(i => i.Name == "close"));
            Assert.IsTrue(fake.IsDisposed);
            Assert.IsFalse(CallbackRegistry.TryDispatch(msgCb.Handle, "\"x\""));
        }

        [Test]
        public void Close_IsIdempotent()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebRTCConnection(fake);

            conn.Close();
            conn.Close();

            Assert.AreEqual(1, fake.Invocations.Count(i => i.Name == "close"));
        }
    }
}

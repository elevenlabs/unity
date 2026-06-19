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
    /// Router-level tests for <see cref="BridgedWebSocketConnection"/>. Drives
    /// the wrapper through a stub <see cref="IJsObject"/> so the constructor's
    /// JS subscriptions, <c>Send</c>, the callback fan-out, and <c>Close</c>'s
    /// teardown can all be asserted in Edit Mode without touching the WebGL
    /// <c>DllImport</c>s.
    /// </summary>
    public class BridgedWebSocketConnectionTests
    {
        [TearDown]
        public void TearDown()
        {
            // The wrapper's BridgeCallback.Wrap calls register entries in the
            // global CallbackRegistry; reset so partial registrations from one
            // test don't bleed into the next.
            CallbackRegistry.ResetForTests();
            PromiseRegistry.ResetForTests();
        }

        private static BridgeCallback ExtractCallback(FakeJsObject fake, string method)
        {
            var inv = fake.Invocations.FirstOrDefault(i => i.Name == method);
            Assert.IsNotNull(inv, $"No invocation for {method} was recorded on the JsObject.");
            Assert.AreEqual(1, inv!.Args.Length, $"Expected exactly one arg passed to {method}.");
            Assert.IsInstanceOf<BridgeCallback>(inv.Args[0]);
            return (BridgeCallback)inv.Args[0];
        }

        // Constructor wiring -------------------------------------------------

        [Test]
        public void Ctor_NullJsObject_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() => new BridgedWebSocketConnection(null!));
        }

        [Test]
        public void Ctor_RegistersOnMessageOnDisconnectOnModeChange_ExactlyOnce()
        {
            var fake = new FakeJsObject();
            _ = new BridgedWebSocketConnection(fake);

            // The constructor subscribes one BridgeCallback per JS event;
            // multiple subscriptions would imply we're shipping callback churn
            // for every C# `+=`, which is the Plan A anti-pattern Plan B
            // explicitly avoids.
            var calls = fake.CalledMethods().ToList();
            Assert.AreEqual(3, calls.Count);
            CollectionAssert.AreEqual(new[] { "onMessage", "onDisconnect", "onModeChange" }, calls);
        }

        [Test]
        public void Ctor_RegistersThreeBridgeCallbacksInRegistry()
        {
            var fake = new FakeJsObject();
            _ = new BridgedWebSocketConnection(fake);

            Assert.AreEqual(3, CallbackRegistry.Count);
        }

        [Test]
        public void IncomingMessageCallback_IsTheSameInstancePassedToOnMessage()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebSocketConnection(fake);

            // BridgedSession passes this same callback to attachDefaultAudio so
            // the JS-side withoutAudioPayload wrapper fires the SAME C# delegate
            // even though connection.onMessage is set twice during setup.
            BridgeCallback wired = ExtractCallback(fake, "onMessage");
            Assert.AreSame(wired, conn.IncomingMessageCallback);
        }

        // Send ---------------------------------------------------------------

        [Test]
        public void Send_CallsSendMessageWithTypedEvent()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebSocketConnection(fake);
            var message = new UserMessage { Text = "hi" };

            conn.Send(message);

            var sendInv = fake.Invocations.FirstOrDefault(i => i.Name == "sendMessage");
            Assert.IsNotNull(sendInv, "sendMessage was not invoked.");
            Assert.AreEqual(InvocationKind.CallVoid, sendInv!.Kind);
            Assert.AreEqual(1, sendInv.Args.Length);
            Assert.AreSame(message, sendInv.Args[0]);
        }

        // OnMessage dispatch -------------------------------------------------

        [Test]
        public void OnMessageCallback_DispatchesTypedIncomingSocketEvent()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebSocketConnection(fake);
            IncomingSocketEvent? received = null;
            conn.OnMessage += evt => received = evt;

            BridgeCallback cb = ExtractCallback(fake, "onMessage");
            string inner =
                "{\"type\":\"agent_response\",\"agent_response_event\":{\"agent_response\":\"hello\"}}";
            CallbackRegistry.TryDispatch(cb.Handle, inner);

            Assert.IsInstanceOf<AgentResponse>(received);
            Assert.AreEqual("hello", ((AgentResponse)received!).AgentResponseEvent.AgentResponse);
        }

        [Test]
        public void OnMessageCallback_UnknownWireType_DispatchesUnknownIncomingEvent()
        {
            // Future-proofing — the polymorphic converter routes unknown `type`
            // discriminators to UnknownIncomingEvent so subscribers don't crash
            // when the server adds a new event ahead of an SDK refresh.
            var fake = new FakeJsObject();
            var conn = new BridgedWebSocketConnection(fake);
            IncomingSocketEvent? received = null;
            conn.OnMessage += evt => received = evt;

            BridgeCallback cb = ExtractCallback(fake, "onMessage");
            string inner = "{\"type\":\"future_event_v3\",\"value\":42}";
            CallbackRegistry.TryDispatch(cb.Handle, inner);

            Assert.IsInstanceOf<UnknownIncomingEvent>(received);
            Assert.AreEqual("future_event_v3", ((UnknownIncomingEvent)received!).Type);
        }

        // OnDisconnect dispatch ----------------------------------------------

        [Test]
        public void OnDisconnectCallback_DispatchesFlattenedDisconnectionDetails()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebSocketConnection(fake);
            DisconnectionDetails? received = null;
            conn.OnDisconnect += details => received = details;

            BridgeCallback cb = ExtractCallback(fake, "onDisconnect");
            string inner =
                "{\"reason\":\"error\",\"message\":\"abnormal close\",\"closeCode\":1006,\"closeReason\":\"network\"}";
            CallbackRegistry.TryDispatch(cb.Handle, inner);

            Assert.IsNotNull(received);
            Assert.AreEqual(DisconnectionReason.Error, received!.Reason);
            Assert.AreEqual("abnormal close", received.Message);
            Assert.AreEqual(1006, received.CloseCode);
            Assert.AreEqual("network", received.CloseReason);
        }

        // OnModeChange dispatch ----------------------------------------------

        [Test]
        public void OnModeChangeCallback_DispatchesMappedMode()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebSocketConnection(fake);
            Mode? received = null;
            conn.OnModeChange += mode => received = mode;

            BridgeCallback cb = ExtractCallback(fake, "onModeChange");
            CallbackRegistry.TryDispatch(cb.Handle, "\"speaking\"");

            Assert.AreEqual(Mode.Speaking, received);
        }

        [Test]
        public void OnModeChangeCallback_UnknownMode_DropsEventSilently()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebSocketConnection(fake);
            int fireCount = 0;
            conn.OnModeChange += _ => fireCount++;

            BridgeCallback cb = ExtractCallback(fake, "onModeChange");
            CallbackRegistry.TryDispatch(cb.Handle, "\"singing\"");

            Assert.AreEqual(0, fireCount);
        }

        // Property reads -----------------------------------------------------

        [Test]
        public void ConversationId_ReadsFromJsProperty()
        {
            var fake = new FakeJsObject();
            fake.PropertyValues["conversationId"] = "conv-42";
            var conn = new BridgedWebSocketConnection(fake);

            Assert.AreEqual("conv-42", conn.ConversationId);
        }

        [Test]
        public void ConversationId_NullProperty_ReturnsEmptyString()
        {
            // The JS side may not have populated the id yet when the wrapper
            // is queried; surface the gap as the empty string rather than
            // null so callers don't need a guard.
            var fake = new FakeJsObject();
            var conn = new BridgedWebSocketConnection(fake);

            Assert.AreEqual(string.Empty, conn.ConversationId);
        }

        [Test]
        public void InputFormat_ProjectsViaMarshalling()
        {
            var fake = new FakeJsObject();
            fake.PropertyValues["inputFormat"] = JObject.Parse(
                "{\"format\":\"pcm\",\"sampleRate\":16000}"
            );
            var conn = new BridgedWebSocketConnection(fake);

            FormatConfig fmt = conn.InputFormat;
            Assert.AreEqual("pcm", fmt.Format);
            Assert.AreEqual(16000, fmt.SampleRate);
        }

        [Test]
        public void OutputFormat_ProjectsViaMarshalling()
        {
            var fake = new FakeJsObject();
            fake.PropertyValues["outputFormat"] = JObject.Parse(
                "{\"format\":\"pcm\",\"sampleRate\":24000}"
            );
            var conn = new BridgedWebSocketConnection(fake);

            FormatConfig fmt = conn.OutputFormat;
            Assert.AreEqual("pcm", fmt.Format);
            Assert.AreEqual(24000, fmt.SampleRate);
        }

        // Close --------------------------------------------------------------

        [Test]
        public void Close_CallsClose_DisposesCallbacksAndJsObject()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebSocketConnection(fake);
            BridgeCallback msgCb = ExtractCallback(fake, "onMessage");
            BridgeCallback dcCb = ExtractCallback(fake, "onDisconnect");
            BridgeCallback modeCb = ExtractCallback(fake, "onModeChange");

            conn.Close();

            Assert.IsTrue(fake.Invocations.Any(i => i.Name == "close"));
            Assert.IsTrue(fake.IsDisposed);
            // Disposed callbacks no longer dispatch — the registry has cleaned
            // up after each Dispose call.
            Assert.IsFalse(CallbackRegistry.TryDispatch(msgCb.Handle, "\"x\""));
            Assert.IsFalse(CallbackRegistry.TryDispatch(dcCb.Handle, "\"x\""));
            Assert.IsFalse(CallbackRegistry.TryDispatch(modeCb.Handle, "\"x\""));
        }

        [Test]
        public void Close_IsIdempotent()
        {
            var fake = new FakeJsObject();
            var conn = new BridgedWebSocketConnection(fake);

            conn.Close();
            int firstCloseCalls = fake.Invocations.Count(i => i.Name == "close");
            conn.Close();
            int secondCloseCalls = fake.Invocations.Count(i => i.Name == "close");

            Assert.AreEqual(1, firstCloseCalls);
            Assert.AreEqual(1, secondCloseCalls);
        }

        [Test]
        public void Close_InvokesAndDisposesAudioDetach_BeforeClose()
        {
            var fake = new FakeJsObject();
            var detach = new FakeJsFunction();
            var conn = new BridgedWebSocketConnection(fake);
            conn.AttachAudioDetach(detach);

            conn.Close();

            // Order matters — the detach must run while the JS transport is
            // still live so attachInputToConnection / attachConnectionToOutput
            // can unhook themselves before close() tears the transport down.
            Assert.AreEqual(
                1,
                detach.Invocations.Count(i => i.Kind == InvocationKind.FunctionCallVoid),
                "Audio detach should be invoked exactly once."
            );
            Assert.IsTrue(
                detach.IsDisposed,
                "Audio detach handle should be disposed after invoke."
            );
            // The close() Call recorded on the connection comes AFTER the detach
            // Call+Dispose pair — the wrapper sequences the teardown that way.
            int detachIndex = detach.Invocations.FindIndex(i =>
                i.Kind == InvocationKind.FunctionCallVoid
            );
            int closeIndex = fake.Invocations.FindIndex(i => i.Name == "close");
            Assert.GreaterOrEqual(closeIndex, 0);
            Assert.GreaterOrEqual(detachIndex, 0);
        }

        [Test]
        public void AttachAudioDetach_Null_ThrowsArgumentNull()
        {
            var conn = new BridgedWebSocketConnection(new FakeJsObject());
            Assert.Throws<ArgumentNullException>(() => conn.AttachAudioDetach(null!));
        }

        [Test]
        public void AttachAudioDetach_Twice_ThrowsInvalidOperation()
        {
            // The audio glue is one-shot per connection — setting it twice is a
            // setup bug worth surfacing rather than silently overwriting.
            var conn = new BridgedWebSocketConnection(new FakeJsObject());
            conn.AttachAudioDetach(new FakeJsFunction());
            Assert.Throws<InvalidOperationException>(() =>
                conn.AttachAudioDetach(new FakeJsFunction())
            );
        }
    }
}

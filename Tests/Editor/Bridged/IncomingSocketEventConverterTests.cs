#nullable enable

using ElevenLabs.Protocol;
using Newtonsoft.Json;
using NUnit.Framework;
// UnityEngine.Ping collides with the protocol wire type — alias the wire one.
using Ping = ElevenLabs.Protocol.Ping;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Pure C# tests for the codegen-emitted polymorphic deserialiser. Locks
    /// the `type` discriminator → concrete subtype mapping so a missing arm
    /// from a future codegen change fails loudly here before it can ship.
    /// </summary>
    public class IncomingSocketEventConverterTests
    {
        private static readonly IncomingSocketEventConverter Converter = new();

        private static T DeserialiseAs<T>(string json)
            where T : IncomingSocketEvent
        {
            var evt = JsonConvert.DeserializeObject<IncomingSocketEvent>(json, Converter);
            Assert.IsNotNull(evt, "Converter returned null for known type.");
            return (T)evt!;
        }

        [Test]
        public void Deserialise_AgentResponse_ReturnsTypedSubclass()
        {
            const string json =
                "{\"type\":\"agent_response\",\"agent_response_event\":{\"agent_response\":\"hi\"}}";
            var typed = DeserialiseAs<AgentResponse>(json);
            Assert.AreEqual("hi", typed.AgentResponseEvent.AgentResponse);
        }

        [Test]
        public void Deserialise_AudioResponse_RoutesViaShortDiscriminator()
        {
            // Wire discriminator is "audio" (not "audio_response") — covers the
            // case where the C# class name and the wire `type` diverge.
            const string json =
                "{\"type\":\"audio\",\"audio_event\":{\"audio_base_64\":\"\",\"event_id\":7}}";
            var typed = DeserialiseAs<AudioResponse>(json);
            Assert.AreEqual(7, typed.AudioEvent.EventId);
        }

        [Test]
        public void Deserialise_Ping_RoutesToPingSubclass()
        {
            const string json = "{\"type\":\"ping\",\"ping_event\":{\"event_id\":42}}";
            var typed = DeserialiseAs<Ping>(json);
            Assert.AreEqual(42, typed.PingEvent.EventId);
        }

        [Test]
        public void Deserialise_ConversationInitiationMetadata_FlowsThrough()
        {
            const string json =
                "{\"type\":\"conversation_initiation_metadata\",\"conversation_initiation_metadata_event\":{\"conversation_id\":\"abc\",\"agent_output_audio_format\":\"pcm_24000\",\"user_input_audio_format\":\"pcm_16000\"}}";
            var typed = DeserialiseAs<ConversationInitiationMetadata>(json);
            Assert.AreEqual("abc", typed.ConversationInitiationMetadataEvent.ConversationId);
        }

        [Test]
        public void Deserialise_UnknownType_ReturnsUnknownIncomingEvent()
        {
            const string json = "{\"type\":\"server_added_event_v2\",\"some_field\":{\"x\":1}}";
            var evt = JsonConvert.DeserializeObject<IncomingSocketEvent>(json, Converter);
            Assert.IsInstanceOf<UnknownIncomingEvent>(evt);
            var unknown = (UnknownIncomingEvent)evt!;
            Assert.AreEqual("server_added_event_v2", unknown.Type);
            StringAssert.Contains("server_added_event_v2", unknown.RawJson);
        }

        [Test]
        public void Deserialise_NoTypeField_ReturnsUnknownWithNullType()
        {
            const string json = "{\"value\":42}";
            var evt = JsonConvert.DeserializeObject<IncomingSocketEvent>(json, Converter);
            Assert.IsInstanceOf<UnknownIncomingEvent>(evt);
            var unknown = (UnknownIncomingEvent)evt!;
            Assert.IsNull(unknown.Type);
        }

        [Test]
        public void CanWrite_IsFalse()
        {
            Assert.IsFalse(Converter.CanWrite);
        }
    }
}

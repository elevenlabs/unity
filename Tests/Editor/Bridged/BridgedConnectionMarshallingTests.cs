#nullable enable

using ElevenLabs.Agents;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Pure C# coverage for the JS→Core marshalling helpers shared by the
    /// bridged connection wrappers. The wrappers themselves can't be
    /// constructed in Edit Mode (their constructors call into JS via the
    /// DllImport-backed primitives layer) so this is where the parsing
    /// correctness is locked.
    /// </summary>
    public class BridgedConnectionMarshallingTests
    {
        // ParseMode -----------------------------------------------------------

        [Test]
        public void ParseMode_Speaking_ReturnsSpeaking()
        {
            Assert.AreEqual(Mode.Speaking, BridgedConnectionMarshalling.ParseMode("speaking"));
        }

        [Test]
        public void ParseMode_Listening_ReturnsListening()
        {
            Assert.AreEqual(Mode.Listening, BridgedConnectionMarshalling.ParseMode("listening"));
        }

        [Test]
        public void ParseMode_UnknownString_ReturnsNull()
        {
            Assert.IsNull(BridgedConnectionMarshalling.ParseMode("singing"));
        }

        // ParseDisconnectionDetails -------------------------------------------

        [Test]
        public void ParseDisconnectionDetails_UserReason_RoundTrips()
        {
            var payload = JObject.Parse("{\"reason\":\"user\"}");
            var details = BridgedConnectionMarshalling.ParseDisconnectionDetails(payload);
            Assert.AreEqual(DisconnectionReason.User, details.Reason);
            Assert.IsNull(details.Message);
            Assert.IsNull(details.Context);
        }

        [Test]
        public void ParseDisconnectionDetails_AgentReason_PopulatesContext()
        {
            var payload = JObject.Parse(
                "{\"reason\":\"agent\",\"context\":{\"type\":\"close\",\"reason\":\"end_call\",\"code\":1000}}"
            );
            var details = BridgedConnectionMarshalling.ParseDisconnectionDetails(payload);
            Assert.AreEqual(DisconnectionReason.Agent, details.Reason);
            Assert.IsNotNull(details.Context);
            Assert.AreEqual("close", details.Context!.Type);
            Assert.AreEqual("end_call", details.Context.Reason);
            Assert.AreEqual(1000, details.Context.Code);
        }

        [Test]
        public void ParseDisconnectionDetails_ErrorReason_CarriesMessageAndCloseCode()
        {
            var payload = JObject.Parse(
                "{\"reason\":\"error\",\"message\":\"abnormal close\",\"closeCode\":1006,\"closeReason\":\"network\"}"
            );
            var details = BridgedConnectionMarshalling.ParseDisconnectionDetails(payload);
            Assert.AreEqual(DisconnectionReason.Error, details.Reason);
            Assert.AreEqual("abnormal close", details.Message);
            Assert.AreEqual(1006, details.CloseCode);
            Assert.AreEqual("network", details.CloseReason);
        }

        [Test]
        public void ParseDisconnectionDetails_UnknownReason_DefaultsToError()
        {
            // Future-proofing: a server-side rename of the reason discriminator
            // shouldn't crash — fall back to Error and let the subscriber surface
            // it as an error path.
            var payload = JObject.Parse("{\"reason\":\"timeout\"}");
            var details = BridgedConnectionMarshalling.ParseDisconnectionDetails(payload);
            Assert.AreEqual(DisconnectionReason.Error, details.Reason);
        }

        [Test]
        public void ParseDisconnectionDetails_MissingReason_DefaultsToError()
        {
            var payload = JObject.Parse("{}");
            var details = BridgedConnectionMarshalling.ParseDisconnectionDetails(payload);
            Assert.AreEqual(DisconnectionReason.Error, details.Reason);
        }

        // ParseFormatConfig ---------------------------------------------------

        [Test]
        public void ParseFormatConfig_ReadsFormatAndSampleRate()
        {
            var payload = JObject.Parse("{\"format\":\"pcm\",\"sampleRate\":16000}");
            var fmt = BridgedConnectionMarshalling.ParseFormatConfig(payload);
            Assert.AreEqual("pcm", fmt.Format);
            Assert.AreEqual(16000, fmt.SampleRate);
        }

        [Test]
        public void ParseFormatConfig_MissingFormat_Throws()
        {
            var payload = JObject.Parse("{\"sampleRate\":16000}");
            Assert.Throws<System.InvalidOperationException>(() =>
                BridgedConnectionMarshalling.ParseFormatConfig(payload)
            );
        }
    }
}

#nullable enable

using Newtonsoft.Json;

namespace ElevenLabs.Protocol
{
    // Hand-written companion to the generated OutgoingSocketEvent.g.cs.
    // The vendored AsyncAPI spec (Codegen~/schemas/convai-asyncapi.yml) does
    // not include a `feedback` outgoing message — the JS SDK sends one but
    // the spec hasn't been updated to declare it. Add the schema entry
    // upstream in elevenlabs/xi and delete this file once it regenerates.
    public class Feedback : OutgoingSocketEvent
    {
        [JsonProperty("type")]
        public string Type { get; init; } = "feedback";

        [JsonProperty("score")]
        public string Score { get; set; } = "";

        [JsonProperty("event_id")]
        public int EventId { get; set; } = 0;
    }
}

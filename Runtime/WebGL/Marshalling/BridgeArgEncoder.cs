using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ElevenLabs.WebGL
{
    /// <summary>
    /// Encodes a <c>params object[]</c> argument list to a JSON array string, applying
    /// typed markers for bridge handle types: <c>{"$ref": h}</c> for <see cref="JsObject"/>,
    /// <c>{"$fn": h}</c> for <see cref="JsFunction"/>, <c>{"$cb": h}</c> for
    /// <see cref="BridgeCallback"/>. All other values are JSON-serialised as-is via
    /// Newtonsoft.Json.
    /// </summary>
    internal static class BridgeArgEncoder
    {
        public static string Encode(params object[] args)
        {
            var tokens = (args ?? System.Array.Empty<object>()).Select(EncodeValue);
            return new JArray(tokens).ToString(Formatting.None);
        }

        private static JToken EncodeValue(object value) =>
            value switch
            {
                null => JValue.CreateNull(),
                JsObject jsObj => new JObject { ["$ref"] = jsObj.Handle },
                JsFunction jsFn => new JObject { ["$fn"] = jsFn.Handle },
                BridgeCallback cb => new JObject { ["$cb"] = cb.Handle },
                _ => JToken.FromObject(value),
            };
    }
}

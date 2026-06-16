using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ElevenLabs.WebGL
{
    /// <summary>
    /// Decodes the JSON string returned from the JS bridge into a typed C# value.
    /// Recognises <c>{"$ref": h}</c> → <see cref="JsObject"/> and
    /// <c>{"$fn": h}</c> → <see cref="JsFunction"/>; all other JSON is deserialised via
    /// Newtonsoft.Json using the caller-supplied <typeparamref name="T"/>.
    /// </summary>
    internal static class BridgeValueDecoder
    {
        public static T Decode<T>(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null")
                return default;

            if (typeof(T) == typeof(JsObject))
            {
                int handle = JToken.Parse(json).Value<int>("$ref");
                return (T)(object)new JsObject(handle);
            }

            if (typeof(T) == typeof(JsFunction))
            {
                int handle = JToken.Parse(json).Value<int>("$fn");
                return (T)(object)new JsFunction(handle);
            }

            return JsonConvert.DeserializeObject<T>(json);
        }

        /// <summary>
        /// Returns the <see cref="BridgeReturnShape"/> int code that the JS dispatcher should
        /// use when encoding its return value. Derived from <typeparamref name="T"/> so the
        /// call site and the JS side are always in sync.
        /// </summary>
        public static BridgeReturnShape GetShapeFor<T>() =>
            typeof(T) == typeof(JsObject) ? BridgeReturnShape.Object
            : typeof(T) == typeof(JsFunction) ? BridgeReturnShape.Function
            : BridgeReturnShape.Value;
    }
}

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
        /// <summary>
        /// Decodes the JSON string returned from the bridge. Recognises <c>{"$ref": h}</c> and
        /// <c>{"$fn": h}</c> markers; everything else is deserialised via Newtonsoft.Json.
        /// </summary>
        /// <typeparam name="T">Expected type of the decoded value.</typeparam>
        /// <param name="json">The JSON string from the bridge, or null/empty for a void result.</param>
        /// <returns>The decoded value, or <c>default</c> for a null/empty/void result.</returns>
        public static T Decode<T>(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null")
                return default;

            if (typeof(T) == typeof(JsObject) || typeof(T) == typeof(IJsObject))
            {
                int handle = JToken.Parse(json).Value<int>("$ref");
                return (T)(object)new JsObject(handle);
            }

            if (typeof(T) == typeof(JsFunction) || typeof(T) == typeof(IJsFunction))
            {
                int handle = JToken.Parse(json).Value<int>("$fn");
                return (T)(object)new JsFunction(handle);
            }

            return JsonConvert.DeserializeObject<T>(json);
        }

        /// <summary>
        /// Returns the <see cref="BridgeReturnShape"/> int code that the JS dispatcher should
        /// use when encoding its return value. Derived from <typeparamref name="T"/> so the
        /// call site and the JS side are always in sync. <see cref="IJsObject"/> and
        /// <see cref="IJsFunction"/> map to the same shapes as their concrete implementations
        /// so wrappers consuming the interface get correct return-shape codes when calling
        /// <c>Get&lt;IJsObject&gt;</c> in production code.
        /// </summary>
        public static BridgeReturnShape GetShapeFor<T>() =>
            typeof(T) == typeof(JsObject) || typeof(T) == typeof(IJsObject)
                ? BridgeReturnShape.Object
            : typeof(T) == typeof(JsFunction) || typeof(T) == typeof(IJsFunction)
                ? BridgeReturnShape.Function
            : BridgeReturnShape.Value;
    }
}

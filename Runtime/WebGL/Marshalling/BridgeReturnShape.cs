namespace ElevenLabs.WebGL
{
    /// <summary>
    /// Instructs the JS dispatcher how to encode its return value before settling the promise.
    /// Sent as an <c>int</c> across the DllImport boundary; the C# call site derives the code
    /// automatically from the generic <c>&lt;T&gt;</c> parameter via
    /// <see cref="BridgeValueDecoder.GetShapeFor{T}"/>.
    /// </summary>
    internal enum BridgeReturnShape
    {
        /// <summary>Plain JSON round-trip. Used for primitives, arrays, and data objects.</summary>
        Value = 0,

        /// <summary>Allocate a <see cref="JsObject"/> handle; encode as <c>{"$ref": handle}</c>.</summary>
        Object = 1,

        /// <summary>Allocate a <see cref="JsFunction"/> handle; encode as <c>{"$fn": handle}</c>.</summary>
        Function = 2,

        /// <summary>Discard the return value; settle with <c>null</c>.</summary>
        Void = 3,
    }
}

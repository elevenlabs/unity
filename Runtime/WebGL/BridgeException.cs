using System;

namespace ElevenLabs.WebGL
{
    /// <summary>Represents an error that originated from JavaScript via the WebGL bridge.</summary>
    public sealed class BridgeException : Exception
    {
        /// <summary>Creates a <see cref="BridgeException"/> with the JS error message.</summary>
        public BridgeException(string message)
            : base(message) { }

        /// <summary>Creates a <see cref="BridgeException"/> with a JS error message and an inner exception.</summary>
        public BridgeException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}

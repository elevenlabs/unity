using System;

namespace ElevenLabs.WebGL
{
    /// <summary>
    /// Parses the colon-delimited message strings delivered via Unity's <c>SendMessage</c>.
    /// All three bridge primitives share the "integer ID prefix" shape; only the number of
    /// colon-separated segments before the payload differs. Payloads may always contain colons.
    /// </summary>
    internal static class BridgeMessageParser
    {
        /// <summary>Parses <c>"id:payload"</c> into its components. Payload may contain colons.</summary>
        /// <exception cref="FormatException">Thrown when the message lacks a <c>':'</c> separator.</exception>
        public static (int id, string payload) ParseIdPayload(string message)
        {
            int sep = message.IndexOf(':');
            if (sep < 0)
                throw new FormatException($"Bridge message missing ':' separator: '{message}'");
            return (int.Parse(message.AsSpan(0, sep)), message[(sep + 1)..]);
        }

        /// <summary>Parses <c>"id:status:payload"</c> into its components. Payload may contain colons.</summary>
        /// <exception cref="FormatException">Thrown when the message lacks two <c>':'</c> separators.</exception>
        public static (int id, string status, string payload) ParseIdStatusPayload(string message)
        {
            var (id, second, payload) = SplitIdSecondPayload(message);
            return (id, second, payload);
        }

        /// <summary>Parses <c>"id:type:payload"</c> into its components. Payload may contain colons.</summary>
        /// <exception cref="FormatException">Thrown when the message lacks two <c>':'</c> separators.</exception>
        public static (int id, string type, string payload) ParseIdTypePayload(string message)
        {
            var (id, second, payload) = SplitIdSecondPayload(message);
            return (id, second, payload);
        }

        private static (int id, string second, string payload) SplitIdSecondPayload(string message)
        {
            int sep1 = message.IndexOf(':');
            if (sep1 < 0)
                throw new FormatException(
                    $"Bridge message missing first ':' separator: '{message}'"
                );
            int sep2 = message.IndexOf(':', sep1 + 1);
            if (sep2 < 0)
                throw new FormatException(
                    $"Bridge message missing second ':' separator: '{message}'"
                );
            return (
                int.Parse(message.AsSpan(0, sep1)),
                message[(sep1 + 1)..sep2],
                message[(sep2 + 1)..]
            );
        }
    }
}

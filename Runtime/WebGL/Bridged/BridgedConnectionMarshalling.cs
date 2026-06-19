#nullable enable

using System;
using ElevenLabs.Agents;
using Newtonsoft.Json.Linq;

namespace ElevenLabs.WebGL.Bridged
{
    /// <summary>
    /// JSON-to-C# helpers shared by the bridged connection wrappers. The
    /// <c>@elevenlabs/client</c> SDK delivers callback payloads in its own
    /// camelCase / union shape; these helpers translate them into the
    /// platform-agnostic Core types (<see cref="Mode"/>,
    /// <see cref="DisconnectionDetails"/>) exposed on <see cref="IConnection"/>.
    /// </summary>
    /// <remarks>
    /// Kept WebGL-only on purpose — the parsing is specific to the JS SDK's
    /// wire shape. Native transports (Phase 7) materialise the same Core types
    /// directly from their own protocol layer.
    /// </remarks>
    internal static class BridgedConnectionMarshalling
    {
        /// <summary>
        /// Parses the JSON string the JS <c>onModeChange</c> callback delivers
        /// (a quoted JSON string literal, e.g. <c>"speaking"</c>). Returns
        /// <c>null</c> when the value isn't a recognised mode so the caller
        /// can drop the event instead of raising on a server-side rename.
        /// </summary>
        public static Mode? ParseMode(string modeString) =>
            modeString switch
            {
                "speaking" => Mode.Speaking,
                "listening" => Mode.Listening,
                _ => null,
            };

        /// <summary>
        /// Parses the JSON payload the JS <c>onDisconnect</c> callback delivers
        /// into a Core <see cref="DisconnectionDetails"/>. The JS shape is a
        /// discriminated union keyed on <c>reason</c> (<c>"error"</c>,
        /// <c>"agent"</c>, <c>"user"</c>); each arm carries slightly different
        /// fields, all flattened here onto the single Core record.
        /// </summary>
        public static DisconnectionDetails ParseDisconnectionDetails(JObject payload)
        {
            string? reasonString = payload.Value<string>("reason");
            DisconnectionReason reason = reasonString switch
            {
                "agent" => DisconnectionReason.Agent,
                "user" => DisconnectionReason.User,
                _ => DisconnectionReason.Error,
            };

            string? message = payload.Value<string>("message");
            DisconnectionContext? context = null;
            if (payload["context"] is JObject ctxObj)
            {
                // The JS `context` is the underlying browser Event (`close`,
                // `error`); we project its discriminator + numeric code into
                // the Core record. Missing fields fall through to defaults.
                string type = ctxObj.Value<string>("type") ?? string.Empty;
                string? ctxReason = ctxObj.Value<string>("reason");
                int? code =
                    ctxObj["code"]?.Type == JTokenType.Integer ? ctxObj.Value<int>("code") : null;
                context = new DisconnectionContext(type, ctxReason, code);
            }

            int? closeCode =
                payload["closeCode"]?.Type == JTokenType.Integer
                    ? payload.Value<int>("closeCode")
                    : null;
            string? closeReason = payload.Value<string>("closeReason");

            return new DisconnectionDetails(reason, message, context, closeCode, closeReason);
        }

        /// <summary>
        /// Reads a JS-side <c>FormatConfig</c> ({ <c>format</c>, <c>sampleRate</c> })
        /// via a raw <see cref="JObject"/> and projects it onto the Core record.
        /// Throws if the property is missing — the JS SDK guarantees both fields
        /// on connection establishment, so a missing one is a contract violation
        /// worth surfacing.
        /// </summary>
        public static FormatConfig ParseFormatConfig(JObject payload)
        {
            string format =
                payload.Value<string>("format")
                ?? throw new InvalidOperationException(
                    "FormatConfig.format missing from JS-side connection state."
                );
            int sampleRate = payload.Value<int>("sampleRate");
            return new FormatConfig(format, sampleRate);
        }
    }
}

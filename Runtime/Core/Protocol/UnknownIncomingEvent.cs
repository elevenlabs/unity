#nullable enable

namespace ElevenLabs.Protocol
{
    /// <summary>
    /// Catch-all <see cref="IncomingSocketEvent"/> emitted by
    /// <see cref="IncomingSocketEventConverter"/> when the wire <c>type</c>
    /// discriminator doesn't match any generated payload. Routes the
    /// <see cref="IncomingEventDispatcher.OnUnhandled"/> arm so server-added
    /// event types ahead of an SDK refresh stay observable rather than
    /// raising as errors.
    /// </summary>
    /// <remarks>
    /// Hand-written, intentionally not under codegen — the converter generator
    /// references this type from its <c>_ =&gt;</c> fall-through arm.
    /// Declared as a regular <c>class</c> (not a <c>record</c>) because
    /// <see cref="IncomingSocketEvent"/> is a class, and C# disallows records
    /// inheriting from non-record bases.
    /// </remarks>
    public sealed class UnknownIncomingEvent : IncomingSocketEvent
    {
        /// <summary>
        /// The unknown <c>type</c> discriminator string, or <c>null</c> when
        /// the payload had no <c>type</c> field at all.
        /// </summary>
        public string? Type { get; }

        /// <summary>The raw JSON object as a compact string for diagnostics.</summary>
        public string RawJson { get; }

        public UnknownIncomingEvent(string? type, string rawJson)
        {
            Type = type;
            RawJson = rawJson;
        }
    }
}

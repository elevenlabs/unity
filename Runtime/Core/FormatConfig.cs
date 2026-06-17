#nullable enable

namespace ElevenLabs.Agents
{
    /// <summary>Audio sample format reported by a connection (input or output).</summary>
    /// <param name="Format">
    /// Codec name. Currently <c>"pcm"</c> or <c>"ulaw"</c>, matching
    /// <c>FormatConfig</c> from <c>@elevenlabs/client</c>.
    /// </param>
    /// <param name="SampleRate">Sample rate in Hz (e.g. 16000, 24000).</param>
    public sealed record FormatConfig(string Format, int SampleRate);
}

#nullable enable

namespace ElevenLabs.Agents
{
    /// <summary>Conversation mode: who currently holds the audio floor.</summary>
    /// <remarks>
    /// Mirrors <c>Mode</c> from <c>@elevenlabs/client</c>. <see cref="Listening"/>
    /// means the user is expected to speak (or is speaking); <see cref="Speaking"/>
    /// means the agent is producing audio.
    /// </remarks>
    public enum Mode
    {
        /// <summary>Agent is producing audio output.</summary>
        Speaking,

        /// <summary>Agent is awaiting user input.</summary>
        Listening,
    }
}

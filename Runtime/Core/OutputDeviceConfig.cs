#nullable enable

namespace ElevenLabs.Agents
{
    /// <summary>Output device selection for <see cref="IOutputController.SetDevice"/>.</summary>
    /// <remarks>
    /// Mirrors <c>OutputDeviceConfig</c> from <c>@elevenlabs/client</c>. A
    /// <c>null</c> field means "use default / no change".
    /// </remarks>
    /// <param name="OutputDeviceId">
    /// Platform-specific identifier for a speaker / headphone sink. On WebGL
    /// this is a <c>MediaDeviceInfo.deviceId</c>.
    /// </param>
    public sealed record OutputDeviceConfig(string? OutputDeviceId = null);
}

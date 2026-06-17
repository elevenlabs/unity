#nullable enable

namespace ElevenLabs.Agents
{
    /// <summary>Input device selection for <see cref="IInputController.SetDevice"/>.</summary>
    /// <remarks>
    /// Mirrors <c>InputDeviceConfig</c> from <c>@elevenlabs/client</c>. Fields
    /// are optional so callers can update just the bits they care about; an
    /// instance with every field <c>null</c> means "use defaults / no change".
    /// </remarks>
    /// <param name="InputDeviceId">
    /// Platform-specific identifier for a microphone. On WebGL this is a
    /// <c>MediaDeviceInfo.deviceId</c>; on native it identifies a Unity
    /// microphone device.
    /// </param>
    /// <param name="PreferHeadphonesForIosDevices">
    /// iOS-only hint that mirrors the JS SDK's option of the same name. Ignored
    /// on every other platform.
    /// </param>
    public sealed record InputDeviceConfig(
        string? InputDeviceId = null,
        bool? PreferHeadphonesForIosDevices = null
    );
}

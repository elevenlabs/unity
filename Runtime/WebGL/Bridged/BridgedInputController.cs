#nullable enable

using ElevenLabs.Agents;
using UnityEngine;

namespace ElevenLabs.WebGL.Bridged
{
    /// <summary>
    /// <see cref="IInputController"/> implementation backed by an
    /// <c>@elevenlabs/client</c> <c>MediaDeviceInput</c> handle. Each method
    /// is a 1:1 mapping onto the JS instance method of the same name.
    /// </summary>
    /// <remarks>
    /// In WebGL default-mode the audio bytes never cross the bridge — they
    /// flow JS-internal between the input and the connection via
    /// <c>attachInputToConnection</c>. This wrapper only carries the control
    /// surface (mute, device-switch, level, frequency-data) over the bridge.
    /// </remarks>
    internal sealed class BridgedInputController : IInputController
    {
        private readonly IJsObject _input;
        private bool _disposed;

        public bool IsMuted => _input.Get<bool>("isMuted");

        /// <summary>
        /// Wraps an already-created JS <c>MediaDeviceInput</c> handle. Typed
        /// against <see cref="IJsObject"/> so router-level tests can substitute
        /// a stub without going through the WebGL primitives.
        /// </summary>
        internal BridgedInputController(IJsObject input)
        {
            _input = input ?? throw new System.ArgumentNullException(nameof(input));
        }

        public async Awaitable Close()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await _input.CallAsync("close");
            }
            finally
            {
                _input.Dispose();
            }
        }

        public Awaitable SetDevice(InputDeviceConfig? config = null, FormatConfig? format = null) =>
            // Passes nullable args verbatim — Newtonsoft serialises `null` as
            // JSON null which the JS side treats as "use the current value".
            _input.CallAsync("setDevice", config, format);

        public Awaitable SetMuted(bool isMuted) => _input.CallAsync("setMuted", isMuted);

        public float GetVolume() => _input.Call<float>("getVolume");

        public void GetByteFrequencyData(byte[] buffer)
        {
            // For v0.1 the JS side returns a fresh Uint8Array per call, marshalled
            // as a JSON number array. The buffer length determines the band count.
            byte[]? remote = _input.Call<byte[]>("getByteFrequencyData", buffer.Length);
            if (remote == null)
                return;
            int n = remote.Length < buffer.Length ? remote.Length : buffer.Length;
            System.Array.Copy(remote, buffer, n);
        }
    }
}

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

#pragma warning disable CS0067 // Never fired — audio bytes never cross the bridge; attachInputToConnection wires the JS-side input straight into the JS-side connection. Required by IInputController.
        public event System.Action<byte[]>? AudioChunkAvailable;
#pragma warning restore CS0067

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
            // Known broken: the SDK's MediaDeviceInput exposes
            // `getByteFrequencyData(buffer: Uint8Array): void`, not the
            // length-in/array-out shape this wrapper calls with. The JS side
            // throws and Newtonsoft decodes the error string as
            // default(byte[]) → null → no-op + zero-fill. Same pre-existing
            // gap as `BridgedOutputController.GetByteFrequencyData`; fixing
            // it cleanly needs a JS-side adapter that allocates a Uint8Array,
            // calls the SDK's setter, and returns Array.from(buf). Deferred
            // alongside the WebRTC-arm work — see the step 6 notes in
            // Docs~/plans/output-audio-source.md.
            byte[]? remote = _input.Call<byte[]>("getByteFrequencyData", buffer.Length);
            if (remote == null)
                return;
            int n = remote.Length < buffer.Length ? remote.Length : buffer.Length;
            System.Array.Copy(remote, buffer, n);
        }
    }
}

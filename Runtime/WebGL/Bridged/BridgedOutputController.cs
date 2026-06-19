#nullable enable

using ElevenLabs.Agents;
using UnityEngine;

namespace ElevenLabs.WebGL.Bridged
{
    /// <summary>
    /// <see cref="IOutputController"/> implementation backed by an
    /// <c>@elevenlabs/client</c> <c>MediaDeviceOutput</c> handle. Each method
    /// is a 1:1 mapping onto the JS instance method of the same name.
    /// </summary>
    /// <remarks>
    /// In WebGL default-mode the audio bytes never cross the bridge — they
    /// flow JS-internal from the connection into the output via
    /// <c>attachConnectionToOutput</c>. This wrapper only carries the control
    /// surface (volume, interrupt, level, frequency-data) over the bridge.
    /// </remarks>
    internal sealed class BridgedOutputController : IOutputController
    {
        private readonly JsObject _output;
        private bool _disposed;

        internal BridgedOutputController(JsObject output)
        {
            _output = output ?? throw new System.ArgumentNullException(nameof(output));
        }

        public async Awaitable Close()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await _output.CallAsync("close");
            }
            finally
            {
                _output.Dispose();
            }
        }

        public Awaitable SetDevice(
            OutputDeviceConfig? config = null,
            FormatConfig? format = null
        ) => _output.CallAsync("setDevice", config, format);

        public void SetVolume(float volume) => _output.Call("setVolume", volume);

        public void Interrupt(int? resetDurationMs = null)
        {
            // SDK accepts an optional `{ resetDurationMs?: number }` options
            // object; pass null when the caller didn't supply a duration so
            // the JS side picks its default.
            if (resetDurationMs.HasValue)
            {
                _output.Call("interrupt", new { resetDurationMs = resetDurationMs.Value });
            }
            else
            {
                _output.Call("interrupt");
            }
        }

        public float GetVolume() => _output.Call<float>("getVolume");

        public void GetByteFrequencyData(byte[] buffer)
        {
            byte[]? remote = _output.Call<byte[]>("getByteFrequencyData", buffer.Length);
            if (remote == null)
                return;
            int n = remote.Length < buffer.Length ? remote.Length : buffer.Length;
            System.Array.Copy(remote, buffer, n);
        }
    }
}

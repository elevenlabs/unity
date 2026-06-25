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
        private readonly IJsObject _output;
        private bool _disposed;

        /// <summary>
        /// Wraps an already-created JS <c>MediaDeviceOutput</c> handle. Typed
        /// against <see cref="IJsObject"/> so router-level tests can substitute
        /// a stub without going through the WebGL primitives.
        /// </summary>
        internal BridgedOutputController(IJsObject output)
        {
            _output = output ?? throw new System.ArgumentNullException(nameof(output));
        }

        // Default-mode WebGL plays audio entirely JS-side via
        // attachConnectionToOutput; the bridge strips audio_base_64 before
        // the event reaches C# (see Bridge~/src/connection/audio-glue.ts).
        // Conversation.HandleAudioResponse therefore calls this with an
        // empty payload — nothing to forward.
        public void PushAudio(byte[] pcm) { }

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
            // Known broken on the WebRTC arm: the SDK's MediaDeviceOutput
            // exposes `getByteFrequencyData(buffer: Uint8Array): void` (the
            // upstream `OutputController` contract), not length-in/array-out.
            // Calling it with a number throws JS-side and Newtonsoft decodes
            // the error string as default(byte[]) → null → no-op + zero-fill.
            // The WebSocket arm uses `WebAudioBackedOutput` which talks to
            // our own JS sink and DOES honour the length-in/array-out shape.
            // The fix for WebRTC needs a JS-side adapter that allocates a
            // Uint8Array, calls the SDK's setter, and returns Array.from(buf);
            // deferred until the WebRTC arm itself ships (see v0.3 in
            // Docs~/plans/v0.1-parity.md and the step 6 notes in
            // Docs~/plans/output-audio-source.md).
            byte[]? remote = _output.Call<byte[]>("getByteFrequencyData", buffer.Length);
            if (remote == null)
                return;
            int n = remote.Length < buffer.Length ? remote.Length : buffer.Length;
            System.Array.Copy(remote, buffer, n);
        }
    }
}

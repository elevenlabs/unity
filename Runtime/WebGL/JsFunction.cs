using System;
using System.Threading.Tasks;

namespace ElevenLabs.WebGL
{
    /// <summary>Handle to a JavaScript function reference returned from a bridge call.</summary>
    public sealed class JsFunction : IDisposable, IAsyncDisposable
    {
        /// <summary>Opaque integer handle that identifies the JS-side function registry entry.</summary>
        public int Handle { get; }

        internal JsFunction(int handle)
        {
            Handle = handle;
        }

        /// <summary>Releases the JS-side function registry entry. Full implementation lands in task 3.5.</summary>
        public void Dispose() { }

        /// <inheritdoc cref="Dispose"/>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}

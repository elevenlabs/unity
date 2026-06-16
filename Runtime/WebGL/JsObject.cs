using System;
using System.Threading.Tasks;

namespace ElevenLabs.WebGL
{
    /// <summary>Handle to a remote JavaScript object whose lifetime is owned by C#.</summary>
    public sealed class JsObject : IDisposable, IAsyncDisposable
    {
        /// <summary>Opaque integer handle that identifies the JS-side registry entry.</summary>
        public int Handle { get; }

        internal JsObject(int handle)
        {
            Handle = handle;
        }

        /// <summary>Releases the JS-side registry entry. Full implementation lands in task 3.5.</summary>
        public void Dispose() { }

        /// <inheritdoc cref="Dispose"/>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}

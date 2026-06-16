using System;

namespace ElevenLabs.WebGL
{
    /// <summary>A C# delegate exposed to JavaScript as a callable function reference.</summary>
    /// <remarks>
    /// JS never receives a return value from a <see cref="BridgeCallback"/> invocation — it is
    /// fire-and-forget. Dispose removes the C# delegate from the callback registry; subsequent
    /// JS invocations arrive, miss the lookup, and no-op silently.
    /// </remarks>
    public sealed class BridgeCallback : IDisposable
    {
        /// <summary>Opaque integer handle passed to JS inside a <c>{"$cb": handle}</c> marker.</summary>
        public int Handle { get; }

        internal BridgeCallback(int handle)
        {
            Handle = handle;
        }

        /// <summary>Removes the C# delegate from the callback registry. Full implementation lands in task 3.6.</summary>
        public void Dispose() { }
    }
}

using System.Threading;

namespace ElevenLabs.WebGL
{
    /// <summary>Monotonic integer ID generator shared by all bridge registries.</summary>
    internal static class BridgeIdGenerator
    {
        private static int _lastId;

        /// <summary>Returns the next unique ID. IDs start from 1 and increase monotonically.</summary>
        public static int Next() => Interlocked.Increment(ref _lastId);
    }
}

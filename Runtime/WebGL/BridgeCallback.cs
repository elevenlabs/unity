using System;
using ElevenLabs.WebGL.Internal;
using Newtonsoft.Json;

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
        private bool _disposed;

        /// <summary>Opaque integer handle passed to JS inside a <c>{"$cb": handle}</c> marker.</summary>
        public int Handle { get; }

        internal BridgeCallback(int handle)
        {
            Handle = handle;
        }

        /// <summary>
        /// Wraps a C# delegate in a <see cref="BridgeCallback"/> that JS can invoke.
        /// The delegate receives the raw JSON payload string the JS bridge produced via
        /// <c>JSON.stringify(arg)</c>. Choose this overload when the handler wants to parse
        /// the payload itself (e.g. a JSON object that deserialises to a typed event).
        /// </summary>
        /// <param name="handler">The delegate to invoke when JS calls the callback.</param>
        /// <returns>A new <see cref="BridgeCallback"/> holding the registered handle.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="handler"/> is null.</exception>
        public static BridgeCallback Wrap(Action<string> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            int handle = CallbackRegistry.Register(handler);
            return new BridgeCallback(handle);
        }

        /// <summary>
        /// Typed convenience overload. The JSON payload from JS is deserialised to
        /// <typeparamref name="T"/> before the delegate is invoked. Pick this when the JS side
        /// genuinely passes a value of type <typeparamref name="T"/> (e.g. a primitive number
        /// or a primitive string); for callbacks that fire with a structured object, prefer
        /// the untyped <see cref="Wrap(Action{string})"/> overload and parse with the right
        /// typed target inside the handler.
        /// </summary>
        /// <typeparam name="T">The type the JSON payload is deserialised to before invoking the delegate.</typeparam>
        /// <param name="handler">The delegate to invoke when JS calls the callback.</param>
        /// <returns>A new <see cref="BridgeCallback"/> holding the registered handle.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="handler"/> is null.</exception>
        public static BridgeCallback Wrap<T>(Action<T> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            return Wrap(payload => handler(JsonConvert.DeserializeObject<T>(payload)));
        }

        /// <summary>
        /// Removes the C# delegate from the callback registry. Safe to call multiple times —
        /// subsequent calls are no-ops. JS invocations arriving after dispose miss the lookup
        /// and no-op silently.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CallbackRegistry.TryRemove(Handle);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ElevenLabs.WebGL.Internal
{
    /// <summary>
    /// Tracks in-flight async bridge operations. Each entry maps a promise ID
    /// (allocated via <see cref="BridgeIdGenerator"/>) to the
    /// <see cref="AwaitableCompletionSource{TResult}"/> the C# caller is
    /// awaiting on. JS settles the operation by routing through
    /// <c>BridgeStaticCallbacks.OnSettleFromJs</c>, which forwards the payload
    /// here.
    /// </summary>
    /// <remarks>
    /// Settle is a one-shot terminal operation: the entry is removed atomically
    /// under a lock before the completion source is signalled. A late settle
    /// (e.g. a second SendMessage arriving after the awaiting caller cancelled)
    /// finds the entry missing and no-ops silently rather than double-completing
    /// the source — this is the "first-wins, lookup-then-remove" rule from the
    /// bridge plan.
    /// </remarks>
    internal static class PromiseRegistry
    {
        private static readonly Dictionary<int, AwaitableCompletionSource<string>> _pending = new();
        private static readonly object _lock = new();

        /// <summary>
        /// Registers a completion source and returns the freshly allocated promise ID
        /// to send across the DllImport boundary.
        /// </summary>
        public static int Register(AwaitableCompletionSource<string> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            int promiseId = BridgeIdGenerator.Next();
            lock (_lock)
            {
                _pending.Add(promiseId, source);
            }
            return promiseId;
        }

        /// <summary>
        /// Settles a pending promise. Returns <c>true</c> if the entry existed and
        /// the source was signalled; <c>false</c> if the ID was unknown (already
        /// settled, cancelled, or never registered).
        /// </summary>
        public static bool TrySettle(int promiseId, bool ok, string payload)
        {
            AwaitableCompletionSource<string> source;
            lock (_lock)
            {
                if (!_pending.Remove(promiseId, out source))
                {
                    return false;
                }
            }

            if (ok)
            {
                source.SetResult(payload);
            }
            else
            {
                source.SetException(new BridgeException(payload ?? string.Empty));
            }
            return true;
        }

        /// <summary>Returns the number of entries currently awaiting settle (testing aid).</summary>
        internal static int Count
        {
            get
            {
                lock (_lock)
                {
                    return _pending.Count;
                }
            }
        }

        /// <summary>Drops every pending entry. Test-only — never call from production code.</summary>
        internal static void ResetForTests()
        {
            lock (_lock)
            {
                _pending.Clear();
            }
        }
    }

    /// <summary>
    /// Tracks C# delegates that JS holds as callable function references. Each
    /// entry maps a callback handle (allocated via <see cref="BridgeIdGenerator"/>)
    /// to the wrapped <see cref="Action{T}"/>. JS invokes the callback by routing
    /// through <c>BridgeStaticCallbacks.OnCallbackInvokedFromJs</c>, which
    /// forwards the payload here.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="PromiseRegistry"/>, callbacks are multi-shot — dispatch
    /// is a non-removing lookup; only <see cref="TryRemove"/> takes the entry out
    /// of the registry. Dispatch arriving after a remove (re-entrant dispose,
    /// late-firing JS listener after the C# side disposed the wrapper) misses
    /// the lookup and no-ops silently. Removal is idempotent — a double-remove
    /// returns <c>false</c> the second time rather than throwing.
    /// </remarks>
    internal static class CallbackRegistry
    {
        private static readonly Dictionary<int, Action<string>> _handlers = new();
        private static readonly object _lock = new();

        /// <summary>
        /// Registers a delegate and returns the freshly allocated callback handle
        /// to pass to JS in a <c>{$cb}</c> marker.
        /// </summary>
        public static int Register(Action<string> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            int handle = BridgeIdGenerator.Next();
            lock (_lock)
            {
                _handlers.Add(handle, handler);
            }
            return handle;
        }

        /// <summary>
        /// Invokes the registered handler for <paramref name="handle"/>, if any.
        /// Returns <c>true</c> when the handler was found and invoked; <c>false</c>
        /// when the handle was unknown (already disposed, or never registered).
        /// </summary>
        /// <remarks>
        /// The lookup is performed under a lock, but the handler is invoked
        /// outside the lock so a re-entrant bridge call from inside the handler
        /// does not deadlock against itself.
        /// </remarks>
        public static bool TryDispatch(int handle, string payload)
        {
            Action<string> handler;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(handle, out handler))
                {
                    return false;
                }
            }

            handler(payload);
            return true;
        }

        /// <summary>
        /// Removes the entry for <paramref name="handle"/>. Returns <c>true</c> if
        /// the entry existed; <c>false</c> if the handle was unknown (already
        /// removed, or never registered). Double-remove is idempotent.
        /// </summary>
        public static bool TryRemove(int handle)
        {
            lock (_lock)
            {
                return _handlers.Remove(handle);
            }
        }

        /// <summary>Returns the number of live registered handlers (testing aid).</summary>
        internal static int Count
        {
            get
            {
                lock (_lock)
                {
                    return _handlers.Count;
                }
            }
        }

        /// <summary>Drops every registered handler. Test-only — never call from production code.</summary>
        internal static void ResetForTests()
        {
            lock (_lock)
            {
                _handlers.Clear();
            }
        }
    }
}

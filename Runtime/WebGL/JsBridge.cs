using System;
using System.Runtime.InteropServices;
using ElevenLabs.WebGL.Internal;
using UnityEngine;

namespace ElevenLabs.WebGL
{
    /// <summary>
    /// Entry point for invoking JavaScript-registered factories from C#. Every
    /// interaction with the JS side begins here — a factory call returns a
    /// <see cref="JsObject"/> or <see cref="JsFunction"/> handle that the caller
    /// then uses to drive further JS operations.
    /// </summary>
    public static class JsBridge
    {
        private const string ErrorPrefix = "!err:";

        /// <summary>
        /// Invokes a named JS factory asynchronously. The factory must have been
        /// registered on the JS side via <c>EL_RegisterFactory</c> before the first
        /// call. The return type <typeparamref name="T"/> determines the
        /// <see cref="BridgeReturnShape"/> code sent to the JS dispatcher — pass
        /// <see cref="JsObject"/> or <see cref="JsFunction"/> to receive a handle,
        /// any other type for a plain JSON-decoded value.
        /// </summary>
        /// <typeparam name="T">
        /// Expected return type. Drives the return-shape code automatically.
        /// </typeparam>
        /// <param name="factoryName">Name of the pre-registered JS factory.</param>
        /// <param name="args">
        /// Arguments forwarded to the factory. <see cref="JsObject"/>,
        /// <see cref="JsFunction"/>, and <see cref="BridgeCallback"/> instances are
        /// encoded as typed markers; all other values are JSON-serialised.
        /// </param>
        public static async Awaitable<T> InvokeFactoryAsync<T>(
            string factoryName,
            params object[] args
        )
        {
            var source = new AwaitableCompletionSource<string>();
            int promiseId = PromiseRegistry.Register(source);
            string argsJson = BridgeArgEncoder.Encode(args);
            BridgeReturnShape shape = BridgeValueDecoder.GetShapeFor<T>();
            try
            {
                ElevenLabsBridgeNative.EL_InvokeFactoryAsync(
                    factoryName,
                    argsJson,
                    (int)shape,
                    promiseId
                );
            }
            catch (Exception ex)
            {
                // The DllImport throws on non-WebGL platforms (or on a misconfigured
                // WebGL build). Settle the promise so the registry stays clean and the
                // awaiting caller receives a BridgeException rather than a dangling
                // promise that would never resolve.
                PromiseRegistry.TrySettle(promiseId, ok: false, ex.Message);
            }
            string rawResult = await source.Awaitable;
            return BridgeValueDecoder.Decode<T>(rawResult);
        }

        /// <summary>
        /// Invokes a named JS factory synchronously. The factory must return a
        /// non-Promise value; a factory that returns a Promise will have its Promise
        /// object JSON-encoded rather than awaited, which is almost certainly wrong.
        /// Prefer <see cref="InvokeFactoryAsync{T}"/> unless you are certain the
        /// factory is synchronous.
        /// </summary>
        /// <typeparam name="T">
        /// Expected return type. Drives the return-shape code automatically.
        /// </typeparam>
        /// <param name="factoryName">Name of the pre-registered JS factory.</param>
        /// <param name="args">Arguments forwarded to the factory.</param>
        /// <exception cref="BridgeException">
        /// Thrown when the JS factory throws an error.
        /// </exception>
        public static T InvokeFactory<T>(string factoryName, params object[] args)
        {
            string argsJson = BridgeArgEncoder.Encode(args);
            BridgeReturnShape shape = BridgeValueDecoder.GetShapeFor<T>();
            IntPtr ptr = ElevenLabsBridgeNative.EL_InvokeFactorySync(
                factoryName,
                argsJson,
                (int)shape
            );
            return DecodeSyncResult<T>(ptr);
        }

        /// <summary>
        /// Reads, frees, and decodes a heap-allocated UTF-8 string pointer returned
        /// by a sync DllImport entry point. A <c>!err:&lt;message&gt;</c> sentinel
        /// prefix causes a <see cref="BridgeException"/> to be thrown. Exposed
        /// internally so <see cref="JsObject"/> and <see cref="JsFunction"/> can
        /// reuse the same decode path for their own sync entry points.
        /// </summary>
        internal static T DecodeSyncResult<T>(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return default;
            string json = Marshal.PtrToStringUTF8(ptr);
            ElevenLabsBridgeNative.EL_Free(ptr);
            if (json != null && json.StartsWith(ErrorPrefix, StringComparison.Ordinal))
                throw new BridgeException(json.Substring(ErrorPrefix.Length));
            return BridgeValueDecoder.Decode<T>(json);
        }
    }
}

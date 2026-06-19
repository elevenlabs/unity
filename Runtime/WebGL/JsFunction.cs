using System;
using System.Threading.Tasks;
using ElevenLabs.WebGL.Internal;
using UnityEngine;

namespace ElevenLabs.WebGL
{
    /// <summary>Handle to a JavaScript function reference returned from a bridge call.</summary>
    public sealed class JsFunction : IJsFunction, IAsyncDisposable
    {
        private bool _disposed;

        /// <summary>Opaque integer handle that identifies the JS-side function registry entry.</summary>
        public int Handle { get; }

        internal JsFunction(int handle)
        {
            Handle = handle;
        }

        ~JsFunction()
        {
            if (!_disposed)
                BridgeLog.Warn(
                    $"JsFunction handle {Handle} was garbage collected without being disposed. "
                        + "Call Dispose() when done with the function."
                );
        }

        /// <summary>
        /// Invokes the remote JS function asynchronously. The return type
        /// <typeparamref name="T"/> determines the <see cref="BridgeReturnShape"/> sent to the
        /// JS dispatcher — pass <see cref="JsObject"/> or <see cref="JsFunction"/> to receive a
        /// handle, any other type for a plain JSON-decoded value.
        /// </summary>
        /// <typeparam name="T">Expected return type. Drives the return-shape code automatically.</typeparam>
        /// <param name="args">
        /// Arguments forwarded to the function. <see cref="JsObject"/>,
        /// <see cref="JsFunction"/>, and <see cref="BridgeCallback"/> instances are
        /// encoded as typed markers; all other values are JSON-serialised.
        /// </param>
        /// <returns>An awaitable that resolves to the decoded return value.</returns>
        public async Awaitable<T> CallAsync<T>(params object[] args)
        {
            var source = new AwaitableCompletionSource<string>();
            int promiseId = PromiseRegistry.Register(source);
            string argsJson = BridgeArgEncoder.Encode(args);
            BridgeReturnShape shape = BridgeValueDecoder.GetShapeFor<T>();
            try
            {
                ElevenLabsBridgeNative.EL_FunctionCallAsync(
                    Handle,
                    argsJson,
                    (int)shape,
                    promiseId
                );
            }
            catch (Exception ex)
            {
                PromiseRegistry.TrySettle(promiseId, ok: false, ex.Message);
            }
            string rawResult = await source.Awaitable;
            return BridgeValueDecoder.Decode<T>(rawResult);
        }

        /// <summary>
        /// Invokes the remote JS function asynchronously, discarding the return value.
        /// </summary>
        /// <param name="args">Arguments forwarded to the function.</param>
        public async Awaitable CallAsync(params object[] args)
        {
            var source = new AwaitableCompletionSource<string>();
            int promiseId = PromiseRegistry.Register(source);
            string argsJson = BridgeArgEncoder.Encode(args);
            try
            {
                ElevenLabsBridgeNative.EL_FunctionCallAsync(
                    Handle,
                    argsJson,
                    (int)BridgeReturnShape.Void,
                    promiseId
                );
            }
            catch (Exception ex)
            {
                PromiseRegistry.TrySettle(promiseId, ok: false, ex.Message);
            }
            await source.Awaitable;
        }

        /// <summary>
        /// Invokes the remote JS function synchronously.
        /// </summary>
        /// <typeparam name="T">Expected return type. Drives the return-shape code automatically.</typeparam>
        /// <param name="args">Arguments forwarded to the function.</param>
        /// <returns>The decoded return value.</returns>
        /// <exception cref="BridgeException">Thrown when the JS function throws an error.</exception>
        public T Call<T>(params object[] args)
        {
            string argsJson = BridgeArgEncoder.Encode(args);
            BridgeReturnShape shape = BridgeValueDecoder.GetShapeFor<T>();
            IntPtr ptr = ElevenLabsBridgeNative.EL_FunctionCallSync(Handle, argsJson, (int)shape);
            return JsBridge.DecodeSyncResult<T>(ptr);
        }

        /// <summary>
        /// Invokes the remote JS function synchronously, discarding the return value.
        /// </summary>
        /// <param name="args">Arguments forwarded to the function.</param>
        /// <exception cref="BridgeException">Thrown when the JS function throws an error.</exception>
        public void Call(params object[] args)
        {
            string argsJson = BridgeArgEncoder.Encode(args);
            IntPtr ptr = ElevenLabsBridgeNative.EL_FunctionCallSync(
                Handle,
                argsJson,
                (int)BridgeReturnShape.Void
            );
            JsBridge.DecodeSyncResult<object>(ptr);
        }

        /// <summary>
        /// Releases the JS-side function registry entry. Safe to call multiple times — subsequent
        /// calls are no-ops. The finalizer logs a warning if this method was never called.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            GC.SuppressFinalize(this);
            try
            {
                ElevenLabsBridgeNative.EL_FunctionRelease(Handle);
            }
            catch (PlatformNotSupportedException) { }
        }

        /// <inheritdoc cref="Dispose"/>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}

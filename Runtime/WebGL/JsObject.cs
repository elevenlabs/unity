using System;
using System.Threading.Tasks;
using ElevenLabs.WebGL.Internal;
using UnityEngine;

namespace ElevenLabs.WebGL
{
    /// <summary>Handle to a remote JavaScript object whose lifetime is owned by C#.</summary>
    public sealed class JsObject : IJsObject, IAsyncDisposable
    {
        private bool _disposed;

        /// <summary>Opaque integer handle that identifies the JS-side registry entry.</summary>
        public int Handle { get; }

        internal JsObject(int handle)
        {
            Handle = handle;
        }

        ~JsObject()
        {
            if (!_disposed)
                BridgeLog.Warn(
                    $"JsObject handle {Handle} was garbage collected without being disposed. "
                        + "Call Dispose() when done with the object."
                );
        }

        /// <summary>
        /// Calls a named method on the remote JS object asynchronously. The return type
        /// <typeparamref name="T"/> determines the <see cref="BridgeReturnShape"/> sent to the
        /// JS dispatcher — pass <see cref="JsObject"/> or <see cref="JsFunction"/> to receive a
        /// handle, any other type for a plain JSON-decoded value.
        /// </summary>
        /// <typeparam name="T">Expected return type. Drives the return-shape code automatically.</typeparam>
        /// <param name="method">Name of the method to invoke on the remote object.</param>
        /// <param name="args">
        /// Arguments forwarded to the method. <see cref="JsObject"/>,
        /// <see cref="JsFunction"/>, and <see cref="BridgeCallback"/> instances are
        /// encoded as typed markers; all other values are JSON-serialised.
        /// </param>
        /// <returns>An awaitable that resolves to the decoded return value.</returns>
        public async Awaitable<T> CallAsync<T>(string method, params object[] args)
        {
            var source = new AwaitableCompletionSource<string>();
            int promiseId = PromiseRegistry.Register(source);
            string argsJson = BridgeArgEncoder.Encode(args);
            BridgeReturnShape shape = BridgeValueDecoder.GetShapeFor<T>();
            try
            {
                ElevenLabsBridgeNative.EL_ObjectCallAsync(
                    Handle,
                    method,
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
        /// Calls a named method on the remote JS object asynchronously, discarding the return value.
        /// </summary>
        /// <param name="method">Name of the method to invoke on the remote object.</param>
        /// <param name="args">Arguments forwarded to the method.</param>
        public async Awaitable CallAsync(string method, params object[] args)
        {
            var source = new AwaitableCompletionSource<string>();
            int promiseId = PromiseRegistry.Register(source);
            string argsJson = BridgeArgEncoder.Encode(args);
            try
            {
                ElevenLabsBridgeNative.EL_ObjectCallAsync(
                    Handle,
                    method,
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
        /// Calls a named method on the remote JS object synchronously.
        /// </summary>
        /// <typeparam name="T">Expected return type. Drives the return-shape code automatically.</typeparam>
        /// <param name="method">Name of the method to invoke on the remote object.</param>
        /// <param name="args">Arguments forwarded to the method.</param>
        /// <returns>The decoded return value.</returns>
        /// <exception cref="BridgeException">Thrown when the JS method throws an error.</exception>
        public T Call<T>(string method, params object[] args)
        {
            string argsJson = BridgeArgEncoder.Encode(args);
            BridgeReturnShape shape = BridgeValueDecoder.GetShapeFor<T>();
            IntPtr ptr = ElevenLabsBridgeNative.EL_ObjectCallSync(
                Handle,
                method,
                argsJson,
                (int)shape
            );
            return JsBridge.DecodeSyncResult<T>(ptr);
        }

        /// <summary>
        /// Calls a named method on the remote JS object synchronously, discarding the return value.
        /// </summary>
        /// <param name="method">Name of the method to invoke on the remote object.</param>
        /// <param name="args">Arguments forwarded to the method.</param>
        /// <exception cref="BridgeException">Thrown when the JS method throws an error.</exception>
        public void Call(string method, params object[] args)
        {
            string argsJson = BridgeArgEncoder.Encode(args);
            IntPtr ptr = ElevenLabsBridgeNative.EL_ObjectCallSync(
                Handle,
                method,
                argsJson,
                (int)BridgeReturnShape.Void
            );
            JsBridge.DecodeSyncResult<object>(ptr);
        }

        /// <summary>
        /// Reads a named property from the remote JS object synchronously. The result is always
        /// JSON-decoded (value shape); use <see cref="CallAsync{T}"/> when you need a handle.
        /// </summary>
        /// <typeparam name="T">Expected property value type.</typeparam>
        /// <param name="property">Name of the property to read.</param>
        /// <returns>The property value decoded as <typeparamref name="T"/>.</returns>
        /// <exception cref="BridgeException">Thrown when the JS property access throws an error.</exception>
        public T Get<T>(string property)
        {
            IntPtr ptr = ElevenLabsBridgeNative.EL_ObjectGet(Handle, property);
            return JsBridge.DecodeSyncResult<T>(ptr);
        }

        /// <summary>
        /// Releases the JS-side registry entry. Safe to call multiple times — subsequent calls
        /// are no-ops. The finalizer logs a warning if this method was never called.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            GC.SuppressFinalize(this);
            try
            {
                ElevenLabsBridgeNative.EL_ObjectRelease(Handle);
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

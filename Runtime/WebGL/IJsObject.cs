using System;
using UnityEngine;

namespace ElevenLabs.WebGL
{
    /// <summary>
    /// Surface the bridged wrappers under <c>ElevenLabs.WebGL.Bridged</c> consume from
    /// <see cref="JsObject"/>. Extracted so router-level tests can substitute a stub
    /// implementation without going through the WebGL <c>DllImport</c>s — the production
    /// code still uses the concrete <see cref="JsObject"/> handle returned by
    /// <see cref="JsBridge.InvokeFactoryAsync{T}"/>; the interface only narrows the
    /// dependency edge between the wrappers and the primitives layer.
    /// </summary>
    internal interface IJsObject : IDisposable
    {
        int Handle { get; }

        Awaitable<T> CallAsync<T>(string method, params object[] args);

        Awaitable CallAsync(string method, params object[] args);

        T Call<T>(string method, params object[] args);

        void Call(string method, params object[] args);

        T Get<T>(string property);
    }
}

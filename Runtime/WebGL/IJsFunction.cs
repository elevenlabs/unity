using System;
using UnityEngine;

namespace ElevenLabs.WebGL
{
    /// <summary>
    /// Surface the bridged wrappers consume from <see cref="JsFunction"/>. Currently
    /// only <c>BridgedWebSocketConnection.AttachAudioDetach</c> takes a function handle
    /// (the detach closure from <c>attachDefaultAudio</c>) — extracted so its teardown
    /// path is stubbable in Edit Mode without going through the WebGL <c>DllImport</c>s.
    /// </summary>
    internal interface IJsFunction : IDisposable
    {
        int Handle { get; }

        Awaitable<T> CallAsync<T>(params object[] args);

        Awaitable CallAsync(params object[] args);

        T Call<T>(params object[] args);

        void Call(params object[] args);
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Test double for <see cref="IJsFunction"/>. Records every invocation and
    /// dispose so router-level tests can assert that the wrapper called the
    /// detach handle exactly once before tearing it down.
    /// </summary>
    internal sealed class FakeJsFunction : IJsFunction
    {
        public int Handle { get; }
        public bool IsDisposed { get; private set; }

        public List<RecordedInvocation> Invocations { get; } = new();

        /// <summary>When set, every <c>Call</c>/<c>CallAsync</c> throws this exception.</summary>
        public Exception? ThrowOnCall { get; set; }

        /// <summary>Return value for <c>Call&lt;T&gt;</c>/<c>CallAsync&lt;T&gt;</c>.</summary>
        public object? CallReturnValue { get; set; }

        public FakeJsFunction(int handle = 0)
        {
            Handle = handle;
        }

        public Awaitable<T> CallAsync<T>(params object[] args)
        {
            Record(InvocationKind.FunctionCallAsyncResult, args, typeof(T));
            if (ThrowOnCall != null)
                throw ThrowOnCall;
            var source = new AwaitableCompletionSource<T>();
            source.SetResult(CallReturnValue is T typed ? typed : default!);
            return source.Awaitable;
        }

        public Awaitable CallAsync(params object[] args)
        {
            Record(InvocationKind.FunctionCallAsyncVoid, args, typeof(void));
            if (ThrowOnCall != null)
                throw ThrowOnCall;
            var source = new AwaitableCompletionSource();
            source.SetResult();
            return source.Awaitable;
        }

        public T Call<T>(params object[] args)
        {
            Record(InvocationKind.FunctionCallResult, args, typeof(T));
            if (ThrowOnCall != null)
                throw ThrowOnCall;
            return CallReturnValue is T typed ? typed : default!;
        }

        public void Call(params object[] args)
        {
            Record(InvocationKind.FunctionCallVoid, args, typeof(void));
            if (ThrowOnCall != null)
                throw ThrowOnCall;
        }

        public void Dispose() => IsDisposed = true;

        private void Record(InvocationKind kind, object[] args, Type resultType)
        {
            Invocations.Add(
                new RecordedInvocation
                {
                    Kind = kind,
                    Name = string.Empty,
                    Args = args,
                    ResultType = resultType,
                }
            );
        }
    }
}

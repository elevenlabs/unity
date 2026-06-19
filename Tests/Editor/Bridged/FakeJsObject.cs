#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Test double for <see cref="IJsObject"/>. Records every call / property
    /// read the wrapper issues, and lets a test configure return values or
    /// exceptions per method/property name without going through the WebGL
    /// primitives layer.
    /// </summary>
    /// <remarks>
    /// Async methods return immediately-completed <see cref="Awaitable"/>s so
    /// the wrapper's <c>await</c> resolves synchronously inside the test — the
    /// 5.5 router-level tests don't need to model JS-side latency, only that
    /// the right method name + args reach the bridge boundary.
    /// </remarks>
    internal sealed class FakeJsObject : IJsObject
    {
        public int Handle { get; }
        public bool IsDisposed { get; private set; }

        /// <summary>Ordered log of every interaction the wrapper issued.</summary>
        public List<RecordedInvocation> Invocations { get; } = new();

        /// <summary>Per-method-name return value for <c>Call&lt;T&gt;</c>/<c>CallAsync&lt;T&gt;</c>.</summary>
        public Dictionary<string, object?> CallReturnValues { get; } = new();

        /// <summary>Per-property return value for <c>Get&lt;T&gt;</c>.</summary>
        public Dictionary<string, object?> PropertyValues { get; } = new();

        /// <summary>Per-method exception override. When set, the corresponding Call/CallAsync/Get throws.</summary>
        public Dictionary<string, Exception> Throws { get; } = new();

        public FakeJsObject(int handle = 0)
        {
            Handle = handle;
        }

        public Awaitable<T> CallAsync<T>(string method, params object[] args)
        {
            Record(InvocationKind.CallAsyncResult, method, args, typeof(T));
            if (Throws.TryGetValue(method, out var ex))
                throw ex;
            return CompletedAwaitable<T>(ReadReturn<T>(method));
        }

        public Awaitable CallAsync(string method, params object[] args)
        {
            Record(InvocationKind.CallAsyncVoid, method, args, typeof(void));
            if (Throws.TryGetValue(method, out var ex))
                throw ex;
            return CompletedAwaitable();
        }

        public T Call<T>(string method, params object[] args)
        {
            Record(InvocationKind.CallResult, method, args, typeof(T));
            if (Throws.TryGetValue(method, out var ex))
                throw ex;
            return ReadReturn<T>(method);
        }

        public void Call(string method, params object[] args)
        {
            Record(InvocationKind.CallVoid, method, args, typeof(void));
            if (Throws.TryGetValue(method, out var ex))
                throw ex;
        }

        public T Get<T>(string property)
        {
            Record(InvocationKind.Get, property, Array.Empty<object>(), typeof(T));
            if (Throws.TryGetValue(property, out var ex))
                throw ex;
            if (PropertyValues.TryGetValue(property, out var value))
                return value is T typed ? typed : default!;
            return default!;
        }

        public void Dispose() => IsDisposed = true;

        /// <summary>Convenience: the names of every Call/CallAsync the wrapper issued, in order.</summary>
        public IEnumerable<string> CalledMethods()
        {
            foreach (var entry in Invocations)
                if (
                    entry.Kind == InvocationKind.CallVoid
                    || entry.Kind == InvocationKind.CallResult
                    || entry.Kind == InvocationKind.CallAsyncVoid
                    || entry.Kind == InvocationKind.CallAsyncResult
                )
                    yield return entry.Name;
        }

        private void Record(InvocationKind kind, string name, object[] args, Type resultType)
        {
            Invocations.Add(
                new RecordedInvocation
                {
                    Kind = kind,
                    Name = name,
                    Args = args,
                    ResultType = resultType,
                }
            );
        }

        private T ReadReturn<T>(string method)
        {
            if (CallReturnValues.TryGetValue(method, out var value))
                return value is T typed ? typed : default!;
            return default!;
        }

        private static Awaitable<T> CompletedAwaitable<T>(T value)
        {
            var source = new AwaitableCompletionSource<T>();
            source.SetResult(value);
            return source.Awaitable;
        }

        private static Awaitable CompletedAwaitable()
        {
            var source = new AwaitableCompletionSource();
            source.SetResult();
            return source.Awaitable;
        }
    }

    /// <summary>Per-invocation record captured by <see cref="FakeJsObject"/> / <see cref="FakeJsFunction"/>.</summary>
    internal sealed class RecordedInvocation
    {
        public InvocationKind Kind;
        public string Name = string.Empty;
        public object[] Args = Array.Empty<object>();
        public Type ResultType = typeof(void);
    }

    internal enum InvocationKind
    {
        CallVoid,
        CallResult,
        CallAsyncVoid,
        CallAsyncResult,
        Get,
        FunctionCallVoid,
        FunctionCallResult,
        FunctionCallAsyncVoid,
        FunctionCallAsyncResult,
    }
}

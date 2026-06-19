#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ElevenLabs.Agents
{
    /// <summary>
    /// Thrown by a client-tool handler when it wants to surface a specific
    /// <see cref="ErrorType"/> back to the server (e.g. <c>"timeout"</c>,
    /// <c>"unauthorized"</c>). Plain <see cref="Exception"/>s also turn into
    /// <c>is_error=true</c> results — this type is only needed when the
    /// handler wants to influence the <c>error_type</c> field on the wire.
    /// </summary>
    public sealed class ClientToolException : Exception
    {
        /// <summary>
        /// Symbolic code mapped to <c>client_tool_result.error_type</c>.
        /// Free-form; agents commonly inspect this in their server prompts.
        /// </summary>
        public string? ErrorType { get; }

        public ClientToolException(string message, string? errorType = null)
            : base(message)
        {
            ErrorType = errorType;
        }
    }

    // Type-erased dispatcher stored in Conversation's tool table. The
    // generic-typed RegisterTool<TParams, TResult> overloads wrap user
    // handlers behind this delegate so the per-call deserialisation /
    // serialisation lives next to the handler invocation — and the message
    // router doesn't have to be generic over TParams / TResult.
    internal delegate Awaitable<string> ClientToolDispatcher(
        Dictionary<string, dynamic>? parameters
    );
}

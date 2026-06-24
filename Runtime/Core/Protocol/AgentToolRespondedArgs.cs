#nullable enable

namespace ElevenLabs.Protocol
{
    /// <summary>
    /// Unified args for the agent-tool-response surface. Mirrors the JS SDK's
    /// <c>onAgentToolResponse</c>, which fires for both the
    /// <see cref="AgentToolResponse"/> wire event and its
    /// <see cref="AgentToolResponseFullPayload"/> variant — callers see one
    /// record regardless of which the server emitted. <see cref="FullToolResult"/>
    /// and <see cref="Truncated"/> are only populated for the full-payload
    /// variant.
    /// </summary>
    /// <remarks>
    /// Hand-written, not under codegen — the two wire events have distinct
    /// auto-generated args records (<see cref="AgentToolResponseArgs"/> /
    /// <see cref="AgentToolResponseFullPayloadArgs"/>); this record unifies
    /// them behind the public event surface.
    /// </remarks>
    public record AgentToolRespondedArgs(
        string ToolName,
        string ToolCallId,
        string ToolType,
        bool IsError,
        bool? IsBlocked,
        int EventId,
        bool IsCalled,
        string? FullToolResult,
        bool? Truncated
    );

    /// <summary>
    /// Extensions translating each agent-tool-response wire event into the
    /// unified <see cref="AgentToolRespondedArgs"/> the public event surface
    /// exposes.
    /// </summary>
    public static class AgentToolRespondedArgsExtensions
    {
        public static AgentToolRespondedArgs ToRespondedArgs(this AgentToolResponse e) =>
            new(
                ToolName: e.AgentToolResponseData.ToolName,
                ToolCallId: e.AgentToolResponseData.ToolCallId,
                ToolType: e.AgentToolResponseData.ToolType,
                IsError: e.AgentToolResponseData.IsError,
                IsBlocked: e.AgentToolResponseData.IsBlocked,
                EventId: e.AgentToolResponseData.EventId,
                IsCalled: e.AgentToolResponseData.IsCalled,
                FullToolResult: null,
                Truncated: null
            );

        public static AgentToolRespondedArgs ToRespondedArgs(this AgentToolResponseFullPayload e) =>
            new(
                ToolName: e.AgentToolResponseFullPayloadData.ToolName,
                ToolCallId: e.AgentToolResponseFullPayloadData.ToolCallId,
                ToolType: e.AgentToolResponseFullPayloadData.ToolType,
                IsError: e.AgentToolResponseFullPayloadData.IsError,
                IsBlocked: e.AgentToolResponseFullPayloadData.IsBlocked,
                EventId: e.AgentToolResponseFullPayloadData.EventId,
                IsCalled: e.AgentToolResponseFullPayloadData.IsCalled,
                FullToolResult: e.AgentToolResponseFullPayloadData.FullToolResult,
                Truncated: e.AgentToolResponseFullPayloadData.Truncated
            );
    }
}

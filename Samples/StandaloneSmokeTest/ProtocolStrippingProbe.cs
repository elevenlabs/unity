#nullable enable

using System;
using System.Linq;
using ElevenLabs.Protocol;
using Newtonsoft.Json;
using UnityEngine;

namespace ElevenLabs.Native.Samples.StandaloneSmokeTest
{
    internal static class ProtocolStrippingProbe
    {
        private static readonly IncomingSocketEventConverter Converter = new();

        public static int Run()
        {
            bool[] results =
            {
                Check(
                    "incoming with nested payload",
                    () =>
                        Parse(
                            "{\"type\":\"agent_response\",\"agent_response_event\":{\"agent_response\":\"hi\",\"event_id\":7}}"
                        )
                            is AgentResponse
                            {
                                AgentResponseEvent: { AgentResponse: "hi", EventId: 7 }
                            }
                ),
                Check(
                    "incoming tool request",
                    () =>
                        Parse(
                            "{\"type\":\"agent_tool_request\",\"agent_tool_request\":{\"tool_name\":\"t\",\"tool_call_id\":\"c\",\"tool_type\":\"x\",\"event_id\":3,\"expects_response\":true,\"disable_interruptions\":false,\"response_timeout_secs\":5,\"execution_mode\":\"m\"}}"
                        )
                            is AgentToolRequest
                            {
                                AgentToolRequestData:
                                { ToolName: "t", EventId: 3, ExpectsResponse: true },
                            }
                ),
                Check(
                    "incoming error",
                    () =>
                        Parse(
                            "{\"type\":\"client_error\",\"error_event\":{\"code\":42,\"error_name\":\"boom\",\"message\":\"m\"}}"
                        )
                            is ClientError { ErrorEvent: { Code: 42, ErrorName: "boom" } }
                ),
                Check(
                    "incoming nested list",
                    () =>
                        Parse(
                            "{\"type\":\"mcp_connection_status\",\"mcp_connection_status\":{\"integrations\":[{\"integration_id\":\"i\",\"integration_type\":\"y\",\"is_connected\":true,\"tool_count\":2}]}}"
                        )
                            is McpConnectionStatus
                            {
                                McpConnectionStatusData: { Integrations: { } items }
                            }
                        && items.FirstOrDefault() is { IntegrationId: "i", IsConnected: true }
                ),
                Check(
                    "outgoing with nested overrides",
                    () =>
                        JsonConvert
                            .SerializeObject(
                                new ConversationInitiationClientData
                                {
                                    ConversationConfigOverride = new ConversationConfigOverride
                                    {
                                        Agent = new ConversationConfigOverrideAgent
                                        {
                                            FirstMessage = "probe",
                                        },
                                    },
                                }
                            )
                            .Contains("\"agent\":{\"first_message\":\"probe\"")
                ),
            };
            return results.Count(ok => !ok);
        }

        private static IncomingSocketEvent? Parse(string json) =>
            JsonConvert.DeserializeObject<IncomingSocketEvent>(json, Converter);

        private static bool Check(string name, Func<bool> probe)
        {
            try
            {
                bool ok = probe();
                Debug.Log($"[StandaloneSmoke] stripping probe {(ok ? "ok" : "FAILED")}: {name}");
                return ok;
            }
            catch (Exception e)
            {
                Debug.LogError(
                    $"[StandaloneSmoke] stripping probe FAILED: {name}: {e.GetType().Name}: {e.Message}"
                );
                return false;
            }
        }
    }
}

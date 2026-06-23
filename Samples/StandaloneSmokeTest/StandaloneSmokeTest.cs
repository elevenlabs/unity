#nullable enable

using System;
using System.Threading;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
using UnityEngine;

namespace ElevenLabs.Native.Samples.StandaloneSmokeTest
{
    /// <summary>
    /// IL2CPP-built desktop standalone smoke MonoBehaviour that drives
    /// <see cref="Conversation"/> end-to-end against a real ElevenLabs agent
    /// over the native <c>ClientWebSocket</c> transport. Add to a scene,
    /// build for the current standalone target via
    /// <see cref="ElevenLabs.WebGL.Editor.HostBuild.BuildStandalone"/>, run
    /// the produced binary, and observe stdout / Player.log for
    /// <c>[StandaloneSmoke]</c> messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gates the native <c>link.xml</c> for the
    /// <c>ElevenLabs.Protocol.*</c> namespace: every wire DTO read by
    /// <see cref="IncomingSocketEventConverter"/> and written by the
    /// outgoing serializer is exercised at least once during a real
    /// conversation_initiation handshake, so any AOT-stripping regression
    /// surfaces here instead of a vague consumer-side
    /// <c>JsonReaderException</c>.
    /// </para>
    /// <para>
    /// Reads agent credentials from <see cref="StandaloneSmokeConfig"/>
    /// loaded via <see cref="Resources.Load{T}(string)"/>. When the asset
    /// is missing or <see cref="StandaloneSmokeConfig.AgentId"/> is empty,
    /// logs <c>[StandaloneSmoke] CONFIG MISSING</c> and exits cleanly so a
    /// clean clone still builds and runs to completion.
    /// </para>
    /// <para>
    /// Editor mode skips the conversation flow — opening a microphone in
    /// an Editor play session that may be running headlessly trips device
    /// permission prompts; the IL2CPP-built binary is the one that
    /// actually verifies the AOT hardening.
    /// </para>
    /// </remarks>
    public sealed class StandaloneSmokeTest : MonoBehaviour
    {
        private const float ResponseTimeoutSeconds = 30f;
        private const float DisconnectTimeoutSeconds = 10f;

        private void Start()
        {
#if UNITY_EDITOR
            Debug.Log(
                "[StandaloneSmoke] Editor mode — skipping conversation flow. "
                    + "Build via HostBuild.BuildStandalone and run the produced binary "
                    + "to exercise the native session over IL2CPP."
            );
#else
            _ = RunSmokeTestAsync();
#endif
        }

#if !UNITY_EDITOR
        private const string AgentIdEnvVar = "ELEVENLABS_AGENT_ID";
        private const string SignedUrlEnvVar = "ELEVENLABS_SIGNED_URL";
        private const string PromptEnvVar = "ELEVENLABS_SMOKE_PROMPT";

        private async Awaitable RunSmokeTestAsync()
        {
            // Env vars win over the Resources asset because the embedded
            // TestProject is also the package root (manifest.json:
            // "file:../.."), so Unity sees Assets/Resources/ assets twice
            // and the build's Resources scanner sometimes skips them. The
            // env var path is also the natural shape for CI runners.
            string? envAgentId = Environment.GetEnvironmentVariable(AgentIdEnvVar);
            string? envSignedUrl = Environment.GetEnvironmentVariable(SignedUrlEnvVar);
            string? envPrompt = Environment.GetEnvironmentVariable(PromptEnvVar);

            StandaloneSmokeConfig? config = Resources.Load<StandaloneSmokeConfig>(
                "StandaloneSmokeConfig"
            );

            string agentId = !string.IsNullOrWhiteSpace(envAgentId)
                ? envAgentId!
                : (config != null ? config.AgentId : "");
            string? signedUrl = !string.IsNullOrWhiteSpace(envSignedUrl)
                ? envSignedUrl
                : config?.SignedUrl;
            string prompt = !string.IsNullOrWhiteSpace(envPrompt)
                ? envPrompt!
                : (
                    config != null
                        ? config.Prompt
                        : "Hello, please reply with the word READY and stop."
                );

            if (string.IsNullOrWhiteSpace(agentId))
            {
                Debug.Log(
                    "[StandaloneSmoke] CONFIG MISSING "
                        + $"(set {AgentIdEnvVar} env var or fill in Resources/StandaloneSmokeConfig.asset)"
                );
                Quit(0);
                return;
            }

            Debug.Log("[StandaloneSmoke] starting session…");

            int exitCode = 1;
            try
            {
                var options = new ConversationOptions
                {
                    AgentId = agentId,
                    ConnectionType = ConnectionType.WebSocket,
                    SignedUrl = signedUrl,
                };

                Conversation conversation = await Conversation.StartSessionAsync(options);
                Debug.Log($"[StandaloneSmoke] connected: id={conversation.ConversationId}");

                conversation.ErrorOccurred += msg => Debug.Log($"[StandaloneSmoke] error: {msg}");
                conversation.StatusChanged += s => Debug.Log($"[StandaloneSmoke] status → {s}");

                var responseTask = AwaitEvent<AgentResponseArgs>(
                    handler => conversation.AgentResponded += handler,
                    handler => conversation.AgentResponded -= handler,
                    ResponseTimeoutSeconds,
                    "AgentResponded"
                );

                Debug.Log($"[StandaloneSmoke] sending user message: \"{prompt}\"");
                conversation.SendUserMessage(prompt);

                AgentResponseArgs response = await responseTask;
                if (string.IsNullOrWhiteSpace(response.AgentResponse))
                    throw new InvalidOperationException(
                        "agent response was empty — Newtonsoft.Json may have stripped property setters"
                    );
                Debug.Log($"[StandaloneSmoke] agent responded: \"{response.AgentResponse}\"");

                var disconnectTask = AwaitEvent<DisconnectionDetails>(
                    handler => conversation.Disconnected += handler,
                    handler => conversation.Disconnected -= handler,
                    DisconnectTimeoutSeconds,
                    "Disconnected"
                );

                Debug.Log("[StandaloneSmoke] ending session…");
                await conversation.EndSession();
                DisconnectionDetails details = await disconnectTask;
                Debug.Log($"[StandaloneSmoke] disconnected: reason={details.Reason}");

                Debug.Log("[StandaloneSmoke] All standalone smoke checks passed!");
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[StandaloneSmoke] ASSERTION FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
                );
            }
            finally
            {
                Quit(exitCode);
            }
        }

        private static void Quit(int exitCode)
        {
            // Application.Quit is async on most platforms; the smoke is a
            // one-shot binary, so set the exit code and request shutdown.
            // Wrapped to keep the success / failure paths symmetric.
            Application.Quit(exitCode);
        }

        private static async Awaitable<T> AwaitEvent<T>(
            Action<Action<T>> subscribe,
            Action<Action<T>> unsubscribe,
            float timeoutSeconds,
            string description
        )
        {
            var source = new AwaitableCompletionSource<T>();
            Action<T> handler = arg => source.TrySetResult(arg);
            subscribe(handler);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using CancellationTokenRegistration _ = cts.Token.Register(() =>
                source.TrySetException(
                    new TimeoutException(
                        $"Timed out waiting for {description} after {timeoutSeconds}s"
                    )
                )
            );
            try
            {
                return await source.Awaitable;
            }
            finally
            {
                unsubscribe(handler);
            }
        }
#endif
    }
}

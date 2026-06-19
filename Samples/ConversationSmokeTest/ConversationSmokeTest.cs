#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
using ElevenLabs.WebGL.Internal;
using UnityEngine;

namespace ElevenLabs.WebGL.Samples.ConversationSmokeTest
{
    /// <summary>
    /// WebGL smoke-test MonoBehaviour that drives <see cref="Conversation"/>
    /// end-to-end against a real ElevenLabs agent. Add to a scene, build for
    /// WebGL, and observe the browser console for <c>[ConvSmoke]</c> messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads agent credentials from <see cref="ConversationSmokeConfig"/>
    /// loaded via <see cref="Resources.Load{T}(string)"/>. When the asset is
    /// missing or <see cref="ConversationSmokeConfig.AgentId"/> is empty,
    /// logs <c>[ConvSmoke] CONFIG MISSING</c> and exits cleanly — the Vitest
    /// harness treats that line as a test skip rather than a failure.
    /// </para>
    /// <para>
    /// Editor mode skips the conversation flow entirely: the bridged session
    /// launcher would attempt a JS bridge call and throw <c>BridgeException</c>.
    /// </para>
    /// <para>
    /// Beyond the happy path (connect → send → response → audio → end), the
    /// test asserts a Conversation-level subset of the primitives smoke's
    /// V-series invariants: <b>V2</b> a sync DllImport-backed call is usable
    /// immediately after an awaited async call; <b>V3</b> rapid audio events
    /// arrive with strictly monotonically increasing <c>EventId</c>; <b>V4</b>
    /// the underlying connection handle serves multiple outgoing sends in one
    /// session. It also checks that the JS-side default-mode audio pipeline
    /// runs without any Unity <see cref="AudioSource"/> in the scene, and that
    /// every JS-side handle acquired during the session is released by
    /// <c>EndSession</c> (registry counts return to baseline).
    /// </para>
    /// </remarks>
    public class ConversationSmokeTest : MonoBehaviour
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private const float ResponseTimeoutSeconds = 30f;
        private const float AudioTimeoutSeconds = 30f;
        private const float ResponseCompletedTimeoutSeconds = 60f;
        private const float DisconnectTimeoutSeconds = 10f;

        private const string Prompt = "Hello, please reply with the word READY and stop.";
        private const string ContextOne = "Game state: player is in a tutorial.";
        private const string ContextTwo = "Locale: en-US.";
#endif

        private void Start()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            _ = RunSmokeTestAsync();
#else
            Debug.Log(
                "[ConvSmoke] Editor mode — skipping conversation flow. "
                    + "Build for WebGL to exercise the bridged session."
            );
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private async Awaitable RunSmokeTestAsync()
        {
            ConversationSmokeConfig? config = Resources.Load<ConversationSmokeConfig>(
                "ConversationSmokeConfig"
            );
            if (config == null || string.IsNullOrWhiteSpace(config.AgentId))
            {
                Debug.Log("[ConvSmoke] CONFIG MISSING");
                return;
            }

            // Baseline: any handles that exist BEFORE we touch the bridge. A
            // fresh page typically has 0, but capturing the value rather than
            // hard-coding 0 makes the no-leak check robust if a future
            // primitives change adds ambient bridge state at page load.
            int baselinePromises = PromiseRegistry.Count;
            int baselineCallbacks = CallbackRegistry.Count;
            Debug.Log(
                $"[ConvSmoke] handle baseline: promises={baselinePromises} callbacks={baselineCallbacks}"
            );

            Debug.Log("[ConvSmoke] starting session…");

            try
            {
                var options = new ConversationOptions
                {
                    AgentId = config.AgentId,
                    ConnectionType = config.ConnectionType,
                    SignedUrl = config.SignedUrl,
                };

                Conversation conversation = await Conversation.StartSessionAsync(options);

                // --- V2: a sync DllImport-backed call (SendContextualUpdate
                // bottoms out in BridgedWebSocketConnection.Send →
                // JsObject.Call → DllImport) must be usable IMMEDIATELY after
                // the awaited StartSessionAsync, with no extra frame yield. If
                // Awaitable continuations corrupted the Emscripten call
                // context this would crash here.
                conversation.SendContextualUpdate(ContextOne);
                Debug.Log("[ConvSmoke] V2 sync send after await ✓");

                // --- V4: the same connection handle must survive multiple
                // outgoing sends in one session. The third call below is the
                // user message; the two contextual updates exercise the same
                // path with different OutgoingSocketEvent subtypes.
                conversation.SendContextualUpdate(ContextTwo);

                int activePromises = PromiseRegistry.Count;
                int activeCallbacks = CallbackRegistry.Count;
                Debug.Log(
                    $"[ConvSmoke] handle active: promises={activePromises} callbacks={activeCallbacks}"
                );

                conversation.ErrorOccurred += msg => Debug.Log($"[ConvSmoke] error: {msg}");
                conversation.StatusChanged += s => Debug.Log($"[ConvSmoke] status → {s}");
                conversation.ModeChanged += m => Debug.Log($"[ConvSmoke] mode → {m}");
                conversation.UserTranscriptReceived += t =>
                    Debug.Log($"[ConvSmoke] user transcript: \"{t.UserTranscript}\"");

                Debug.Log($"[ConvSmoke] connected: id={conversation.ConversationId}");

                // --- V3: record every audio chunk's EventId for the entire
                // turn. Strict monotonicity is asserted after the agent
                // signals AgentResponseCompleted so the list is complete.
                var audioEventIds = new List<int>();
                Action<AudioResponseArgs> audioCollector = audio =>
                {
                    audioEventIds.Add(audio.EventId);
                };
                conversation.AudioReceived += audioCollector;

                var responseTask = AwaitEvent<AgentResponseArgs>(
                    handler => conversation.AgentResponded += handler,
                    handler => conversation.AgentResponded -= handler,
                    ResponseTimeoutSeconds,
                    "AgentResponded"
                );
                var firstAudioTask = AwaitEvent<AudioResponseArgs>(
                    handler => conversation.AudioReceived += handler,
                    handler => conversation.AudioReceived -= handler,
                    AudioTimeoutSeconds,
                    "AudioReceived"
                );
                var responseCompletedTask = AwaitEvent<AgentResponseCompleteArgs>(
                    handler => conversation.AgentResponseCompleted += handler,
                    handler => conversation.AgentResponseCompleted -= handler,
                    ResponseCompletedTimeoutSeconds,
                    "AgentResponseCompleted"
                );

                Debug.Log($"[ConvSmoke] sending user message: \"{Prompt}\"");
                conversation.SendUserMessage(Prompt);

                AgentResponseArgs response = await responseTask;
                Debug.Log($"[ConvSmoke] agent responded: \"{response.AgentResponse}\"");

                AudioResponseArgs firstAudio = await firstAudioTask;
                Debug.Log($"[ConvSmoke] first audio chunk received: event_id={firstAudio.EventId}");

                await responseCompletedTask;
                conversation.AudioReceived -= audioCollector;

                AssertMonotonicallyIncreasing(audioEventIds);
                Debug.Log(
                    $"[ConvSmoke] V3 audio ordering ✓ ({audioEventIds.Count} chunks, ids={string.Join(",", audioEventIds)})"
                );

                // The conversation smoke scene is built by HostBuild.BuildConversation
                // as an empty scene with this MonoBehaviour as its only object —
                // no Unity AudioSource is ever instantiated. Audio events
                // nevertheless arriving here proves the JS-side default-mode
                // pipeline (attachInputToConnection / attachConnectionToOutput +
                // withoutAudioPayload) carried the turn end-to-end without
                // touching Unity's audio component graph.
                if (audioEventIds.Count == 0)
                    throw new InvalidOperationException(
                        "audio default-mode: expected at least one AudioReceived event, got 0"
                    );
                Debug.Log(
                    $"[ConvSmoke] audio default-mode: {audioEventIds.Count} audio events received via JS-only pipeline ✓"
                );

                var disconnectTask = AwaitEvent<DisconnectionDetails>(
                    handler => conversation.Disconnected += handler,
                    handler => conversation.Disconnected -= handler,
                    DisconnectTimeoutSeconds,
                    "Disconnected"
                );

                Debug.Log("[ConvSmoke] ending session…");
                await conversation.EndSession();
                DisconnectionDetails details = await disconnectTask;
                Debug.Log($"[ConvSmoke] disconnected: reason={details.Reason}");

                // Let any pending EL_*Release DllImports queued by the dispose
                // chain settle on the JS side before snapshotting counts.
                await Awaitable.NextFrameAsync();

                int teardownPromises = PromiseRegistry.Count;
                int teardownCallbacks = CallbackRegistry.Count;
                Debug.Log(
                    $"[ConvSmoke] handle teardown: promises={teardownPromises} callbacks={teardownCallbacks}"
                );
                if (teardownPromises != baselinePromises || teardownCallbacks != baselineCallbacks)
                    throw new InvalidOperationException(
                        $"handles leaked: baseline (p={baselinePromises}, c={baselineCallbacks}) "
                            + $"vs teardown (p={teardownPromises}, c={teardownCallbacks})"
                    );
                Debug.Log("[ConvSmoke] no-leak: handle counts back to baseline ✓");

                Debug.Log("[ConvSmoke] All conversation smoke tests passed!");
            }
            catch (Exception ex)
            {
                // Match the primitives smoke's "ASSERTION FAILED" prefix so the
                // existing Vitest harness regex catches conversational failures
                // too. Include the exception type for quick triage from console.
                Debug.LogError(
                    $"[ConvSmoke] ASSERTION FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
                );
            }
        }

        private static void AssertMonotonicallyIncreasing(List<int> eventIds)
        {
            if (eventIds.Count == 0)
                throw new InvalidOperationException("V3: expected at least one audio chunk, got 0");
            for (int i = 1; i < eventIds.Count; i++)
            {
                if (eventIds[i] <= eventIds[i - 1])
                    throw new InvalidOperationException(
                        $"V3: audio EventIds not strictly increasing at index {i}: "
                            + $"[{eventIds[i - 1]}, {eventIds[i]}] — full list: "
                            + string.Join(",", eventIds)
                    );
            }
        }

        // Subscribe to a Conversation event, await the next invocation, then
        // unsubscribe — with a hard timeout that surfaces as a TimeoutException.
        // The harness can't tell a hung session from a slow agent otherwise.
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

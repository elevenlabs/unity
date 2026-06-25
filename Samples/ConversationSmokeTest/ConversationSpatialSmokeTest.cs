#nullable enable

using System;
using System.Threading;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
using UnityEngine;

namespace ElevenLabs.WebGL.Samples.ConversationSmokeTest
{
    /// <summary>
    /// WebGL smoke that opens a <see cref="Conversation"/> with an
    /// <see cref="AudioSource"/> bound via
    /// <c>ConversationOptions.OutputAudioSource</c>, awaits one audio chunk,
    /// then exits. Drives the JS-side <c>createWebAudioSink</c> path so the
    /// browser-side <see cref="IntegrationTests~"/> harness can introspect
    /// the running Web Audio graph and verify the supplied source's
    /// transform actually reaches the <c>PannerNode</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately minimal — bridge wiring + handle-leak invariants are
    /// already covered by <see cref="ConversationSmokeTest"/>. This smoke's
    /// only job is to prove the spatial-output binding survives a real WebGL
    /// session end-to-end, so duplicating the V2/V3/V4 assertions would just
    /// add maintenance surface.
    /// </para>
    /// <para>
    /// Source position is set to (3, 0, 0) world before
    /// <see cref="Conversation.StartSessionAsync"/> opens. The scene is built
    /// empty (no Main Camera, no <see cref="AudioListener"/>), so
    /// <c>WebAudioBackedOutput</c> takes the no-listener fallback: world
    /// position with Z flipped. The integration test asserts
    /// <c>panner.positionX.value === 3</c> on the JS side.
    /// </para>
    /// </remarks>
    public class ConversationSpatialSmokeTest : MonoBehaviour
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private const float ResponseTimeoutSeconds = 30f;
        private const float AudioTimeoutSeconds = 30f;
        private const float DisconnectTimeoutSeconds = 10f;

        private const string Prompt = "Hello, please reply with the word READY and stop.";

        // Distinctive coordinate so the integration test can pin the exact
        // value pushed to the PannerNode. Y / Z are zero so the assertion
        // pins one specific axis without tolerance work.
        private static readonly Vector3 SpatialPosition = new Vector3(3f, 0f, 0f);
#endif

        private void Start()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            _ = RunSpatialSmokeAsync();
#else
            Debug.Log(
                "[SpatialSmoke] Editor mode — skipping spatial smoke flow. "
                    + "Build for WebGL to exercise the bridged session."
            );
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private async Awaitable RunSpatialSmokeAsync()
        {
            ConversationSmokeConfig? config = Resources.Load<ConversationSmokeConfig>(
                "ConversationSmokeConfig"
            );
            if (config == null || string.IsNullOrWhiteSpace(config.AgentId))
            {
                Debug.Log("[SpatialSmoke] CONFIG MISSING");
                return;
            }

            transform.position = SpatialPosition;
            AudioSource audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.spatialBlend = 1f;
            audioSource.minDistance = 1f;
            audioSource.maxDistance = 50f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;

            Debug.Log(
                $"[SpatialSmoke] starting session with OutputAudioSource at "
                    + $"({SpatialPosition.x}, {SpatialPosition.y}, {SpatialPosition.z})…"
            );

            try
            {
                var options = new ConversationOptions
                {
                    AgentId = config.AgentId,
                    ConnectionType = config.ConnectionType,
                    SignedUrl = config.SignedUrl,
                    OutputAudioSource = audioSource,
                };

                Conversation conversation = await Conversation.StartSessionAsync(options);
                conversation.ErrorOccurred += msg => Debug.Log($"[SpatialSmoke] error: {msg}");

                Debug.Log($"[SpatialSmoke] connected: id={conversation.ConversationId}");

                var firstAudioTask = AwaitEvent<AudioResponseArgs>(
                    handler => conversation.AudioReceived += handler,
                    handler => conversation.AudioReceived -= handler,
                    AudioTimeoutSeconds,
                    "AudioReceived"
                );
                var responseTask = AwaitEvent<AgentResponseArgs>(
                    handler => conversation.AgentResponded += handler,
                    handler => conversation.AgentResponded -= handler,
                    ResponseTimeoutSeconds,
                    "AgentResponded"
                );

                conversation.SendUserMessage(Prompt);
                await responseTask;
                await firstAudioTask;
                Debug.Log("[SpatialSmoke] first audio chunk arrived ✓");

                // Poll GetOutputVolume across a window of frames while audio
                // is actively playing. This is the cross-platform sample-read
                // API devs should use (post-step-6 of output-audio-source.md);
                // raw `audioSource.GetOutputData(...)` on the bound AudioSource
                // returns silent buffers on WebGL because the SDK plays through
                // a parallel Web Audio graph — the AudioSource is a property
                // carrier, never actually playing scripted PCM. 60 frames at
                // ~60 Hz = ~1 s, enough to see at least one non-silent reading
                // if the analyser tap is alive on this backend.
                float maxVolume = 0f;
                for (int i = 0; i < 60; i++)
                {
                    float v = conversation.GetOutputVolume();
                    if (v > maxVolume)
                        maxVolume = v;
                    if (i % 10 == 0)
                        Debug.Log($"[SpatialSmoke] frame {i} GetOutputVolume = {v:F4}");
                    await Awaitable.NextFrameAsync();
                }
                Debug.Log(
                    $"[SpatialSmoke] GetOutputVolume sampled across 60 frames; max = {maxVolume:F4}"
                );
                // Step-6 regression gate: if the JS sink's getVolume contract
                // drifts (e.g. someone flips `getByteTimeDomainData` back to
                // `getByteFrequencyData`, or the analyser stays at silence
                // because the AudioContext never resumed), maxVolume will pin
                // at 0 and this assertion catches it. 0.005 is a very small
                // floor — quiet speech RMS routinely exceeds 0.05, so anything
                // below this threshold is almost certainly broken plumbing,
                // not a quiet line of dialogue.
                const float MinExpectedMaxVolume = 0.005f;
                if (maxVolume < MinExpectedMaxVolume)
                {
                    throw new InvalidOperationException(
                        $"GetOutputVolume max ({maxVolume:F4}) stayed below "
                            + $"{MinExpectedMaxVolume:F4} across 60 frames — the JS sink's "
                            + "analyser tap appears silent. Check that "
                            + "createWebAudioSink resumed the AudioContext and that "
                            + "getVolume reads getByteTimeDomainData."
                    );
                }

                var disconnectTask = AwaitEvent<DisconnectionDetails>(
                    handler => conversation.Disconnected += handler,
                    handler => conversation.Disconnected -= handler,
                    DisconnectTimeoutSeconds,
                    "Disconnected"
                );

                await conversation.EndSession();
                await disconnectTask;
                Debug.Log("[SpatialSmoke] All spatial smoke tests passed!");
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[SpatialSmoke] ASSERTION FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
                );
            }
        }

        // Subscribe to a Conversation event, await the next invocation, then
        // unsubscribe — with a hard timeout that surfaces as a TimeoutException.
        // Identical to ConversationSmokeTest.AwaitEvent; intentionally
        // duplicated here so this smoke stays a self-contained sibling.
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

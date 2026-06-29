#nullable enable

using System;
using System.Collections.Generic;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
using UnityEngine;

namespace ElevenLabs.Agents.Samples.GettingStarted
{
    /// <summary>
    /// Cube mood, injected as the <c>{{mood}}</c> dynamic variable on session
    /// start (lower-cased).
    /// </summary>
    public enum BoxMood
    {
        Happy,
        Sad,
        Angry,
    }

    /// <summary>
    /// Drop on a cube with a trigger collider and an <see cref="AudioSource"/>.
    /// Walk a non-trigger collider into the box to open a conversation; walk
    /// out to end it. The box bobs up and down with the agent's output volume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only one <see cref="TalkingBox"/> can be active at a time — entering a
    /// new box's trigger automatically ends the previous box's session before
    /// opening the new one. This keeps multiple boxes in the same scene from
    /// fighting over the microphone.
    /// </para>
    /// <para>
    /// The bob uses <see cref="Conversation.GetOutputVolume()"/> rather than
    /// <c>AudioSource.GetOutputData</c> so it works identically on native and
    /// WebGL — the WebGL backend plays through a parallel Web Audio graph and
    /// the assigned <see cref="AudioSource"/> is a spatialisation carrier
    /// rather than a real PCM playback path.
    /// </para>
    /// </remarks>
    public class TalkingBox : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Cube color label, injected as the {{color}} dynamic variable.")]
        private string color = "yellow";

        [SerializeField]
        [Tooltip("Cube mood, injected as the {{mood}} dynamic variable (lower-cased).")]
        private BoxMood mood = BoxMood.Happy;

        [SerializeField]
        [TextArea(2, 4)]
        [Tooltip(
            "First message override sent to the agent. Supports {{color}} and {{mood}} "
                + "dynamic-variable interpolation. Leave empty to use the agent's default."
        )]
        private string firstMessage =
            "Hi there! I'm a {{color}} cube, and I'm feeling {{mood}} today.";

        [SerializeField]
        [Tooltip(
            "TTS voice ID override sent to the agent. Empty = use the agent's configured voice. "
                + "The agent must have tts.voice_id overrides enabled in its security settings."
        )]
        private string voiceIdOverride = "";

        [SerializeField]
        [Tooltip(
            "World-space Y offset (in metres) added at peak perceived volume. "
                + "Using position rather than scale avoids growing the trigger collider "
                + "and pushing the player out of the conversation zone mid-speech."
        )]
        private float peakOffsetY = 0.3f;

        [SerializeField]
        [Tooltip(
            "Pre-multiplier on the raw RMS volume reading. Speech RMS typically sits in [0.1, 0.3]; "
                + "a sensitivity of ~4 makes normal speech saturate the response."
        )]
        private float volumeSensitivity = 4.0f;

        [SerializeField]
        [Tooltip(
            "Exponential-smoothing time constant (seconds) applied to the polled volume. "
                + "Bridges the audio-thread cadence into smooth per-frame motion. "
                + "Smaller = snappier, larger = calmer."
        )]
        private float smoothingTau = 0.08f;

        [SerializeField]
        [Tooltip(
            "AudioSource that the agent's voice plays through. The SDK binds its streaming "
                + "audio to this source (via ConversationOptions.OutputAudioSource) so it "
                + "spatialises with the box's position."
        )]
        private AudioSource? audioSource;

        private static TalkingBox? currentlyActive;

        private Vector3 baselinePosition;

        private Conversation? activeConversation;

        private float smoothedVolume;

        private void Awake()
        {
            baselinePosition = transform.localPosition;

            bool hasTrigger = false;
            foreach (Collider c in GetComponents<Collider>())
                if (c.isTrigger)
                {
                    hasTrigger = true;
                    break;
                }
            if (!hasTrigger)
                Debug.LogWarning(
                    $"[TalkingBox:{name}] no trigger collider found — add a SphereCollider with Is Trigger enabled."
                );

            if (audioSource == null)
                Debug.LogWarning(
                    $"[TalkingBox:{name}] no AudioSource assigned — the box won't bob, and voice will play omnipresent (2D)."
                );
        }

        private void Update()
        {
            float rms = activeConversation?.GetOutputVolume() ?? 0f;
            float target = Mathf.Clamp01(rms * volumeSensitivity);
            float alpha = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, smoothingTau));
            smoothedVolume = Mathf.Lerp(smoothedVolume, target, alpha);
            transform.localPosition =
                baselinePosition + Vector3.up * (smoothedVolume * peakOffsetY);
        }

        private void OnTriggerEnter(Collider other) => _ = StartTalkingAsync();

        private void OnTriggerExit(Collider other) => _ = StopTalkingAsync();

        private string MoodString => mood.ToString().ToLowerInvariant();

        // Returns null when neither field is set, so we don't send an empty
        // Overrides object. Otherwise builds Agent / Tts subtrees individually so
        // each field only ships when the designer actually wanted to override it.
        private ConversationConfigOverride? BuildOverrides()
        {
            bool hasFirstMessage = !string.IsNullOrWhiteSpace(firstMessage);
            bool hasVoiceId = !string.IsNullOrWhiteSpace(voiceIdOverride);
            if (!hasFirstMessage && !hasVoiceId)
                return null;
            return new ConversationConfigOverride
            {
                Agent = hasFirstMessage
                    ? new ConversationConfigOverrideAgent { FirstMessage = firstMessage }
                    : null,
                Tts = hasVoiceId
                    ? new ConversationConfigOverrideTts { VoiceId = voiceIdOverride }
                    : null,
            };
        }

        private async Awaitable StartTalkingAsync()
        {
            if (currentlyActive == this)
                return;
            if (currentlyActive != null)
                await currentlyActive.StopTalkingAsync();
            currentlyActive = this;

            TalkingBoxAgentConfig? config = Resources.Load<TalkingBoxAgentConfig>(
                "TalkingBoxAgentConfig"
            );
            if (config == null || string.IsNullOrWhiteSpace(config.AgentId))
            {
                Debug.LogError(
                    "[TalkingBox] Missing or empty Assets/Resources/TalkingBoxAgentConfig.asset"
                );
                currentlyActive = null;
                return;
            }

            var options = new ConversationOptions
            {
                AgentId = config.AgentId,
                DynamicVariables = new Dictionary<string, object>
                {
                    ["color"] = color,
                    ["mood"] = MoodString,
                },
                Overrides = BuildOverrides(),
                OutputAudioSource = audioSource,
            };

            try
            {
                activeConversation = await Conversation.StartSessionAsync(options);
                activeConversation.ErrorOccurred += msg =>
                    Debug.LogError($"[TalkingBox:{name}] agent error: {msg}");
                Debug.Log(
                    $"[TalkingBox:{name}] session started "
                        + $"({color}/{MoodString}) id={activeConversation.ConversationId}"
                );
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TalkingBox:{name}] StartSessionAsync failed: {ex.Message}");
                activeConversation = null;
                currentlyActive = null;
            }
        }

        private async Awaitable StopTalkingAsync()
        {
            if (activeConversation == null)
            {
                if (currentlyActive == this)
                    currentlyActive = null;
                return;
            }

            try
            {
                await activeConversation.EndSession();
                Debug.Log($"[TalkingBox:{name}] session ended");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TalkingBox:{name}] EndSession failed: {ex.Message}");
            }
            finally
            {
                activeConversation = null;
                if (currentlyActive == this)
                    currentlyActive = null;
            }
        }
    }
}

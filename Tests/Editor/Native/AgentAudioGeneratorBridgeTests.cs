#nullable enable

using System.Collections.Generic;
using NUnit.Framework;

namespace ElevenLabs.Native.Tests
{
    /// <summary>
    /// Edit-mode coverage for <see cref="AgentAudioGeneratorBridge"/>'s
    /// refill contract. The drain callback hands samples over
    /// destructively (the host ring's read cursor advances as it fills
    /// the scratch buffer), so every sample the callback returns must
    /// eventually reach the bridge ring — a refill the ring can't absorb
    /// would vanish from the playback stream entirely.
    /// </summary>
    public class AgentAudioGeneratorBridgeTests
    {
        [Test]
        public void PeekOrPull_RingNearlyFull_NeverLosesCallbackSamples()
        {
            // Capacity 100 with a 64-sample refill scratch: pre-filling 90
            // samples leaves only 10 slots free, so a refill pulled while
            // the ring is nearly full cannot be absorbed whole. The
            // producer hands over an incrementing sequence and the test
            // drains everything the bridge ever plays — any dropped
            // refill tail shows up as a gap in the sequence.
            var bridge = new AgentAudioGeneratorBridge(
                inputSampleRate: 16000,
                ringCapacity: 100,
                scratchFrames: 64
            );
            try
            {
                float[] prefill = new float[90];
                for (int i = 0; i < prefill.Length; i++)
                    prefill[i] = i;
                Assert.AreEqual(prefill.Length, bridge.Ring.Write(prefill));

                int next = prefill.Length;
                bridge.SetProducer(dest =>
                {
                    for (int i = 0; i < dest.Length; i++)
                        dest[i] = next++;
                    return dest.Length;
                });

                // Drain in chunks larger than the ring's current content so
                // every round underruns the peek and exercises the refill
                // path while free space varies.
                var drained = new List<float>();
                float[] dest = new float[95];
                for (int round = 0; round < 4; round++)
                {
                    int n = bridge.Drain(dest);
                    for (int i = 0; i < n; i++)
                        drained.Add(dest[i]);
                }

                Assert.Greater(drained.Count, prefill.Length, "Refill path never exercised.");
                for (int i = 0; i < drained.Count; i++)
                {
                    if (drained[i] == i)
                        continue;
                    Assert.Fail(
                        $"Playback stream discontinuity at drained sample {i}: expected "
                            + $"sequence value {i}, observed {drained[i]} — "
                            + $"{drained[i] - i} samples handed over by the drain callback "
                            + "were dropped between the host ring and the bridge ring."
                    );
                }
            }
            finally
            {
                bridge.Dispose();
            }
        }
    }
}

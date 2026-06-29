#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace ElevenLabs.Native.Tests
{
    /// <summary>
    /// Unit tests for <see cref="AudioPcmRing"/> — the SPSC PCM ring
    /// buffer the upcoming <c>UnityGeneratorAudioOutputEngine</c> will
    /// use to hand samples from the managed network thread to the audio
    /// thread (see <c>Docs~/plans/audio-generator-engine.md</c> step 2).
    /// </summary>
    /// <remarks>
    /// Covers: empty-read short-circuit, full-write overflow drop,
    /// boundary wrap on both read and write, idempotent dispose, and a
    /// producer/consumer race smoke test that asserts no samples are
    /// lost or reordered when both sides are pushed hard. The race test
    /// is bounded to ~1 second wall-clock so it stays well within the
    /// Edit-Mode runner's per-test budget.
    /// </remarks>
    public class AudioPcmRingTests
    {
        [Test]
        public void Ctor_NonPositiveCapacity_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AudioPcmRing(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AudioPcmRing(-1));
        }

        [Test]
        public void NewRing_IsEmpty_AndFullyAvailable()
        {
            using var ring = new AudioPcmRing(16);
            Assert.That(ring.Capacity, Is.EqualTo(16));
            Assert.That(ring.Count, Is.EqualTo(0));
            Assert.That(ring.Available, Is.EqualTo(16));
        }

        [Test]
        public void Read_OnEmpty_Returns_Zero_AndLeavesDestinationUntouched()
        {
            using var ring = new AudioPcmRing(8);
            var dst = new float[4];
            Array.Fill(dst, -1f);
            int read = ring.Read(dst);
            Assert.That(read, Is.EqualTo(0));
            Assert.That(
                dst,
                Is.All.EqualTo(-1f),
                "Read should not touch destination when ring is empty."
            );
        }

        [Test]
        public void Write_EmptyInput_Returns_Zero_AndDoesNotAdvance()
        {
            using var ring = new AudioPcmRing(8);
            int written = ring.Write(ReadOnlySpan<float>.Empty);
            Assert.That(written, Is.EqualTo(0));
            Assert.That(ring.Count, Is.EqualTo(0));
        }

        [Test]
        public void Read_EmptyDestination_Returns_Zero()
        {
            using var ring = new AudioPcmRing(8);
            ring.Write(new float[] { 1f, 2f, 3f });
            int read = ring.Read(Span<float>.Empty);
            Assert.That(read, Is.EqualTo(0));
            Assert.That(ring.Count, Is.EqualTo(3), "Empty Read must not drain the ring.");
        }

        [Test]
        public void WriteThenRead_RoundTripsExactValues()
        {
            using var ring = new AudioPcmRing(16);
            var src = new float[] { 1.5f, 2.5f, 3.5f, 4.5f, 5.5f };
            int written = ring.Write(src);
            Assert.That(written, Is.EqualTo(src.Length));
            Assert.That(ring.Count, Is.EqualTo(src.Length));
            Assert.That(ring.Available, Is.EqualTo(16 - src.Length));

            var dst = new float[src.Length];
            int read = ring.Read(dst);
            Assert.That(read, Is.EqualTo(src.Length));
            Assert.That(dst, Is.EqualTo(src));
            Assert.That(ring.Count, Is.EqualTo(0));
            Assert.That(ring.Available, Is.EqualTo(16));
        }

        [Test]
        public void Write_BeyondCapacity_DropsOverflow_AndReportsWrittenCount()
        {
            using var ring = new AudioPcmRing(4);
            var src = new float[] { 10f, 11f, 12f, 13f, 14f, 15f };
            int written = ring.Write(src);
            Assert.That(written, Is.EqualTo(4), "Only the first four samples should fit.");
            Assert.That(ring.Count, Is.EqualTo(4));
            Assert.That(ring.Available, Is.EqualTo(0));

            // A subsequent write while full drops everything.
            int writtenAgain = ring.Write(new float[] { 99f });
            Assert.That(writtenAgain, Is.EqualTo(0));

            var dst = new float[6];
            int read = ring.Read(dst);
            Assert.That(read, Is.EqualTo(4));
            Assert.That(dst[0..4], Is.EqualTo(new float[] { 10f, 11f, 12f, 13f }));
        }

        [Test]
        public void Read_LargerThanAvailable_ReturnsAvailableAndDoesNotOverflowDestination()
        {
            using var ring = new AudioPcmRing(8);
            ring.Write(new float[] { 7f, 8f });
            var dst = new float[5];
            Array.Fill(dst, -1f);
            int read = ring.Read(dst);
            Assert.That(read, Is.EqualTo(2));
            Assert.That(dst[0], Is.EqualTo(7f));
            Assert.That(dst[1], Is.EqualTo(8f));
            Assert.That(dst[2], Is.EqualTo(-1f), "Slots beyond the read count must be untouched.");
            Assert.That(dst[3], Is.EqualTo(-1f));
            Assert.That(dst[4], Is.EqualTo(-1f));
        }

        [Test]
        public void Write_WrapsAcrossBoundary_PreservesOrder()
        {
            using var ring = new AudioPcmRing(8);
            // Fill 6, drain 6: leaves write head at index 6, read head at index 6, count 0.
            ring.Write(new float[] { 1f, 2f, 3f, 4f, 5f, 6f });
            var scratch = new float[6];
            ring.Read(scratch);
            Assert.That(ring.Count, Is.EqualTo(0));

            // Write 5 samples — must wrap (slots 6,7 then 0,1,2).
            var src = new float[] { 100f, 101f, 102f, 103f, 104f };
            int written = ring.Write(src);
            Assert.That(written, Is.EqualTo(5));

            var dst = new float[5];
            int read = ring.Read(dst);
            Assert.That(read, Is.EqualTo(5));
            Assert.That(
                dst,
                Is.EqualTo(src),
                "Wrapped write must reassemble in original order on read."
            );
        }

        [Test]
        public void Read_WrapsAcrossBoundary_PreservesOrder()
        {
            using var ring = new AudioPcmRing(8);
            // Prime: write 4, read 4 — heads at index 4.
            ring.Write(new float[] { -1f, -2f, -3f, -4f });
            var scratch = new float[4];
            ring.Read(scratch);

            // Write 7 — slots 4..7 then wraps to 0..2.
            var src = new float[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f };
            int written = ring.Write(src);
            Assert.That(written, Is.EqualTo(7));

            // Partial read of 3 — pulls slots 4,5,6 (no wrap yet).
            var firstChunk = new float[3];
            int read1 = ring.Read(firstChunk);
            Assert.That(read1, Is.EqualTo(3));
            Assert.That(firstChunk, Is.EqualTo(new float[] { 1f, 2f, 3f }));

            // Remaining 4-sample read crosses the boundary: slot 7 then 0,1,2.
            var secondChunk = new float[4];
            int read2 = ring.Read(secondChunk);
            Assert.That(read2, Is.EqualTo(4));
            Assert.That(secondChunk, Is.EqualTo(new float[] { 4f, 5f, 6f, 7f }));
            Assert.That(ring.Count, Is.EqualTo(0));
        }

        [Test]
        public void Dispose_IsIdempotent_AndShortCircuitsSubsequentOperations()
        {
            var ring = new AudioPcmRing(8);
            ring.Write(new float[] { 1f, 2f });
            ring.Dispose();
            ring.Dispose(); // must not throw.

            Assert.That(ring.Write(new float[] { 3f }), Is.EqualTo(0));
            var dst = new float[4];
            Assert.That(ring.Read(dst), Is.EqualTo(0));
        }

        [Test]
        [Timeout(10_000)]
        public void ProducerConsumerRace_NoLossOrReordering()
        {
            // Strategy: producer streams a monotonically increasing
            // counter; consumer drains into a single growing list. Each
            // partial-write return value tells the producer exactly how
            // many to advance — so the producer never "thinks it wrote"
            // a sample that isn't actually in the ring. After both
            // sides stop, the consumer must have received the producer's
            // entire written prefix in order with no gaps or
            // corruption. This catches: lost samples, duplicated
            // samples, reordered samples, and torn 64-bit index reads.
            const int Capacity = 1024;
            using var ring = new AudioPcmRing(Capacity);

            long producerCounter = 0;
            var stop = new ManualResetEventSlim();
            Exception? producerError = null;
            Exception? consumerError = null;
            var consumed = new List<float>(capacity: 200_000);

            var producer = new Thread(() =>
            {
                try
                {
                    var batch = new float[37]; // intentionally awkward, doesn't divide Capacity.
                    while (!stop.IsSet)
                    {
                        for (int i = 0; i < batch.Length; i++)
                            batch[i] = (float)(producerCounter + i);
                        int written = ring.Write(batch);
                        producerCounter += written;
                        if (written < batch.Length)
                            Thread.Yield(); // give the consumer a chance to drain.
                    }
                }
                catch (Exception ex)
                {
                    producerError = ex;
                }
            })
            {
                IsBackground = true,
                Name = "AudioPcmRingTests.Producer",
            };

            var consumer = new Thread(() =>
            {
                try
                {
                    var buffer = new float[53]; // also awkward.
                    while (!stop.IsSet)
                    {
                        int n = ring.Read(buffer);
                        for (int i = 0; i < n; i++)
                            consumed.Add(buffer[i]);
                    }
                    // Drain whatever the producer published in its last
                    // batch — the consumer should leave the ring empty
                    // so the post-loop assertion can compare the full
                    // produced prefix.
                    int drained;
                    do
                    {
                        drained = ring.Read(buffer);
                        for (int i = 0; i < drained; i++)
                            consumed.Add(buffer[i]);
                    } while (drained > 0);
                }
                catch (Exception ex)
                {
                    consumerError = ex;
                }
            })
            {
                IsBackground = true,
                Name = "AudioPcmRingTests.Consumer",
            };

            // Start consumer first so the producer's first writes can
            // drain immediately and the ring spends most of the test
            // close to half-full rather than perpetually saturated.
            consumer.Start();
            producer.Start();

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
                Thread.Sleep(10);
            stop.Set();

            // Producer stops first; consumer keeps running long enough
            // to drain the residual then exits.
            Assert.That(producer.Join(2000), Is.True, "Producer thread did not exit within 2 s.");
            Assert.That(consumer.Join(2000), Is.True, "Consumer thread did not exit within 2 s.");

            Assert.That(producerError, Is.Null, "Producer threw: " + producerError);
            Assert.That(consumerError, Is.Null, "Consumer threw: " + consumerError);

            // After both threads stop, the consumer should have drained
            // every sample the producer published. The ring is SPSC and
            // the producer is told (via the Write return value) exactly
            // how many made it in, so there should be no surprise loss.
            Assert.That(
                consumed.Count,
                Is.EqualTo((int)producerCounter),
                "Consumer count must equal producer's written prefix length — "
                    + "any mismatch indicates lost or duplicated samples."
            );

            // Order check: samples must be 0, 1, 2, ... in lockstep.
            // Any out-of-order or torn value blows up here.
            for (int i = 0; i < consumed.Count; i++)
            {
                if (consumed[i] != (float)i)
                {
                    Assert.Fail(
                        $"Sample at index {i} is {consumed[i]}, expected {(float)i} — "
                            + "consumer received an out-of-order or corrupted value."
                    );
                }
            }

            // Stress sanity: with a ~1 s budget we should have moved a
            // non-trivial number of samples; if the loops never made
            // contact (e.g. producer never produced because of a bug)
            // the test would pass trivially with consumed.Count == 0.
            Assert.That(
                consumed.Count,
                Is.GreaterThan(Capacity * 4),
                "Race test transferred fewer samples than expected — "
                    + "ring may be deadlocked or the producer/consumer never started."
            );
        }
    }
}

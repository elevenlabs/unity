#nullable enable

using System;
using System.Threading;
using Unity.Collections;

namespace ElevenLabs.Native
{
    /// <summary>
    /// Single-producer / single-consumer lock-free PCM ring buffer that
    /// bridges the managed network thread (producer) and Unity's audio
    /// thread (consumer) for the upcoming
    /// <c>UnityGeneratorAudioOutputEngine</c>. Backed by
    /// <see cref="NativeArray{T}"/> so a future
    /// <c>[BurstCompile]</c> migration of the realtime struct (see
    /// <c>Docs~/plans/audio-generator-engine.md</c> step 3) keeps the
    /// PCM payload in native-accessible memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Concurrency contract: <strong>strictly SPSC</strong>. Exactly one
    /// thread may call <see cref="Write"/> and exactly one other thread
    /// may call <see cref="Read"/>. Concurrent callers on the same role
    /// yield undefined results. <see cref="Dispose"/> is safe to call
    /// once from either side after both producer and consumer have
    /// stopped.
    /// </para>
    /// <para>
    /// Indices are monotonically increasing <see cref="long"/> counters
    /// reduced modulo <see cref="Capacity"/> when accessing the backing
    /// buffer. <see cref="Interlocked.Read(ref long)"/> and
    /// <see cref="Interlocked.Exchange(ref long, long)"/> ensure 64-bit
    /// atomicity and acquire/release ordering across every Unity target
    /// (including 32-bit platforms where naked <see cref="long"/>
    /// reads/writes are not atomic).
    /// </para>
    /// <para>
    /// The producer drops on overflow: <see cref="Write"/> writes only
    /// what fits and returns the count actually written, letting the
    /// caller detect backpressure by comparing against the input span
    /// length. The consumer is non-blocking: <see cref="Read"/> copies
    /// only what's currently available and returns the count actually
    /// read (zero on empty).
    /// </para>
    /// </remarks>
    internal sealed class AudioPcmRing : IDisposable
    {
        private NativeArray<float> _buffer;
        private long _writeIndex;
        private long _readIndex;
        private int _disposed;

        /// <summary>Backing capacity, in samples. Fixed at construction.</summary>
        public int Capacity { get; }

        /// <summary>
        /// Construct a ring with the requested sample capacity.
        /// </summary>
        /// <param name="capacity">Sample count the ring can hold; must be positive.</param>
        /// <param name="allocator">Allocator for the backing
        /// <see cref="NativeArray{T}"/>. Defaults to
        /// <see cref="Allocator.Persistent"/> — a session-scoped ring
        /// outlives any single frame or job.</param>
        public AudioPcmRing(int capacity, Allocator allocator = Allocator.Persistent)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    capacity,
                    "Capacity must be positive."
                );
            Capacity = capacity;
            _buffer = new NativeArray<float>(capacity, allocator, NativeArrayOptions.ClearMemory);
        }

        /// <summary>Sample count currently available to read.</summary>
        /// <remarks>
        /// Snapshot only — by the time the caller acts on the value the
        /// producer may have written more or the consumer drained some.
        /// Use the return value of <see cref="Read"/> / <see cref="Write"/>
        /// for authoritative counts.
        /// </remarks>
        public int Count
        {
            get
            {
                long write = Interlocked.Read(ref _writeIndex);
                long read = Interlocked.Read(ref _readIndex);
                return (int)(write - read);
            }
        }

        /// <summary>Sample slots currently available to write
        /// (<see cref="Capacity"/> minus <see cref="Count"/>). Snapshot.</summary>
        public int Available => Capacity - Count;

        /// <summary>
        /// Copy as many samples from <paramref name="source"/> into the
        /// ring as fit. Returns the count actually written — values less
        /// than <c>source.Length</c> indicate overflow (the tail was
        /// dropped). Returns <c>0</c> when the ring is full or disposed,
        /// or when <paramref name="source"/> is empty. Non-blocking.
        /// </summary>
        /// <remarks>
        /// Must be called from a single producer thread. The producer's
        /// view of the consumer's read index is acquire-loaded via
        /// <see cref="Interlocked.Read(ref long)"/>; the publish of the
        /// new write index is release-stored via
        /// <see cref="Interlocked.Exchange(ref long, long)"/>.
        /// </remarks>
        public int Write(ReadOnlySpan<float> source)
        {
            if (_disposed != 0 || source.IsEmpty)
                return 0;
            long writeIndex = Interlocked.Read(ref _writeIndex);
            long readIndex = Interlocked.Read(ref _readIndex);
            int available = Capacity - (int)(writeIndex - readIndex);
            int toWrite = Math.Min(source.Length, available);
            if (toWrite <= 0)
                return 0;
            int writePos = (int)(writeIndex % Capacity);
            int firstChunk = Math.Min(toWrite, Capacity - writePos);
            for (int i = 0; i < firstChunk; i++)
                _buffer[writePos + i] = source[i];
            int secondChunk = toWrite - firstChunk;
            for (int i = 0; i < secondChunk; i++)
                _buffer[i] = source[firstChunk + i];
            Interlocked.Exchange(ref _writeIndex, writeIndex + toWrite);
            return toWrite;
        }

        /// <summary>
        /// Copy as many samples from the ring into
        /// <paramref name="destination"/> as are available. Returns the
        /// count actually read — less than <c>destination.Length</c>
        /// indicates the ring drained partway (the unfilled tail is
        /// untouched). Returns <c>0</c> when the ring is empty or
        /// disposed, or when <paramref name="destination"/> is empty.
        /// Non-blocking and allocation-free, suitable for the audio
        /// thread.
        /// </summary>
        /// <remarks>
        /// Must be called from a single consumer thread. The consumer's
        /// view of the producer's write index is acquire-loaded via
        /// <see cref="Interlocked.Read(ref long)"/>; the publish of the
        /// new read index is release-stored via
        /// <see cref="Interlocked.Exchange(ref long, long)"/>.
        /// </remarks>
        public int Read(Span<float> destination)
        {
            if (_disposed != 0 || destination.IsEmpty)
                return 0;
            long readIndex = Interlocked.Read(ref _readIndex);
            long writeIndex = Interlocked.Read(ref _writeIndex);
            int count = (int)(writeIndex - readIndex);
            int toRead = Math.Min(destination.Length, count);
            if (toRead <= 0)
                return 0;
            int readPos = (int)(readIndex % Capacity);
            int firstChunk = Math.Min(toRead, Capacity - readPos);
            for (int i = 0; i < firstChunk; i++)
                destination[i] = _buffer[readPos + i];
            int secondChunk = toRead - firstChunk;
            for (int i = 0; i < secondChunk; i++)
                destination[firstChunk + i] = _buffer[i];
            Interlocked.Exchange(ref _readIndex, readIndex + toRead);
            return toRead;
        }

        /// <summary>
        /// Copy as many samples from the ring into
        /// <paramref name="destination"/> as are available <em>without</em>
        /// advancing the read cursor. Returns the count actually peeked.
        /// Subsequent <see cref="Peek"/> / <see cref="Read"/> calls see
        /// the same samples until <see cref="Discard"/> or
        /// <see cref="Read"/> advances past them.
        /// </summary>
        /// <remarks>
        /// Use this with <see cref="Discard"/> when the consumer needs
        /// to read a sliding window where the right edge of the window
        /// is "lookahead" (peeked) and the left edge is "consumed"
        /// (discarded) — e.g., a polyphase FIR resampler whose kernel
        /// reads <c>taps</c> samples but only advances the cursor by
        /// <c>consumed</c> samples per call. Must be called from the
        /// single consumer thread.
        /// </remarks>
        public int Peek(Span<float> destination)
        {
            if (_disposed != 0 || destination.IsEmpty)
                return 0;
            long readIndex = Interlocked.Read(ref _readIndex);
            long writeIndex = Interlocked.Read(ref _writeIndex);
            int count = (int)(writeIndex - readIndex);
            int toRead = Math.Min(destination.Length, count);
            if (toRead <= 0)
                return 0;
            int readPos = (int)(readIndex % Capacity);
            int firstChunk = Math.Min(toRead, Capacity - readPos);
            for (int i = 0; i < firstChunk; i++)
                destination[i] = _buffer[readPos + i];
            int secondChunk = toRead - firstChunk;
            for (int i = 0; i < secondChunk; i++)
                destination[firstChunk + i] = _buffer[i];
            return toRead;
        }

        /// <summary>
        /// Advance the read cursor by <paramref name="count"/> samples,
        /// clamped to the currently available count. No-op when the
        /// ring is empty or disposed, or when <paramref name="count"/>
        /// is non-positive. Must be called from the single consumer
        /// thread.
        /// </summary>
        public void Discard(int count)
        {
            if (_disposed != 0 || count <= 0)
                return;
            long readIndex = Interlocked.Read(ref _readIndex);
            long writeIndex = Interlocked.Read(ref _writeIndex);
            int available = (int)(writeIndex - readIndex);
            int toDiscard = Math.Min(count, available);
            if (toDiscard <= 0)
                return;
            Interlocked.Exchange(ref _readIndex, readIndex + toDiscard);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;
            if (_buffer.IsCreated)
                _buffer.Dispose();
        }
    }
}

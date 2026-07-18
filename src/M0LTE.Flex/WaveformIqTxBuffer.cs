namespace M0LTE.Flex;

/// <summary>
/// A back-pressured ring of interleaved <c>I, Q</c> transmit samples feeding the reflection-driven
/// waveform TX path (<see cref="FlexWaveformIqOutput"/>). The producer (a modulator) pushes a burst
/// with <see cref="Write"/>; the radio pulls it a packet at a time — each keyed TX request drains a
/// packet's worth with <see cref="TakePacket"/>. This is the transport-agnostic core, kept separate
/// so it is unit-testable without a live <c>FlexClient</c>.
/// </summary>
/// <remarks>
/// Unlike DAX-TX (we push at our own cadence — <see cref="FlexAudioOutput"/>), a waveform is
/// <b>reflection-driven</b>: the radio streams TX buffers while keyed and expects one back per
/// buffer (smartsdr-dsp <c>sched_waveform</c>). So <see cref="Write"/> blocks when the ring is full
/// (back-pressure off the radio's drain rate, exactly as a sound-card output blocks on the device),
/// and a momentary starve emits zeros to keep the carrier continuous through a burst rather than
/// glitch it. Samples are host-endian here; the wire conversion is big-endian float32 I/Q (the
/// full-bandwidth stereo class — <see cref="DaxStreamFormat.FullBandwidth"/>).
/// </remarks>
public sealed class WaveformIqTxBuffer
{
    private readonly float[] _ring;
    private readonly object _lock = new();
    private int _head;
    private int _tail;
    private int _count;
    private bool _completed;

    /// <summary>Creates a buffer holding up to <paramref name="capacityPairs"/> complex samples.</summary>
    public WaveformIqTxBuffer(int capacityPairs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityPairs, 1);
        _ring = new float[capacityPairs * 2];
    }

    /// <summary>Complex samples the radio pulled but the ring could not supply (a starved burst).</summary>
    public long SamplesStarved { get; private set; }

    /// <summary>Enqueues interleaved <c>I, Q</c> samples for transmission, blocking while the ring is
    /// full (back-pressure) until the radio drains space or the buffer is <see cref="Complete">
    /// completed</see>. Length must be even (whole pairs).</summary>
    public void Write(ReadOnlySpan<float> interleavedIq)
    {
        if ((interleavedIq.Length & 1) != 0)
        {
            throw new ArgumentException("interleaved IQ length must be even (I,Q pairs)", nameof(interleavedIq));
        }

        int written = 0;
        lock (_lock)
        {
            while (written < interleavedIq.Length)
            {
                while (_count == _ring.Length && !_completed)
                {
                    System.Threading.Monitor.Wait(_lock);
                }

                if (_completed)
                {
                    return;                                    // no more transmission — drop the rest
                }

                int space = _ring.Length - _count;
                int take = Math.Min(space, interleavedIq.Length - written);
                for (int i = 0; i < take; i++)
                {
                    _ring[_head] = interleavedIq[written + i];
                    if (++_head == _ring.Length)
                    {
                        _head = 0;
                    }
                }

                _count += take;
                written += take;
                System.Threading.Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>Fills <paramref name="destination"/> (a whole-pairs span the radio asked for) with the
    /// next queued IQ, zero-padding any shortfall so the carrier stays continuous. Called once per
    /// radio TX-buffer request.</summary>
    public void TakePacket(Span<float> destination)
    {
        lock (_lock)
        {
            int take = Math.Min(destination.Length, _count);
            for (int i = 0; i < take; i++)
            {
                destination[i] = _ring[_tail];
                if (++_tail == _ring.Length)
                {
                    _tail = 0;
                }
            }

            _count -= take;
            if (take < destination.Length)
            {
                destination[take..].Clear();
                SamplesStarved += (destination.Length - take) / 2;
            }

            System.Threading.Monitor.PulseAll(_lock);           // unblock a producer waiting on space
        }
    }

    /// <summary>Blocks until the ring has drained (everything written has been pulled) or
    /// <paramref name="timeout"/> elapses. Returns true if fully drained. The sample-domain half of
    /// PTT release — call it before unkeying so the burst's tail actually goes out.</summary>
    public bool WaitDrained(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_lock)
        {
            while (_count > 0)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return false;
                }

                System.Threading.Monitor.Wait(_lock, (int)remaining);
            }

            return true;
        }
    }

    /// <summary>Marks the stream complete: unblocks any producer waiting in <see cref="Write"/> and
    /// lets it drop the remainder. Idempotent.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            System.Threading.Monitor.PulseAll(_lock);
        }
    }
}

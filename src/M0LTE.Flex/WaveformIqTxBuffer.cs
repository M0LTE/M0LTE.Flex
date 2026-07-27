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
    private long _unconfirmedPadPairs;

    /// <summary>Creates a buffer holding up to <paramref name="capacityPairs"/> complex samples.</summary>
    public WaveformIqTxBuffer(int capacityPairs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityPairs, 1);
        _ring = new float[capacityPairs * 2];
    }

    /// <summary>Complex samples the producer failed to supply <b>while more real IQ was still owed</b>
    /// — a genuine mid-stream underrun, which puts zeros between real samples on air (a phase
    /// discontinuity: the failure the 0.5.0 interlock fix exists to catch). The benign zero-pad of a
    /// burst's <i>drained tail</i> — after the last real sample and before unkey, while the radio keeps
    /// pulling transmit buffers at 187.5/s — does not count: it is silence between the burst and unkey,
    /// not a discontinuity. See <see cref="TakePacket"/> for how the two are told apart.</summary>
    public long SamplesStarved { get; private set; }

    /// <summary>Whether every queued sample has been taken. The signal a waveform uses to
    /// know its post-unkey flush is complete and it may stop emitting.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_lock)
            {
                return _count == 0;
            }
        }
    }

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
    /// <remarks>
    /// A zero-pad only becomes a <see cref="SamplesStarved">starve</see> once a later packet delivers
    /// real IQ — that is what proves the pad went out <i>between</i> real samples (a mid-stream
    /// underrun) rather than after the last one. So the shortfall is held pending and confirmed
    /// retroactively when real data follows; the drained tail before unkey never sees more real data,
    /// so it is discarded (<see cref="DiscardPendingStarve"/>) rather than counted. Counting it at the
    /// moment of the shortfall — as this did before — inflated a clean, fully-delivered burst by the
    /// whole drain-then-unkey window (measured: 0 became ~4600 once the reflected pulls were paced at
    /// the radio's real 187.5/s).
    /// </remarks>
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

            if (take > 0 && _unconfirmedPadPairs > 0)
            {
                // Real IQ followed the pad we were holding: it went to air between real samples, so
                // it was a genuine mid-stream underrun. Confirm it now.
                SamplesStarved += _unconfirmedPadPairs;
                _unconfirmedPadPairs = 0;
            }

            if (take < destination.Length)
            {
                destination[take..].Clear();
                // Hold this packet's shortfall rather than counting it yet — it is a starve only if
                // more real IQ arrives after it (above). If none does, it is the benign drained tail.
                _unconfirmedPadPairs += (destination.Length - take) / 2;
            }

            System.Threading.Monitor.PulseAll(_lock);           // unblock a producer waiting on space
        }
    }

    /// <summary>Drops any zero-pad held pending confirmation in <see cref="TakePacket"/>. The sink calls
    /// this once a burst's tail has gone out and it stops answering, so one burst's benign drained-tail
    /// padding is never mistaken for a mid-stream underrun at the start of the next.</summary>
    internal void DiscardPendingStarve()
    {
        lock (_lock)
        {
            _unconfirmedPadPairs = 0;
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

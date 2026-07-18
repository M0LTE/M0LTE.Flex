using System.Buffers.Binary;

namespace M0LTE.Flex;

/// <summary>
/// Depacketizes FlexRadio DAX-IQ VITA payloads into a bounded jitter/reorder ring, and hands the
/// interleaved <c>I, Q</c> floats to a consumer via a blocking <see cref="Read"/>. This is the
/// transport-agnostic core of <see cref="FlexDaxIqSource"/> (kept separate so it is unit-testable
/// without a live <see cref="FlexClient"/>).
/// </summary>
/// <remarks>
/// <para><b>Wire format (the load-bearing quirk).</b> Unlike DAX <i>audio</i> (big-endian float32),
/// DAX-<b>IQ</b> payloads are <b>little-endian float32, interleaved I/Q</b> — verified on hardware.
/// A 96 kSPS packet carries 512 complex samples plus a 4-byte VITA trailer word; we consume whole
/// pairs and ignore any tail.</para>
/// <para><b>Loss.</b> The VITA 4-bit packet count detects drops (a busy box can drop UDP under the
/// ~180–190 pkt/s DAX-IQ rate); losses are counted, not concealed — a hole simply shortens the ring
/// that round, which reads as a gap the downstream demodulators recover from.</para>
/// </remarks>
public sealed class DaxIqStreamBuffer
{
    private readonly float[] _ring;
    private readonly object _lock = new();
    private int _head;
    private int _tail;
    private int _count;
    private int _lastPacketCount = -1;
    private bool _stopped;

    /// <summary>Creates a buffer holding up to <paramref name="capacityPairs"/> complex samples.</summary>
    public DaxIqStreamBuffer(int capacityPairs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityPairs, 1);
        _ring = new float[capacityPairs * 2];
    }

    /// <summary>Total VITA packets ingested.</summary>
    public long PacketsReceived { get; private set; }

    /// <summary>Complex samples dropped on ring overflow (a consumer not keeping up).</summary>
    public long SamplesDropped { get; private set; }

    /// <summary>Packets the VITA 4-bit counter says went missing before arrival.</summary>
    public long PacketsLost { get; private set; }

    /// <summary>Ingests one DAX-IQ VITA payload (little-endian float32 <c>I, Q</c> pairs). The
    /// <paramref name="packetCount"/> is the VITA 4-bit packet count, used for loss detection.</summary>
    public void Ingest(int packetCount, ReadOnlySpan<byte> payload)
    {
        int pairs = payload.Length / 8;                    // 8 bytes = one complex (I,Q) float32 pair; a trailing <8 tail is ignored
        lock (_lock)
        {
            if (_lastPacketCount >= 0)
            {
                int expected = (_lastPacketCount + 1) & 0xF;
                if (packetCount != expected)
                {
                    PacketsLost += (packetCount - expected) & 0xF;
                }
            }

            _lastPacketCount = packetCount & 0xF;
            PacketsReceived++;

            for (int i = 0; i < pairs; i++)
            {
                float iSample = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(i * 8, 4));
                float qSample = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice((i * 8) + 4, 4));
                PushPair(iSample, qSample);
            }

            if (pairs > 0)
            {
                System.Threading.Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>Reads the next interleaved <c>I, Q</c> block, blocking until at least one pair is
    /// available or the buffer is <see cref="Stop">stopped</see>. Returns the number of
    /// <b>float</b> elements written (always even), or 0 once stopped and drained.</summary>
    public int Read(Span<float> destination)
    {
        lock (_lock)
        {
            while (_count < 2 && !_stopped)
            {
                System.Threading.Monitor.Wait(_lock);
            }

            if (_count < 2)
            {
                return 0;                                   // stopped and drained
            }

            int take = Math.Min(destination.Length & ~1, _count);   // whole pairs; _count is always even
            for (int i = 0; i < take; i++)
            {
                destination[i] = _ring[_tail];
                if (++_tail == _ring.Length)
                {
                    _tail = 0;
                }
            }

            _count -= take;
            return take;
        }
    }

    /// <summary>Unblocks a waiting <see cref="Read"/> and makes it return 0 once the ring drains —
    /// the end-of-stream signal for the pump loop.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            System.Threading.Monitor.PulseAll(_lock);
        }
    }

    // Pairs are pushed/dropped together so the ring never desynchronises I from Q.
    private void PushPair(float i, float q)
    {
        if (_count > _ring.Length - 2)
        {
            _tail += 2;
            if (_tail >= _ring.Length)
            {
                _tail -= _ring.Length;
            }

            _count -= 2;
            SamplesDropped++;
        }

        _ring[_head] = i;
        if (++_head == _ring.Length)
        {
            _head = 0;
        }

        _ring[_head] = q;
        if (++_head == _ring.Length)
        {
            _head = 0;
        }

        _count += 2;
    }
}

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
/// ~180 to 190 pkt/s DAX-IQ rate); losses are counted, not concealed. Each hole is remembered at
/// the place in the stream it happened, whether packets went missing in flight or the ring
/// overflowed because the consumer fell behind, and <see cref="Read"/> ends a block at the last
/// sample before it and adds the hole's size to <see cref="SamplesLost"/> on the block after it.
/// That is the <c>IIqSource</c> gap contract: a consumer can mark the gap instead of splicing
/// across it.</para>
/// </remarks>
public sealed class DaxIqStreamBuffer
{
    // Holes remembered between ingest and read. Holes that overflow has already dropped past are
    // folded into the tail loss before a new one is queued, so only holes still inside the ring
    // take a slot. If it ever fills even so, a new hole is merged into the newest one, so it is
    // still reported, early.
    private const int MaxPendingGaps = 64;

    private readonly float[] _ring;
    private readonly object _lock = new();
    private readonly long[] _gapAt = new long[MaxPendingGaps];
    private readonly long[] _gapPairs = new long[MaxPendingGaps];
    private int _head;
    private int _tail;
    private int _count;
    private int _lastPacketCount = -1;
    private bool _stopped;

    // Stream positions in pairs: _writtenPairs counts every pair pushed, _readPairs is the stream
    // position of the oldest pair still in the ring.
    private long _writtenPairs;
    private long _readPairs;
    private int _gapFirst;
    private int _gapCount;

    // Pairs dropped off the tail on overflow and not yet reported by Read.
    private long _tailLossPairs;

    /// <summary>Creates a buffer holding up to <paramref name="capacityPairs"/> complex samples.</summary>
    public DaxIqStreamBuffer(int capacityPairs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityPairs, 1);
        _ring = new float[capacityPairs * 2];
    }

    /// <summary>The running total of complex samples lost before the blocks <see cref="Read"/> has
    /// returned: ring overflow, plus an estimate for packets missing in flight (the missing packet
    /// count times the size of the packet that arrived after them). Changes only inside
    /// <see cref="Read"/>, and an increase means the hole lies immediately before the block just
    /// returned.</summary>
    public long SamplesLost { get; private set; }

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
            PacketsReceived++;
            if (_lastPacketCount >= 0)
            {
                int expected = (_lastPacketCount + 1) & 0xF;
                int lostPackets = (packetCount - expected) & 0xF;

                // A count at or up to 7 behind the last one is a packet that arrived late
                // (reordered in flight) or twice. Read as a jump forward it would be a phantom hole
                // of 8 to 15 packets that splits the stream (0, 1, 3, 2, 4 would report 1, 14 and 1
                // missing), so it is dropped instead: no hole, its data discarded (a late packet's
                // place was already reported as a hole when the one after it arrived), and the last
                // count left alone. The cost is that a genuine burst of 8 to 15 lost packets,
                // roughly 45 to 80 ms at 96 kSPS, is misread as one late packet and its hole goes
                // unreported; the 4-bit counter cannot tell the two apart.
                if (lostPackets >= 8)
                {
                    return;
                }

                if (lostPackets > 0)
                {
                    PacketsLost += lostPackets;

                    // The hole is just before this packet's first pair. Its size is an estimate:
                    // the radio's packets are all one size at a given rate, so the one that arrived
                    // stands in for the ones that did not.
                    RecordGap(_writtenPairs, (long)lostPackets * Math.Max(pairs, 1));
                }
            }

            _lastPacketCount = packetCount & 0xF;

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
    /// <b>float</b> elements written (always even), or 0 once stopped and drained. A block never
    /// spans a hole in the stream: it ends at the last pair before one, and the read that returns
    /// the first pair after it adds the hole's size to <see cref="SamplesLost"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> holds less than one
    /// pair, so a read could return nothing while data waits.</exception>
    public int Read(Span<float> destination)
    {
        if (destination.Length < 2)
        {
            throw new ArgumentException("the destination must hold at least one I,Q pair", nameof(destination));
        }

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

            // Every hole at or behind the oldest pair still held lies immediately before this
            // block: overflow drops off the tail, and any in-flight loss whose position has been
            // dropped past or reached.
            long lost = _tailLossPairs;
            _tailLossPairs = 0;
            while (_gapCount > 0 && _gapAt[_gapFirst] <= _readPairs)
            {
                lost += _gapPairs[_gapFirst];
                _gapFirst = (_gapFirst + 1) % MaxPendingGaps;
                _gapCount--;
            }

            // Stop the block at the next hole still ahead.
            int available = _count;                         // whole pairs; _count is always even
            if (_gapCount > 0)
            {
                long pairsToGap = _gapAt[_gapFirst] - _readPairs;
                available = (int)Math.Min(available, pairsToGap * 2);
            }

            int take = Math.Min(destination.Length & ~1, available);
            for (int i = 0; i < take; i++)
            {
                destination[i] = _ring[_tail];
                if (++_tail == _ring.Length)
                {
                    _tail = 0;
                }
            }

            _count -= take;
            _readPairs += take / 2;
            SamplesLost += lost;
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
            _readPairs++;
            _tailLossPairs++;
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
        _writtenPairs++;
    }

    // Remembers a hole of `pairs` complex samples just before stream position `at`. Called under
    // the lock.
    private void RecordGap(long at, long pairs)
    {
        // Holes at or behind the oldest pair still held lie before the next block anyway; moving
        // them to the tail loss keeps a stalled reader's queue from filling with them.
        while (_gapCount > 0 && _gapAt[_gapFirst] <= _readPairs)
        {
            _tailLossPairs += _gapPairs[_gapFirst];
            _gapFirst = (_gapFirst + 1) % MaxPendingGaps;
            _gapCount--;
        }

        if (_gapCount > 0)
        {
            int newest = (_gapFirst + _gapCount - 1) % MaxPendingGaps;
            if (_gapAt[newest] == at || _gapCount == MaxPendingGaps)
            {
                _gapPairs[newest] += pairs;
                return;
            }
        }

        int slot = (_gapFirst + _gapCount) % MaxPendingGaps;
        _gapAt[slot] = at;
        _gapPairs[slot] = pairs;
        _gapCount++;
    }
}

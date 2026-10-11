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
/// <para><b>A stream that stops and restarts.</b> The counter wraps every 16 packets, so it
/// cannot measure a silence longer than that, and a stream the radio restarts begins its count
/// again wherever it likes: read naively, the new count is a hole of the wrong size, no hole at
/// all, or a run of "late" packets that the reorder guard below would throw away unmarked. Built
/// with a sample rate and a <see cref="TimeProvider"/>, the buffer also times the arrivals. A
/// silence longer than the counter's 16-packet cycle is either the radio's (the stream stopped,
/// or restarted with a new count) or ours (the receive thread was held up while the packets
/// waited in the socket), and only the packets after it can say which: ours ends in a burst, the
/// socket's backlog delivered back to back, while a stream the radio resumes arrives at its own
/// pace. So a silence that ends on a count that does not follow on leaves its hole undecided until
/// the next packet arrives, and <see cref="Read"/> holds the samples after the hole until then
/// (one packet's delay, and only after such a silence). If the next packet comes within half a
/// packet's time it is a burst and the counter is believed, so a stall of ours can never inflate a
/// small real loss into the length of the stall; otherwise the hole is the silence's wall-clock
/// length at the stream's rate, less the packet that ended it. A silence that ends on a count that
/// does follow on is taken at the counter's word: that is exactly what a stall of ours that lost
/// nothing looks like. A count behind the last one (a repeat of it included) after a silence is
/// decided the same way: in a burst it is a late or repeated packet out of the socket after a stall
/// of ours, and is taken back out of the ring with no hole; at the stream's own pace it is a
/// restarted stream that landed behind the old count, and is kept with the silence as its
/// hole.</para>
/// </remarks>
public sealed class DaxIqStreamBuffer
{
    // Holes remembered between ingest and read. Holes that overflow has already dropped past are
    // folded into the tail loss before a new one is queued, so only holes still inside the ring
    // take a slot. If it ever fills even so, a new hole is merged into the newest one, so it is
    // still reported, early.
    private const int MaxPendingGaps = 64;

    // The counter's cycle: after a silence this many packets long the 4-bit count says nothing.
    private const int CounterCycle = 16;

    private readonly float[] _ring;
    private readonly object _lock = new();
    private readonly TimeProvider? _timeProvider;
    private readonly int _sampleRate;
    private long _lastArrival;
    private int _lastPairs;

    // A hole after a silence, waiting for the next packet to say whether it was ours or the
    // radio's (see the remarks). Read does not pass _pendingAt while _pending is set.
    private bool _pending;
    private long _pendingAt;
    private long _pendingCounterPackets;
    private long _pendingCounterPairs;
    private long _pendingTimePairs;
    private long _pendingArrival;
    private int _pendingPacketPairs;

    // Set when the undecided packet is one behind the last count: in a burst it is a late or
    // repeated packet and comes back out of the ring, and the count and arrival before it return.
    private bool _pendingBehind;
    private int _pendingPushedPairs;
    private int _priorPacketCount;
    private long _priorArrival;
    private int _priorPairs;
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

    /// <summary>Creates a buffer holding up to <paramref name="capacityPairs"/> complex samples
    /// that also times packet arrivals on <paramref name="timeProvider"/>, so a stream that falls
    /// silent for longer than the packet counter's 16-packet cycle, or that the radio restarts with
    /// its counter starting over, is reported as a hole of the silence's length at
    /// <paramref name="sampleRate"/>, rather than whatever the 4-bit counter happens to say.</summary>
    public DaxIqStreamBuffer(int capacityPairs, int sampleRate, TimeProvider timeProvider)
        : this(capacityPairs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _sampleRate = sampleRate;
        _timeProvider = timeProvider;
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

    /// <summary>Packets that went missing before arrival: what the VITA 4-bit counter says, or,
    /// for a silence of the radio's longer than the counter's cycle (timed arrivals only), the
    /// silence's length in packets of the size that ended it.</summary>
    public long PacketsLost { get; private set; }

    /// <summary>Ingests one DAX-IQ VITA payload (little-endian float32 <c>I, Q</c> pairs). The
    /// <paramref name="packetCount"/> is the VITA 4-bit packet count, used for loss detection.</summary>
    public void Ingest(int packetCount, ReadOnlySpan<byte> payload)
    {
        int pairs = payload.Length / 8;                    // 8 bytes = one complex (I,Q) float32 pair; a trailing <8 tail is ignored
        long now = _timeProvider?.GetTimestamp() ?? 0;
        lock (_lock)
        {
            PacketsReceived++;

            // This packet's arrival is what tells a stall of ours from a silence of the radio's.
            if (_pending)
            {
                ResolvePending(Burst(now));
            }

            if (_lastPacketCount >= 0)
            {
                int expected = (_lastPacketCount + 1) & 0xF;
                int lostPackets = (packetCount - expected) & 0xF;
                long silence = lostPackets != 0 ? SilenceHole(now, pairs) : 0;

                if (lostPackets >= 8 && silence > 0 && 2 * pairs <= _ring.Length)
                {
                    // Behind the last count after a silence: a late or repeated packet out of the
                    // socket after a stall of ours, or a stream the radio restarted behind the old
                    // count. Kept for now, behind the undecided hole, and taken back out if the
                    // next packet shows a burst.
                    _priorPacketCount = _lastPacketCount;
                    _priorArrival = _lastArrival;
                    _priorPairs = _lastPairs;
                    OpenPending(0, pairs, silence, now);
                    _pendingBehind = true;
                    _pendingPushedPairs = pairs;
                    Accept(packetCount, pairs, now, payload);
                    return;
                }

                // After a silence the counter cannot span, a count that does not follow on is a
                // restarted stream, a wrapped counter, or a stall of ours with a real loss in it.
                if (lostPackets > 0 && silence > 0)
                {
                    OpenPending(lostPackets, pairs, silence, now);
                    Accept(packetCount, pairs, now, payload);
                    return;
                }

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

            Accept(packetCount, pairs, now, payload);
        }
    }

    // Takes a packet into the ring and makes it the one the next count and arrival are measured
    // from. Called under the lock.
    private void Accept(int packetCount, int pairs, long now, ReadOnlySpan<byte> payload)
    {
        _lastPacketCount = packetCount & 0xF;
        _lastArrival = now;
        _lastPairs = pairs;

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

    // Opens the undecided hole just before the packet about to be accepted: the counter's say
    // (counterPackets of this packet's size) if the next packet comes in a burst, the silence's
    // length otherwise. Called under the lock.
    private void OpenPending(long counterPackets, int pairs, long silencePairs, long now)
    {
        _pending = true;
        _pendingBehind = false;
        _pendingAt = _writtenPairs;
        _pendingCounterPackets = counterPackets;
        _pendingCounterPairs = counterPackets * Math.Max(pairs, 1);
        _pendingTimePairs = Math.Max(silencePairs, _pendingCounterPairs);
        _pendingArrival = now;
        _pendingPacketPairs = Math.Max(pairs, 1);
    }

    // True when a packet arriving at `now` came within half a packet's time of the one that ended
    // the silence: the socket's backlog after a stall of ours, not a stream resuming at its own
    // pace. Called under the lock.
    private bool Burst(long now)
    {
        double seconds = _timeProvider!.GetElapsedTime(_pendingArrival, now).TotalSeconds;
        return seconds < 0.5 * _pendingPacketPairs / _sampleRate;
    }

    // Decides the undecided hole and lets Read past it. Called under the lock.
    private void ResolvePending(bool burst)
    {
        _pending = false;
        if (_pendingBehind && burst)
        {
            // A late or repeated packet after a stall of ours: no hole, and it never happened.
            // Read has not passed the hole, so the packet is still the newest thing in the ring.
            Retract(_pendingPushedPairs);
            _lastPacketCount = _priorPacketCount;
            _lastArrival = _priorArrival;
            _lastPairs = _priorPairs;
            System.Threading.Monitor.PulseAll(_lock);
            return;
        }

        long pairs = burst ? _pendingCounterPairs : _pendingTimePairs;
        PacketsLost += burst
            ? _pendingCounterPackets
            : (pairs + _pendingPacketPairs - 1) / _pendingPacketPairs;
        if (pairs > 0)
        {
            RecordGap(_pendingAt, pairs);
        }

        System.Threading.Monitor.PulseAll(_lock);
    }

    // The pairs a silence since the last accepted packet stands for, when it outlasted the
    // counter's cycle; 0 when arrivals are not timed or the silence was shorter. The packet that
    // ended the silence covers the end of it, so its own pairs are not counted. Called under the
    // lock.
    private long SilenceHole(long now, int pairs)
    {
        if (_timeProvider is null || _lastPairs <= 0)
        {
            return 0;
        }

        double seconds = _timeProvider.GetElapsedTime(_lastArrival, now).TotalSeconds;
        double cycleSeconds = (double)CounterCycle * _lastPairs / _sampleRate;
        if (seconds <= cycleSeconds)
        {
            return 0;
        }

        return Math.Max(0, (long)Math.Round(seconds * _sampleRate) - pairs);
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
            // Nor past an undecided hole: the samples after it wait for the next packet.
            while ((_count < 2 || (_pending && _readPairs >= _pendingAt)) && !_stopped)
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
            if (_pending)
            {
                available = (int)Math.Min(available, (_pendingAt - _readPairs) * 2);
            }

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
            // No packet is coming to decide an undecided hole: take the counter's word.
            if (_pending)
            {
                ResolvePending(burst: true);
            }

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

    // Takes the newest `pairs` pairs back out of the ring. Called under the lock.
    private void Retract(int pairs)
    {
        int floats = Math.Min(pairs * 2, _count);
        _head -= floats;
        if (_head < 0)
        {
            _head += _ring.Length;
        }

        _count -= floats;
        _writtenPairs -= floats / 2;
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

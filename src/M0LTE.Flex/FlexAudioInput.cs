
namespace M0LTE.Flex;

/// <summary>
/// A FlexRadio DAX-RX audio source: depacketizes the radio's VITA-49 DAX packets (filtered
/// to our RX stream id) into normalised floats at the DAX rate, through a small
/// reorder/loss-concealing jitter ring. Presented as an <see cref="IAudioInput"/> at the
/// DAX rate; the daemon decimates to the DSP rate. See docs/flex-integration.md §4.
/// </summary>
/// <remarks>
/// The reorder ring is a port of nDAX <c>readPacketsBuffered</c> (© Andrew Rodland KC2G,
/// MIT): a 16-slot buffer keyed by the VITA 4-bit packet count, emitting in order once the
/// stream is <c>packetBuffer</c> packets ahead and concealing a lost slot by repeating the
/// last payload.
/// </remarks>
public sealed class FlexAudioInput : IAudioInput, IDisposable
{
    private const int SlotCount = 16;

    private readonly FlexClient _client;
    private readonly uint _streamId;
    private readonly DaxStreamFormat _format;
    private readonly int _packetBuffer;
    private readonly Action<VitaPreamble, byte[]> _handler;

    private readonly object _lock = new();
    private readonly float[]?[] _slots = new float[]?[SlotCount];
    private readonly Queue<float[]> _ready = new();
    private float[] _lastPayload;
    private float[] _head = [];
    private int _headOffset;
    private int _available;
    private int _readPoint = -1;
    private bool _closed;

    /// <summary>Number of DAX packets received (diagnostics).</summary>
    private long _received;

    /// <summary>Number of concealed lost packets (diagnostics).</summary>
    private long _lost;

    /// <summary>Subscribes to <paramref name="client"/>'s DAX packets for
    /// <paramref name="streamId"/>.</summary>
    /// <param name="client">The shared session.</param>
    /// <param name="streamId">The DAX-RX stream id from <c>stream create type=dax_rx</c>.</param>
    /// <param name="format">The DAX transport format.</param>
    /// <param name="packetBuffer">Reorder depth in packets (nDAX default 3; deeper on a
    /// loaded box, e.g. for ARDOP).</param>
    public FlexAudioInput(FlexClient client, uint streamId, DaxStreamFormat format, int packetBuffer = 3)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfNegative(packetBuffer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(packetBuffer, SlotCount - 2);
        _client = client;
        _streamId = streamId;
        _format = format;
        _packetBuffer = packetBuffer;
        _lastPayload = new float[format.SamplesPerPacket];
        _handler = OnVitaPacket;
        client.VitaPacketReceived += _handler;
    }

    /// <inheritdoc />
    public int SampleRate => _format.SampleRate;

    /// <summary>Total DAX packets received.</summary>
    public long PacketsReceived => Interlocked.Read(ref _received);

    /// <summary>Total packets concealed as lost.</summary>
    public long PacketsLost => Interlocked.Read(ref _lost);

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        lock (_lock)
        {
            while (_available == 0 && !_closed)
            {
                // Wake periodically so a caller loop can re-check its own cancellation.
                Monitor.Wait(_lock, 200);
                if (_available == 0 && !_closed)
                {
                    return 0;
                }
            }

            int toCopy = Math.Min(destination.Length, _available);
            int written = 0;
            while (written < toCopy)
            {
                if (_headOffset >= _head.Length)
                {
                    _head = _ready.Dequeue();
                    _headOffset = 0;
                }

                int chunk = Math.Min(_head.Length - _headOffset, toCopy - written);
                _head.AsSpan(_headOffset, chunk).CopyTo(destination[written..]);
                _headOffset += chunk;
                written += chunk;
            }

            _available -= written;
            return written;
        }
    }

    private void OnVitaPacket(VitaPreamble preamble, byte[] payload)
    {
        if (preamble.StreamId != _streamId
            || preamble.ClassId.PacketClassCode != _format.PacketClassCode)
        {
            return;
        }

        Interlocked.Increment(ref _received);
        var samples = new float[payload.Length / _format.BytesPerSample];
        _format.Depacketize(payload, samples);

        int count = preamble.PacketCount & (SlotCount - 1);
        lock (_lock)
        {
            _slots[count] = samples;
            if (_readPoint == -1)
            {
                _readPoint = count;
            }

            int ahead = (SlotCount + count - _readPoint) % SlotCount;
            while (ahead >= _packetBuffer)
            {
                float[]? slot = _slots[_readPoint];
                if (slot is not null)
                {
                    Emit(slot);
                    _lastPayload = slot;
                    _slots[_readPoint] = null;
                }
                else
                {
                    _lost++;
                    Emit(_lastPayload);
                }

                _readPoint = (_readPoint + 1) % SlotCount;
                ahead = (SlotCount + count - _readPoint) % SlotCount;
            }

            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Drains any packets still held in the reorder ring, in order — for a finite
    /// stream (e.g. replaying a captured buffer) where no further packets will arrive to
    /// push the last <c>packetBuffer − 1</c> out. A live DAX stream never needs this.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_readPoint < 0)
            {
                return;
            }

            for (int k = 0; k < SlotCount; k++)
            {
                int index = (_readPoint + k) % SlotCount;
                if (_slots[index] is float[] slot)
                {
                    Emit(slot);
                    _slots[index] = null;
                }
            }

            Monitor.PulseAll(_lock);
        }
    }

    // Caller holds _lock.
    private void Emit(float[] samples)
    {
        _ready.Enqueue(samples);
        _available += samples.Length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.VitaPacketReceived -= _handler;
        lock (_lock)
        {
            _closed = true;
            Monitor.PulseAll(_lock);
        }
    }
}

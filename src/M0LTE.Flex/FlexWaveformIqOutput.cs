namespace M0LTE.Flex;

/// <summary>
/// The transmit sink for a FlexRadio <b>waveform</b>: complex baseband (interleaved <c>I, Q</c>)
/// written here is transmitted as RF via the Waveform API. Present it the modulator's IQ with
/// <see cref="Write"/> and release with <see cref="Drain"/> — the transmit counterpart to
/// <see cref="FlexAudioOutput"/> for wideband IQ (docs/flex-integration.md §9.2/§9.5).
/// </summary>
/// <remarks>
/// A waveform is <b>reflection-driven</b>: while keyed, the radio streams TX buffers (the
/// full-bandwidth IF-data class, odd stream id — smartsdr-dsp <c>sched_waveform</c>) and expects one
/// packet back per buffer. So this sink does not push on its own clock; it subscribes to
/// <see cref="FlexClient.VitaPacketReceived"/> and, for each TX request, reflects the next
/// <see cref="WaveformIqTxBuffer">buffered</see> IQ (zero-padded on a starve) using the same stream
/// id, packetized big-endian as the full-bandwidth stereo class (<see cref="DaxStreamFormat.FullBandwidth"/>).
/// RX buffers (even stream id) and other streams (meter/FFT) are ignored. The achievable on-air
/// bandwidth depends on the waveform's <c>underlying_mode</c> — <c>RAW</c> gives true wideband
/// complex IQ→RF, capped by the 24 kHz waveform rate (§9.5).
/// </remarks>
public sealed class FlexWaveformIqOutput : IDisposable
{
    /// <summary>The waveform stream's complex sample rate: 24 kHz (the 6000-series waveform rate).</summary>
    public const int SampleRate = 24000;

    private readonly FlexClient _client;
    private readonly WaveformIqTxBuffer _buffer;
    private readonly Action<VitaPreamble, byte[]> _onVita;
    private readonly float[] _reflect = new float[4096];       // scratch for one reflected packet (single event thread)
    private int _packetCount;
    private int _disposed;

    /// <summary>Creates a waveform TX sink over <paramref name="client"/>, buffering up to
    /// <paramref name="bufferSeconds"/> of IQ (back-pressure past that).</summary>
    public FlexWaveformIqOutput(FlexClient client, double bufferSeconds = 3.0)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _buffer = new WaveformIqTxBuffer(Math.Max(SampleRate / 2, (int)(SampleRate * bufferSeconds)));
        _onVita = OnVita;
        client.VitaPacketReceived += _onVita;
    }

    /// <summary>Waveform TX packets reflected to the radio.</summary>
    public long PacketsReflected { get; private set; }

    /// <summary>Complex samples the radio pulled that the buffer could not supply (a starved burst).</summary>
    public long SamplesStarved => _buffer.SamplesStarved;

    /// <summary>Enqueues interleaved <c>I, Q</c> samples (at <see cref="SampleRate"/>) for
    /// transmission. Blocks while the buffer is full — back-pressure off the radio's drain rate.
    /// Key the PTT first so the radio is pulling.</summary>
    public void Write(ReadOnlySpan<float> interleavedIq) => _buffer.Write(interleavedIq);

    /// <summary>Waits (up to <paramref name="timeout"/>) for the buffered IQ to finish going out —
    /// the sample-domain half of PTT release. Unkey after this returns. Returns true if fully drained.</summary>
    public bool Drain(TimeSpan timeout) => _buffer.WaitDrained(timeout);

    private void OnVita(VitaPreamble preamble, byte[] packet)
    {
        // Reflect only the waveform's TX buffers: the full-bandwidth IF-data class with an odd
        // stream id (even = the waveform RX path; other classes = meter/FFT). Reply with the same
        // stream id the radio addressed us on.
        if (preamble.ClassId.PacketClassCode != DaxStreamFormat.FullBandwidth.PacketClassCode
            || (preamble.StreamId & 1) == 0)
        {
            return;
        }

        int floats = Math.Min(preamble.PayloadLength / 4, _reflect.Length) & ~1;   // whole I/Q pairs
        if (floats == 0)
        {
            return;
        }

        Span<float> block = _reflect.AsSpan(0, floats);
        _buffer.TakePacket(block);
        byte[] reply = DaxStreamFormat.FullBandwidth.BuildPacket(preamble.StreamId, _packetCount, block);
        _packetCount = (_packetCount + 1) & 0x0F;
        _client.SendVita(reply);
        PacketsReflected++;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _client.VitaPacketReceived -= _onVita;
        _buffer.Complete();
    }
}

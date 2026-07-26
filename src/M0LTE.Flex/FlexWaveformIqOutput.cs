namespace M0LTE.Flex;

/// <summary>
/// The transmit sink for a FlexRadio <b>waveform</b>: complex baseband (interleaved <c>I, Q</c>)
/// written here is transmitted as RF via the Waveform API. Present it the modulator's IQ with
/// <see cref="Write"/> and release with <see cref="Drain"/> — the transmit counterpart to
/// <see cref="FlexAudioOutput"/> for complex IQ (docs/flex-integration.md §9.2/§9.5).
/// </summary>
/// <remarks>
/// A waveform is <b>reflection-driven</b>: while keyed, the radio streams TX buffers (the
/// full-bandwidth IF-data class, odd stream id — smartsdr-dsp <c>sched_waveform</c>) and expects one
/// packet back per buffer. So this sink does not push on its own clock; it subscribes to
/// <see cref="FlexClient.VitaPacketReceived"/> and, for each TX request, reflects the next
/// <see cref="WaveformIqTxBuffer">buffered</see> IQ (zero-padded on a starve) using the same stream
/// id, packetized big-endian as the full-bandwidth stereo class (<see cref="DaxStreamFormat.FullBandwidth"/>).
/// RX buffers (even stream id) and other streams (meter/FFT) are ignored.
/// </remarks>
/// <remarks>
/// <b>Achievable on-air bandwidth</b> (measured, FLEX-6500 fw 4.1.5, 2026-07-26): the transmit path
/// is <b>single-sideband</b> — only the <b>negative</b> half of the baseband is transmitted, in every
/// <c>underlying_mode</c> — and the surviving half is capped by the radio's transmit filter
/// (<see cref="FlexWaveformOptions.TransmitFilterHighHz"/>), which defaults to 3 kHz and clamps at
/// 10 kHz. So the usable width is about <b>10 kHz one-sided</b>, not ±12 kHz. Only the <b>negative</b>
/// half of the baseband is transmitted, in every mode — put the whole signal below DC. Whether it
/// then lands below the carrier upright (<c>RAW</c>/<c>LSB</c>/<c>DIGL</c>) or above it mirrored
/// (<c>IQ</c>/<c>USB</c>/<c>DIGU</c>) is the mode's doing;
/// <see cref="FlexWaveformOptions.OccupiedBandwidthHz">band placement</see> handles it for you.
/// </remarks>
public sealed class FlexWaveformIqOutput : IDisposable
{
    /// <summary>The waveform stream's complex sample rate: 24 kHz (the 6000-series waveform rate).</summary>
    public const int SampleRate = 24000;

    private readonly FlexClient _client;
    private readonly WaveformIqTxBuffer _buffer;
    private readonly Action<VitaPacket> _onVita;
    private readonly float[] _reflect = new float[4096];       // scratch for one reflected packet (single event thread)
    private readonly double _shiftStep;                        // NCO radians/sample; 0 = pass through
    private readonly float[] _shiftScratch = new float[4096];  // reused, so Write stays allocation-free
    private double _shiftPhase;
    private int _packetCount;
    private int _disposed;

    /// <summary>Creates a waveform TX sink over <paramref name="client"/>, buffering up to
    /// <paramref name="bufferSeconds"/> of IQ (back-pressure past that), optionally shifting every
    /// sample by <paramref name="shiftHz"/> on the way in.</summary>
    /// <param name="client">The shared session.</param>
    /// <param name="bufferSeconds">How much IQ to buffer before <see cref="Write"/> back-pressures.</param>
    /// <param name="shiftHz">
    /// Frequency shift applied to written samples, in Hz. <see cref="FlexWaveform"/> uses this to move
    /// a caller's baseband into the sideband the radio actually transmits. It is a true frequency
    /// translation, <b>not</b> a spectral mirror — conjugating would land the signal in the same place
    /// while inverting it, which no receiver would decode.
    /// </param>
    public FlexWaveformIqOutput(FlexClient client, double bufferSeconds = 3.0, double shiftHz = 0)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _shiftStep = 2 * Math.PI * shiftHz / SampleRate;
        _buffer = new WaveformIqTxBuffer(Math.Max(SampleRate / 2, (int)(SampleRate * bufferSeconds)));
        _onVita = OnVita;
        _onStatus = OnStatus;
        client.VitaPacketReceived += _onVita;
        client.StatusUpdated += _onStatus;
    }

    private readonly Action<FlexStatusUpdate> _onStatus;
    private volatile bool _transmitting;
    private volatile bool _flushing;

    /// <summary>Whether the sink is currently answering the radio's transmit-buffer requests.</summary>
    public bool Transmitting => _transmitting;

    /// <summary>
    /// Tracks the interlock so IQ is emitted only while the radio is actually transmitting.
    /// </summary>
    /// <remarks>
    /// <para>The radio asks for transmit buffers <b>continuously</b> once a slice is in a
    /// waveform mode — measured at the full 187.5/s whether keyed or not, and continuing
    /// indefinitely after <c>xmit 0</c> with the interlock parked in UNKEY_REQUESTED. That is
    /// normal: FlexRadio's own waveform SDK (<c>smartsdr-dsp</c>, <c>sched_waveform.c</c>)
    /// emits only between PTT and the post-unkey flush, setting <c>inhibit_tx</c> on
    /// UNKEY_REQUESTED and staying silent until the next key.</para>
    /// <para>Answering unconditionally, as this class previously did, drains the transmit ring
    /// around the clock: samples queued before the PA comes up are consumed and discarded, so
    /// a burst goes out truncated with no error and a starve count of zero. Gating on the
    /// interlock is what makes "queue a burst, then key" mean what it says.</para>
    /// </remarks>
    private void OnStatus(FlexStatusUpdate update)
    {
        if (!update.Object.StartsWith("interlock", StringComparison.OrdinalIgnoreCase)
            || !update.Updated.TryGetValue("state", out string? state))
        {
            return;
        }

        switch (state)
        {
            case "PTT_REQUESTED":
            case "TRANSMITTING":
                _flushing = false;
                _transmitting = true;
                break;

            case "UNKEY_REQUESTED":
                // Keep emitting until the ring empties, so the tail of the burst reaches the
                // air, then fall silent — the SDK's flush-then-inhibit sequence.
                _flushing = true;
                break;

            default:
                _flushing = false;
                _transmitting = false;
                break;
        }
    }

    /// <summary>Waveform TX packets reflected to the radio.</summary>
    public long PacketsReflected { get; private set; }

    /// <summary>Complex samples the radio pulled that the buffer could not supply (a starved burst).</summary>
    public long SamplesStarved => _buffer.SamplesStarved;

    /// <summary>Enqueues interleaved <c>I, Q</c> samples (at <see cref="SampleRate"/>) for
    /// transmission. Blocks while the buffer is full — back-pressure off the radio's drain rate.
    /// Key the PTT first so the radio is pulling.</summary>
    public void Write(ReadOnlySpan<float> interleavedIq)
    {
        if (_shiftStep == 0)
        {
            _buffer.Write(interleavedIq);
            return;
        }

        // Shift in reused chunks: called on the producer thread, once per sample, so it must not
        // allocate. Phase runs on across chunks and across calls, keeping the output continuous.
        while (!interleavedIq.IsEmpty)
        {
            int floats = Math.Min(interleavedIq.Length, _shiftScratch.Length) & ~1;
            ReadOnlySpan<float> source = interleavedIq[..floats];
            Span<float> destination = _shiftScratch.AsSpan(0, floats);

            for (int n = 0; n < floats; n += 2)
            {
                (double sin, double cos) = Math.SinCos(_shiftPhase);
                double i = source[n];
                double q = source[n + 1];
                destination[n] = (float)((i * cos) - (q * sin));
                destination[n + 1] = (float)((i * sin) + (q * cos));

                _shiftPhase += _shiftStep;
                if (_shiftPhase > Math.PI)
                {
                    _shiftPhase -= 2 * Math.PI;
                }
                else if (_shiftPhase < -Math.PI)
                {
                    _shiftPhase += 2 * Math.PI;
                }
            }

            _buffer.Write(destination);
            interleavedIq = interleavedIq[floats..];
        }
    }

    /// <summary>Waits (up to <paramref name="timeout"/>) for the buffered IQ to finish going out —
    /// the sample-domain half of PTT release. Unkey after this returns. Returns true if fully drained.</summary>
    public bool Drain(TimeSpan timeout) => _buffer.WaitDrained(timeout);

    private void OnVita(VitaPacket packet)
    {
        // Reflect only the waveform's TX buffers: the full-bandwidth IF-data class with an odd
        // stream id (even = the waveform RX path; other classes = meter/FFT). Reply with the same
        // stream id the radio addressed us on.
        if (packet.PacketClassCode != DaxStreamFormat.FullBandwidth.PacketClassCode
            || (packet.StreamId & 1) == 0)
        {
            return;
        }

        // Only answer while transmitting. Outside that window the radio is still asking, but
        // anything we hand it is discarded — and taking it from the ring would throw away a
        // burst that has been queued for the next transmission.
        if (!_transmitting)
        {
            return;
        }

        int floats = Math.Min(packet.Payload.Length / 4, _reflect.Length) & ~1;   // whole I/Q pairs
        if (floats == 0)
        {
            return;
        }

        Span<float> block = _reflect.AsSpan(0, floats);
        _buffer.TakePacket(block);
        byte[] reply = DaxStreamFormat.FullBandwidth.BuildPacket(packet.StreamId, _packetCount, block);
        _packetCount = (_packetCount + 1) & 0x0F;
        _client.SendVita(reply);
        PacketsReflected++;

        if (_flushing && _buffer.IsEmpty)
        {
            // Tail delivered. Stop answering until the next key — the radio does not announce
            // its return to RECEIVE on this path, so the ring emptying is the only signal.
            _flushing = false;
            _transmitting = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _client.VitaPacketReceived -= _onVita;
        _client.StatusUpdated -= _onStatus;
        _buffer.Complete();
    }
}

using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>
/// The Waveform-API IQ transmit path against the in-process <see cref="MockFlexRadio"/>: register a
/// waveform, key, and confirm the complex IQ we <see cref="FlexWaveformIqOutput.Write">write</see>
/// is reflected back to the radio byte-exact (the wideband IQ TX path — docs/flex-integration.md
/// §9.2). Runs entirely in-process, no hardware.
/// </summary>
public sealed class FlexWaveformTests
{
    [Fact]
    public async Task Written_iq_is_reflected_to_the_radio_in_order()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        await using var _ = mock;

        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        // In-process delivery (as flex:mock wires it) — deterministic, no UDP loss/reordering.
        mock.RxDelivery = client.DeliverVitaPacket;
        client.VitaSendHook = mock.DeliverTxPacket;

        await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions { SliceFrequencyMhz = 14.1 });
        using FlexWaveformIqOutput iq = waveform.CreateIqOutput();
        FlexPtt ptt = waveform.CreatePtt();

        waveform.SliceIndex.Should().Be("0");
        waveform.WaveformName.Should().Be("PdnWfm");

        float[] burst = MakeIqBurst(pairs: 640); // 5 waveform packets of 128 complex

        iq.Write(burst);                          // buffered before keying
        ptt.Key();                                // radio starts streaming TX buffers
        iq.Drain(TimeSpan.FromSeconds(2)).Should().BeTrue();
        await WaitForAsync(() => mock.CapturedWaveformIq.Count >= burst.Length);
        ptt.Unkey();

        IReadOnlyList<float> captured = mock.CapturedWaveformIq;
        captured.Count.Should().BeGreaterThanOrEqualTo(burst.Length);
        // The first reflections drain the buffered burst (in order); anything after is the starved
        // zero-fill. float32 round-trips byte-exact through the VITA big-endian packetize.
        captured.Take(burst.Length).Should().Equal(burst);
    }

    [Fact]
    public void Waveform_packet_is_the_full_bandwidth_stereo_class_with_big_endian_iq()
    {
        // The waveform TX packet is a full-bandwidth (stereo float32) DAX packet carrying interleaved
        // I/Q — the packetizer FlexWaveformIqOutput reflects with. Lock the on-wire layout.
        float[] iq = [1.0f, -1.0f, 0.5f, 0.25f]; // 2 complex pairs
        byte[] packet = DaxStreamFormat.FullBandwidth.BuildPacket(streamId: 0x20000001, packetCount: 3, iq);

        Vita49.TryParsePreamble(packet, out VitaPreamble preamble).Should().BeTrue();
        preamble.StreamId.Should().Be(0x20000001u);
        preamble.ClassId.Oui.Should().Be(0x001C2Du);
        preamble.ClassId.PacketClassCode.Should().Be(DaxStreamFormat.FullBandwidth.PacketClassCode);

        // Payload is the four floats, big-endian.
        var recovered = new float[iq.Length];
        DaxStreamFormat.FullBandwidth.Depacketize(
            packet.AsSpan(preamble.PayloadOffset, preamble.PayloadLength), recovered);
        recovered.Should().Equal(iq);
    }

    private static float[] MakeIqBurst(int pairs)
    {
        var iq = new float[pairs * 2];
        for (int k = 0; k < pairs; k++)
        {
            // A complex tone — distinct, deterministic, exactly float32-representable after round-trip.
            iq[2 * k] = MathF.Sin(0.1f * k);
            iq[(2 * k) + 1] = MathF.Cos(0.1f * k);
        }

        return iq;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("condition not met in time");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_queued_burst_survives_until_the_radio_is_keyed()
    {
        // The regression that cost a live session. A real 6500 asks for transmit buffers
        // continuously once a slice is in a waveform mode — keyed or not, and indefinitely
        // after xmit 0 — so a sink that answers unconditionally drains its own ring around
        // the clock. Samples queued before the key were consumed and discarded, and the burst
        // went out truncated with no error raised and a starve count of zero, because from the
        // client's side everything had been "sent".
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.Start();
        await using var _ = mock;

        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        mock.RxDelivery = client.DeliverVitaPacket;
        client.VitaSendHook = mock.DeliverTxPacket;

        await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions { SliceFrequencyMhz = 14.1 });
        using FlexWaveformIqOutput iq = waveform.CreateIqOutput();
        FlexPtt ptt = waveform.CreatePtt();

        float[] burst = MakeIqBurst(pairs: 640);
        iq.Write(burst);

        // The radio is already pulling. Give it time to eat the burst if the sink lets it.
        iq.Transmitting.Should().BeFalse("nothing has been keyed yet");
        await Task.Delay(300);
        mock.CapturedWaveformIq.Should().BeEmpty("a sink must not answer before the PA is up");

        ptt.Key();
        await WaitForAsync(() => mock.CapturedWaveformIq.Count >= burst.Length);
        ptt.Unkey();

        // Every queued sample reached the radio, in order, having survived the wait.
        mock.CapturedWaveformIq.Take(burst.Length).Should().Equal(burst);
    }

    [Fact]
    public async Task The_sink_falls_silent_once_its_tail_has_gone_out()
    {
        // After UNKEY_REQUESTED the reference waveform flushes what is left and then stops
        // emitting (smartsdr-dsp sched_waveform.c: flush_tx then inhibit_tx). It cannot wait
        // for a return to RECEIVE, because on this path the radio never sends one — the ring
        // emptying is the only available signal.
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.Start();
        await using var _ = mock;

        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        mock.RxDelivery = client.DeliverVitaPacket;
        client.VitaSendHook = mock.DeliverTxPacket;

        await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions { SliceFrequencyMhz = 14.1 });
        using FlexWaveformIqOutput iq = waveform.CreateIqOutput();
        FlexPtt ptt = waveform.CreatePtt();

        iq.Write(MakeIqBurst(pairs: 256));
        ptt.Key();
        await WaitForAsync(() => mock.CapturedWaveformIq.Count >= 512);
        ptt.Unkey();

        await WaitForAsync(() => !iq.Transmitting);
        iq.Transmitting.Should().BeFalse();

        long settled = mock.CapturedWaveformIq.Count;
        await Task.Delay(300);
        mock.CapturedWaveformIq.Count.Should().Be(
            (int)settled, "the radio keeps asking, but a silent sink must stop answering");
    }

    [Fact]
    public async Task A_cleanly_delivered_burst_reports_no_starve()
    {
        // Regression (M0LTE.Flex 0.8.0): a clean, fully-delivered burst over-counted its
        // drain-then-unkey tail as starves. The radio keeps pulling transmit buffers at 187.5/s
        // after the ring has drained and until the interlock parks; those empty pulls zero-pad
        // benign silence between the last real sample and unkey — not a mid-stream discontinuity —
        // and must not read as an underrun. Downstream the same mock burst measured
        // SamplesStarved==0 on 0.5.0 but ==4608 on 0.8.0.
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.Start();
        await using var _ = mock;

        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        mock.RxDelivery = client.DeliverVitaPacket;
        client.VitaSendHook = mock.DeliverTxPacket;

        await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions { SliceFrequencyMhz = 14.1 });
        using FlexWaveformIqOutput iq = waveform.CreateIqOutput();
        FlexPtt ptt = waveform.CreatePtt();

        float[] burst = MakeIqBurst(pairs: 2560); // 20 mock packets of 128 complex, whole burst
        iq.Write(burst);                           // fully buffered before keying — never falls behind
        ptt.Key();
        iq.Drain(TimeSpan.FromSeconds(2)).Should().BeTrue();
        await WaitForAsync(() => mock.CapturedWaveformIq.Count >= burst.Length);

        // The producer is done, but does not unkey instantly: the radio keeps pulling empty
        // transmit buffers through the drain-then-unkey gap (this window is where 0.8.0 accrued
        // its bogus starve). The fix holds those tail zero-pads rather than counting them.
        await Task.Delay(200);
        ptt.Unkey();
        await WaitForAsync(() => !iq.Transmitting);

        iq.SamplesStarved.Should().Be(0, "a fully-delivered burst never fell behind the radio");
    }

    [Fact]
    public async Task A_mid_burst_underrun_is_still_counted()
    {
        // The other direction: a genuine mid-stream underrun — the producer stalls while keyed with
        // real IQ still owed — puts zeros between real samples on air (a phase discontinuity, the
        // failure the 0.5.0 interlock fix exists to catch). That must still count, so the tail fix
        // cannot simply stop counting zero-pad.
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.Start();
        await using var _ = mock;

        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        mock.RxDelivery = client.DeliverVitaPacket;
        client.VitaSendHook = mock.DeliverTxPacket;

        await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions { SliceFrequencyMhz = 14.1 });
        using FlexWaveformIqOutput iq = waveform.CreateIqOutput();
        FlexPtt ptt = waveform.CreatePtt();

        iq.Write(MakeIqBurst(pairs: 256)); // first chunk (2 packets), buffered before keying
        ptt.Key();
        await WaitForAsync(() => mock.CapturedWaveformIq.Count >= 512); // first chunk drained

        // Producer stalls with the PA still up and more of the burst to come: the radio pulls empty
        // buffers across the gap.
        await Task.Delay(200);

        iq.Write(MakeIqBurst(pairs: 256)); // more real IQ arrives — the gap was a true underrun
        iq.Drain(TimeSpan.FromSeconds(2)).Should().BeTrue();
        ptt.Unkey();
        await WaitForAsync(() => !iq.Transmitting);

        iq.SamplesStarved.Should().BeGreaterThan(0, "the producer fell behind mid-burst with data still owed");
    }
}

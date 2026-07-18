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

        await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions());
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
}

using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>Direct coverage of the <see cref="FlexAudioInput"/> reorder/jitter ring —
/// out-of-order recovery and lost-packet concealment — driven by injecting VITA packets
/// with controlled packet counts (the branches the in-order in-process loop does not hit).</summary>
public sealed class FlexAudioInputReorderTests
{
    // Full-bandwidth float32 so each packet's marker level round-trips exactly.
    private static readonly DaxStreamFormat Format = DaxStreamFormat.FullBandwidth;
    private const int MarkerSamples = 4;

    private static async Task<(MockFlexRadio Mock, FlexStation Station)> SetUpAsync()
    {
        var mock = new MockFlexRadio(Format, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, Format, new FlexStationOptions { Keepalive = false });
        return (mock, station);
    }

    // A packet whose payload is a constant marker level, tagged with the given packet count.
    private static byte[] Packet(uint streamId, int count, float level)
    {
        var samples = new float[MarkerSamples];
        Array.Fill(samples, level);
        return Format.BuildPacket(streamId, count, samples);
    }

    [Fact]
    public async Task Out_of_order_packets_are_emitted_in_packet_count_order()
    {
        (MockFlexRadio mock, FlexStation station) = await SetUpAsync();
        await using var _ = mock;
        await using var __ = station;
        using FlexAudioInput input = station.CreateAudioInput(packetBuffer: 3);

        // Deliver counts 0,1,3,2,4 — count 3 arrives before count 2. Marker level = count + 1.
        foreach (int count in new[] { 0, 1, 3, 2, 4 })
        {
            station.Client.DeliverVitaPacket(Packet(station.RxStreamId, count, count + 1));
        }

        input.Flush();
        float[] markers = DrainMarkers(input, expectedPackets: 5);

        markers.Should().Equal(1, 2, 3, 4, 5); // reordered back into count order
        input.PacketsLost.Should().Be(0);
    }

    [Fact]
    public async Task A_lost_packet_is_concealed_by_repeating_the_previous_payload()
    {
        (MockFlexRadio mock, FlexStation station) = await SetUpAsync();
        await using var _ = mock;
        await using var __ = station;
        using FlexAudioInput input = station.CreateAudioInput(packetBuffer: 3);

        // Deliver counts 0,1,2,4,5,6 — count 3 never arrives. Marker level = count + 1.
        foreach (int count in new[] { 0, 1, 2, 4, 5, 6 })
        {
            station.Client.DeliverVitaPacket(Packet(station.RxStreamId, count, count + 1));
        }

        input.Flush();
        float[] markers = DrainMarkers(input, expectedPackets: 7);

        // The gap at count 3 is concealed by repeating count 2's payload (level 3).
        markers.Should().Equal(1, 2, 3, 3, 5, 6, 7);
        input.PacketsLost.Should().Be(1);
    }

    // Reads the drained samples and returns the marker level of each packet-sized chunk.
    private static float[] DrainMarkers(FlexAudioInput input, int expectedPackets)
    {
        var all = new List<float>();
        var buffer = new float[1024];
        int expectedSamples = expectedPackets * MarkerSamples;
        int got;
        while (all.Count < expectedSamples && (got = input.Read(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, got).ToArray());
        }

        var markers = new float[expectedPackets];
        for (int i = 0; i < expectedPackets; i++)
        {
            markers[i] = all[i * MarkerSamples];
        }

        return markers;
    }
}

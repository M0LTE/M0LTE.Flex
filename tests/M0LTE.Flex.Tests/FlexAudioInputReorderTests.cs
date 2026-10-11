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

    [Fact]
    public async Task A_rebuilt_slice_whose_count_lands_just_ahead_counts_one_loss_and_keeps_both_streams_in_order()
    {
        // Issue packet-net/pdn-soundmodem#680. Before, a new stream counting from 4 after the old
        // one's 0 to 5 counted nothing lost (the hole went unmarked) and wrote over the old
        // stream's held packets 4 and 5.
        (MockFlexRadio mock, FlexStation station) = await SetUpAsync();
        await using var _ = mock;
        await using var __ = station;
        using FlexAudioInput input = station.CreateAudioInput(packetBuffer: 3);

        foreach (int count in new[] { 0, 1, 2, 3, 4, 5 })
        {
            station.Client.DeliverVitaPacket(Packet(station.RxStreamId, count, count + 1));
        }

        Rebuild(station, station.RxStreamId); // the radio may hand back the same stream id
        foreach (int count in new[] { 4, 5, 6, 7, 8 })
        {
            station.Client.DeliverVitaPacket(Packet(station.RxStreamId, count, count + 7));
        }

        input.Flush();
        float[] markers = DrainMarkers(input, expectedPackets: 11);

        markers.Should().Equal(1, 2, 3, 4, 5, 6, 11, 12, 13, 14, 15);
        input.PacketsLost.Should().Be(1, "the hole between the two streams, and nothing else");
    }

    [Fact]
    public async Task A_rebuilt_slice_whose_count_lands_behind_conceals_no_phantom_packets()
    {
        // Before, the new stream's count 2 against a read point of 3 concealed 13 phantom packets.
        (MockFlexRadio mock, FlexStation station) = await SetUpAsync();
        await using var _ = mock;
        await using var __ = station;
        using FlexAudioInput input = station.CreateAudioInput(packetBuffer: 3);

        foreach (int count in new[] { 0, 1, 2, 3, 4, 5 })
        {
            station.Client.DeliverVitaPacket(Packet(station.RxStreamId, count, count + 1));
        }

        uint newStream = station.RxStreamId + 1;
        Rebuild(station, newStream);
        foreach (int count in new[] { 2, 3, 4, 5 })
        {
            station.Client.DeliverVitaPacket(Packet(newStream, count, count + 20));
        }

        input.Flush();
        float[] markers = DrainMarkers(input, expectedPackets: 10);

        markers.Should().Equal(1, 2, 3, 4, 5, 6, 22, 23, 24, 25);
        input.PacketsLost.Should().Be(1);
    }

    [Fact]
    public async Task The_first_stream_of_an_input_is_not_a_restart()
    {
        (MockFlexRadio mock, FlexStation station) = await SetUpAsync();
        await using var _ = mock;
        await using var __ = station;
        Rebuild(station, station.RxStreamId); // a rebuild before any audio has arrived
        using FlexAudioInput input = station.CreateAudioInput(packetBuffer: 3);

        foreach (int count in new[] { 9, 10, 11, 12 })
        {
            station.Client.DeliverVitaPacket(Packet(station.RxStreamId, count, count));
        }

        input.Flush();
        DrainMarkers(input, expectedPackets: 4).Should().Equal(9, 10, 11, 12);
        input.PacketsLost.Should().Be(0);
    }

    // What FlexStation.RecoverAsync publishes once it has rebuilt the slice and its DAX streams.
    private static void Rebuild(FlexStation station, uint rxStreamId)
    {
        FlexSliceBinding current = station.Lease.Current;
        station.Lease.Invalidate();
        station.Lease.Bind(current.SliceIndex, current.OwnerHandle, rxStreamId, current.TxStreamId);
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

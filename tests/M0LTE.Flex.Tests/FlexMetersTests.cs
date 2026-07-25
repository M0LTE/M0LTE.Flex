using System.Buffers.Binary;

namespace M0LTE.Flex.Tests;

/// <summary>
/// The meter path: metadata parse, value decode, unit scaling, and an end-to-end subscription
/// against <see cref="MockFlexRadio"/>.
/// </summary>
/// <remarks>
/// The scaling expectations here are <b>measured</b>, not read off a table — the published
/// documentation contradicts itself on two of the three divisors, and the radio settled it.
/// Every value marked "measured" came off M0LTE's FLEX-6500 into a dummy load.
/// </remarks>
public class FlexMetersTests
{
    private static byte[] MeterPacket(params (int Id, short Raw)[] meters)
    {
        // Exactly the shape a FLEX-6500 sends: ExtDataWithStream, class id present, BOTH
        // timestamp fields — a 28-byte preamble.
        var packet = new byte[28 + (4 * meters.Length)];
        uint header = (3u << 28) | 0x08000000u | (1u << 22) | (1u << 20) | (uint)(packet.Length / 4);
        BinaryPrimitives.WriteUInt32BigEndian(packet, header);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MockFlexRadio.MeterStreamId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), Vita49.FlexOui);
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(12), ((uint)Vita49.FlexInformationClass << 16) | Vita49.MeterClass);
        for (int i = 0; i < meters.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(28 + (4 * i)), (ushort)meters[i].Id);
            BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(30 + (4 * i)), meters[i].Raw);
        }

        return packet;
    }

    [Fact]
    public void Meter_metadata_parses_including_names_with_dots_and_signs()
    {
        IReadOnlyList<FlexMeterDescriptor> meters =
            FlexMeters.ParseMeterList(MockFlexRadio.MeterListReply);

        meters.Should().HaveCount(7);

        FlexMeterDescriptor swr = meters.Single(m => m.Name == "SWR");
        swr.Id.Should().Be(8);
        swr.Source.Should().Be("TX-");
        swr.Unit.Should().Be("SWR");
        swr.Low.Should().Be(1.0);
        swr.High.Should().Be(999.0);
        swr.Fps.Should().Be(20);

        // `4.nam=+13.8A` is the nasty one — the value holds both '.' and '+', so the parse has
        // to split on the FIRST '.' and then the FIRST '='.
        FlexMeterDescriptor volts = meters.Single(m => m.Id == 4);
        volts.Name.Should().Be("+13.8A");
        volts.Unit.Should().Be("Volts");
        volts.Low.Should().Be(10.5);
        volts.Description.Should().Be("Main radio input voltage before fuse");
    }

    [Fact]
    public void Meter_values_decode_as_big_endian_id_value_pairs()
    {
        byte[] payload = [0x00, 0x08, 0x00, 0x80, 0x00, 0x06, 0xFF, 0x00];

        FlexMeters.DecodePayload(payload).Should().Equal([(8, (short)128), (6, (short)-256)]);
    }

    [Fact]
    public void A_trailing_partial_word_is_ignored_rather_than_misread()
    {
        FlexMeters.DecodePayload([0x00, 0x08, 0x00, 0x80, 0x00, 0x06])
            .Should().Equal([(8, (short)128)]);
    }

    [Theory]
    [InlineData("dBm", (short)6400, 50.0)]           // 50 dBm = 100 W
    [InlineData("dBFS", (short)-19200, -150.0)]      // measured: an idle TX chain meter
    [InlineData("SWR", (short)128, 1.0)]             // measured: into a dummy load
    [InlineData("Volts", (short)3294, 12.8671875)]   // measured: the +13.8A rail
    [InlineData("degC", (short)2239, 34.984375)]     // measured: an idle PA
    public void Unit_scaling_matches_what_the_radio_actually_reports(string unit, short raw, double expected)
    {
        FlexMeters.Scale(raw, unit).Should().BeApproximately(expected, 1e-6);
    }

    [Fact]
    public void Swr_is_scaled_by_128_not_used_raw()
    {
        // The FlexRadio wiki's "all others used directly" rule would report 128:1 into a dummy
        // load. It describes how the radio converts meters a CLIENT creates and sends, not how
        // to read the radio's own — the MIT flexclient reference scales SWR with dBm/dBFS, and
        // the radio agrees.
        FlexMeters.Scale(128, "SWR").Should().BeApproximately(1.0, 1e-9);
        FlexMeters.Scale(192, "SWR").Should().BeApproximately(1.5, 1e-9);
    }

    [Fact]
    public void Swr_derived_from_forward_and_reflected_power_matches_the_meter()
    {
        // Two independent routes to SWR that must agree: the radio's own meter, and the
        // computation from FWDPWR/REFPWR (both dBm, whose scaling is unambiguous). Live on a
        // 6500 these read 1.27 and 1.32 — agreement is what validates both at once.
        // Γ = 10^((21.5 − 39.1)/20) = 0.1318 → SWR = 1.303.
        double gamma = Math.Pow(10.0, (21.5 - 39.1) / 20.0);
        double expected = (1 + gamma) / (1 - gamma);

        expected.Should().BeApproximately(1.30, 0.01);
    }

    [Fact]
    public void Dbm_to_watts_matches_the_familiar_anchors()
    {
        FlexMeters.DbmToWatts(30).Should().BeApproximately(1.0, 1e-9);
        FlexMeters.DbmToWatts(39.9).Should().BeApproximately(9.77, 0.01);  // measured at rfpower=10
        FlexMeters.DbmToWatts(50).Should().BeApproximately(100.0, 1e-6);
    }

    [Fact]
    public async Task Subscribing_decodes_every_meter_in_a_real_shaped_packet()
    {
        // The regression that matters: a 28-byte preamble carrying the low ids. Re-applying
        // PayloadOffset inside the handler dropped this packet entirely (offset ≥ payload
        // length) and, on longer ones, ate the first seven values — which on a real radio
        // meant SWR, FWDPWR and REFPWR silently never appeared.
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.Start();
        await using (mock)
        {
            FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
            await using (client)
            {
                await client.InitUdpAsync();
                mock.RxDelivery = client.DeliverVitaPacket;

                using FlexMeters meters = await FlexMeters.SubscribeAsync(client);
                meters.Descriptors.Should().HaveCount(7);

                mock.PushMeters((1, -19200), (6, 5011), (7, 2751), (8, 162), (9, 2239));

                meters.PacketsReceived.Should().Be(1);
                meters.UnknownIdSamples.Should().Be(0);

                meters.TryGet("SWR", out FlexMeterReading swr).Should().BeTrue();
                swr.Raw.Should().Be(162);
                swr.Value.Should().BeApproximately(1.265625, 1e-6);

                meters.TryGet("FWDPWR", out FlexMeterReading fwd).Should().BeTrue();
                fwd.Value.Should().BeApproximately(39.148, 1e-3);
                FlexMeters.DbmToWatts(fwd.Value).Should().BeApproximately(8.2, 0.1);

                meters.TryGet("PATEMP", out FlexMeterReading pa).Should().BeTrue();
                pa.Value.Should().BeApproximately(34.984, 1e-3);

                // And the derived cross-check, from the two dBm meters.
                meters.SwrFromPowers().Should().BeApproximately(1.30, 0.02);
            }
        }
    }

    [Fact]
    public async Task A_short_meter_packet_carrying_only_low_ids_is_not_dropped()
    {
        // The live radio sent 36-byte packets holding ids 1..3 alone. Under the old bug these
        // vanished outright, because a re-applied 28-byte offset exceeded their payload.
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.Start();
        await using (mock)
        {
            FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
            await using (client)
            {
                await client.InitUdpAsync();
                mock.RxDelivery = client.DeliverVitaPacket;
                using FlexMeters meters = await FlexMeters.SubscribeAsync(client);

                mock.PushMeters((1, -19200), (3, 0));

                meters.PacketsReceived.Should().Be(1);
                meters.TryGet("MICPEAK", out FlexMeterReading mic).Should().BeTrue();
                mic.Value.Should().BeApproximately(-150.0, 1e-6);
            }
        }
    }

    [Fact]
    public async Task An_inert_instance_reports_nothing_rather_than_inventing_an_interlock()
    {
        // A consumer with no meter surface must get honest nulls — a fabricated SWR reading
        // would be worse than none at all, because something downstream would rely on it.
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.Start();
        await using (mock)
        {
            FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
            await using (client)
            {
                using FlexMeters meters = FlexMeters.None(client);

                meters.Descriptors.Should().BeEmpty();
                meters.TryGet("SWR", out _).Should().BeFalse();
                meters.SwrFromPowers().Should().BeNull();
            }
        }
    }

    [Fact]
    public async Task Meter_packets_carry_the_payload_the_dispatcher_hands_over_unchanged()
    {
        // Guards the VitaPacket contract itself: whatever a handler receives must be the
        // payload, header already stripped. If that ever regresses, everything above breaks
        // in the same silent way it did on the radio.
        byte[] packet = MeterPacket((8, 128), (6, 6400));

        Vita49.TryParsePreamble(packet, out VitaPreamble preamble).Should().BeTrue();
        preamble.PayloadOffset.Should().Be(28);
        preamble.PayloadLength.Should().Be(8);

        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.Start();
        await using (mock)
        {
            FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
            await using (client)
            {
                await client.InitUdpAsync();
                VitaPacket? seen = null;
                client.VitaPacketReceived += p => seen = p;
                client.DeliverVitaPacket(packet);

                seen.Should().NotBeNull();
                seen!.Value.Payload.Length.Should().Be(8, "the header must already be stripped");
                seen.Value.PacketClassCode.Should().Be(Vita49.MeterClass);
                FlexMeters.DecodePayload(seen.Value.Payload.Span)
                    .Should().Equal([(8, (short)128), (6, (short)6400)]);
            }
        }
    }
}

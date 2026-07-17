using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>Byte-exact regression vectors for the VITA-49 DAX-audio TX packet layout
/// (docs/flex-integration.md §2.4) and the preamble parser.</summary>
public sealed class Vita49Tests
{
    [Fact]
    public void Reduced_bandwidth_dax_tx_packet_is_byte_exact()
    {
        // payload = two s16 samples {1, 2} big-endian.
        byte[] payload = [0x00, 0x01, 0x00, 0x02];
        byte[] packet = Vita49.BuildDaxAudioPacket(
            Vita49.ReducedDaxStreamClass, streamId: 0x12345678, packetCount: 5, payload);

        byte[] expected =
        [
            0x18,                                            // IFDataWithStream, C
            0xD5,                                            // 0xD0 | (5 & 0x0F): TSI=Other, TSF=SampleCount
            0x00, 0x08,                                      // words = payload/4 (1) + 7 header
            0x12, 0x34, 0x56, 0x78,                          // stream id
            0x00, 0x00, 0x1C, 0x2D, 0x53, 0x4C, 0x01, 0x23, // stream class (reduced, 0x0123)
            0x00, 0x00, 0x00, 0x00,                          // timestamp int (unused)
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // timestamp frac (unused)
            0x00, 0x01, 0x00, 0x02,                          // payload
        ];

        packet.Should().Equal(expected);
    }

    [Fact]
    public void Full_bandwidth_dax_tx_packet_is_byte_exact()
    {
        // payload = one float32 sample 1.0 big-endian (0x3F800000).
        byte[] payload = [0x3F, 0x80, 0x00, 0x00];
        byte[] packet = Vita49.BuildDaxAudioPacket(
            Vita49.FullDaxStreamClass, streamId: 0xAABBCCDD, packetCount: 0, payload);

        byte[] expected =
        [
            0x18,
            0xD0,                                            // 0xD0 | 0
            0x00, 0x08,
            0xAA, 0xBB, 0xCC, 0xDD,
            0x00, 0x00, 0x1C, 0x2D, 0x53, 0x4C, 0x03, 0xE3, // stream class (full, 0x03E3)
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x3F, 0x80, 0x00, 0x00,
        ];

        packet.Should().Equal(expected);
    }

    [Fact]
    public void Packet_count_only_uses_the_low_four_bits()
    {
        byte[] payload = [0x00, 0x00, 0x00, 0x00];
        byte[] packet = Vita49.BuildDaxAudioPacket(
            Vita49.ReducedDaxStreamClass, streamId: 1, packetCount: 0x1F, payload);

        packet[1].Should().Be(0xDF); // 0xD0 | (0x1F & 0x0F)
    }

    [Fact]
    public void Payload_length_not_a_multiple_of_four_is_rejected()
    {
        Action build = () => Vita49.BuildDaxAudioPacket(
            Vita49.ReducedDaxStreamClass, 1, 0, new byte[6]);
        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Built_dax_packet_round_trips_through_the_preamble_parser()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04];
        byte[] packet = Vita49.BuildDaxAudioPacket(
            Vita49.FullDaxStreamClass, streamId: 0x40000123, packetCount: 7, payload);

        Vita49.TryParsePreamble(packet, out VitaPreamble preamble).Should().BeTrue();
        preamble.PacketType.Should().Be(VitaPacketType.IfDataWithStream);
        preamble.HasClassId.Should().BeTrue();
        preamble.HasTrailer.Should().BeFalse();
        preamble.Tsi.Should().Be(VitaTsi.Other);
        preamble.Tsf.Should().Be(VitaTsf.SampleCount);
        preamble.PacketCount.Should().Be(7);
        preamble.StreamId.Should().Be(0x40000123u);
        preamble.ClassId.Oui.Should().Be(Vita49.FlexOui);
        preamble.ClassId.InformationClassCode.Should().Be(Vita49.FlexInformationClass);
        preamble.ClassId.PacketClassCode.Should().Be(Vita49.IfNarrowClass);
        preamble.PayloadOffset.Should().Be(Vita49.DaxHeaderBytes);
        preamble.PayloadLength.Should().Be(payload.Length);
        packet.AsSpan(preamble.PayloadOffset, preamble.PayloadLength).ToArray().Should().Equal(payload);
    }

    [Fact]
    public void Preamble_parse_rejects_a_runt_buffer()
    {
        Vita49.TryParsePreamble(new byte[8], out _).Should().BeFalse();
    }
}

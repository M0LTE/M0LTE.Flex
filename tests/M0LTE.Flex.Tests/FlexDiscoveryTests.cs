using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>Discovery packet build → parse, including the name underscore↔space rule and
/// the OUI/packet-class gate.</summary>
public sealed class FlexDiscoveryTests
{
    private const string SampleKv =
        "discovery_protocol_version=3.0.0.2 model=FLEX-6500 serial=1234-5678-6500-9012 "
        + "version=3.8.23.35785 nickname=Flex_6500 callsign=M0LTE "
        + "name=Flex_6500 ip=192.168.1.50 port=4992 status=Available";

    [Fact]
    public void Synthesized_discovery_packet_parses_to_radio_info()
    {
        byte[] packet = Vita49.BuildDiscoveryPacket(SampleKv);
        FlexRadioInfo? info = FlexDiscovery.TryParsePacket(packet);

        info.Should().NotBeNull();
        info!.Model.Should().Be("FLEX-6500");
        info.Serial.Should().Be("1234-5678-6500-9012");
        info.Version.Should().Be("3.8.23.35785");
        info.Callsign.Should().Be("M0LTE");
        info.Ip.Should().Be("192.168.1.50");
        info.Port.Should().Be(4992);
    }

    [Fact]
    public void Name_underscores_decode_back_to_spaces_but_raw_fields_are_verbatim()
    {
        byte[] packet = Vita49.BuildDiscoveryPacket("name=My_Shack_Radio serial=42 ip=10.0.0.9 port=4992");
        FlexRadioInfo info = FlexDiscovery.TryParsePacket(packet)!;

        info.Name.Should().Be("My Shack Radio");
        info.Fields["name"].Should().Be("My_Shack_Radio");
    }

    [Fact]
    public void Non_flex_vita_packet_is_not_discovery()
    {
        // A DAX-audio packet (OUI matches but packet class is 0x03E3, not 0xFFFF).
        byte[] packet = Vita49.BuildDaxAudioPacket(Vita49.FullDaxStreamClass, 1, 0, new byte[4]);
        FlexDiscovery.TryParsePacket(packet).Should().BeNull();
    }

    [Fact]
    public void Fields_parse_trims_word_padding_nulls()
    {
        // BuildDiscoveryPacket NUL-pads to a 32-bit boundary; parse must not leak them.
        byte[] packet = Vita49.BuildDiscoveryPacket("model=FLEX-6400 serial=7"); // 24 chars → padded
        FlexRadioInfo info = FlexDiscovery.TryParsePacket(packet)!;
        info.Model.Should().Be("FLEX-6400");
        info.Serial.Should().Be("7");
        info.Fields.Should().NotContainKey("");
    }
}

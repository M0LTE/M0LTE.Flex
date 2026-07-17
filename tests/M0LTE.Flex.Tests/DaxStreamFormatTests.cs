using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>DAX-audio sample↔wire conversion: s16be and float32be, mono, endianness,
/// levels, and the DSP-rate auto-pick.</summary>
public sealed class DaxStreamFormatTests
{
    [Fact]
    public void Reduced_bandwidth_is_24k_s16_128_samples()
    {
        DaxStreamFormat format = DaxStreamFormat.ReducedBandwidth;
        format.SampleRate.Should().Be(24000);
        format.SamplesPerPacket.Should().Be(128);
        format.BytesPerSample.Should().Be(2);
        format.IsFloat.Should().BeFalse();
        format.PacketClassCode.Should().Be(Vita49.ReducedDaxAudioClass);
        format.StreamClass.Should().Be(Vita49.ReducedDaxStreamClass);
        format.PayloadBytesPerPacket.Should().Be(256);
        format.IsReducedBandwidth.Should().BeTrue();
    }

    [Fact]
    public void Full_bandwidth_is_48k_float32_256_samples()
    {
        DaxStreamFormat format = DaxStreamFormat.FullBandwidth;
        format.SampleRate.Should().Be(48000);
        format.SamplesPerPacket.Should().Be(256);
        format.BytesPerSample.Should().Be(4);
        format.IsFloat.Should().BeTrue();
        format.PacketClassCode.Should().Be(Vita49.IfNarrowClass);
        format.StreamClass.Should().Be(Vita49.FullDaxStreamClass);
        format.PayloadBytesPerPacket.Should().Be(1024);
        format.IsReducedBandwidth.Should().BeFalse();
    }

    [Theory]
    [InlineData(12000)]
    [InlineData(6000)]
    public void Auto_pick_bridges_audio_band_rates_to_reduced_bandwidth(int dspRate)
    {
        DaxStreamFormat.ForDspRate(dspRate).Should().BeSameAs(DaxStreamFormat.ReducedBandwidth);
    }

    [Fact]
    public void Auto_pick_bridges_48k_to_full_bandwidth()
    {
        DaxStreamFormat.ForDspRate(48000).Should().BeSameAs(DaxStreamFormat.FullBandwidth);
    }

    [Fact]
    public void S16_depacketize_reads_big_endian_mono()
    {
        // 0x7FFF = 32767, 0x8001 = -32767.
        byte[] payload = [0x7F, 0xFF, 0x80, 0x01];
        var samples = new float[2];
        int count = DaxStreamFormat.ReducedBandwidth.Depacketize(payload, samples);

        count.Should().Be(2);
        samples[0].Should().BeApproximately(32767f / 32768f, 1e-6f);
        samples[1].Should().BeApproximately(-32767f / 32768f, 1e-6f);
    }

    [Fact]
    public void Float32_depacketize_reads_big_endian_mono()
    {
        // 0x3F800000 = 1.0f, 0xBF800000 = -1.0f, 0x00000000 = 0.0f.
        byte[] payload =
        [
            0x3F, 0x80, 0x00, 0x00,
            0xBF, 0x80, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        var samples = new float[3];
        int count = DaxStreamFormat.FullBandwidth.Depacketize(payload, samples);

        count.Should().Be(3);
        samples.Should().Equal(1.0f, -1.0f, 0.0f);
    }

    [Fact]
    public void S16_write_samples_rounds_and_clamps_big_endian()
    {
        var destination = new byte[4];
        DaxStreamFormat.ReducedBandwidth.WriteSamples([1.0f, -1.0f], destination);
        destination.Should().Equal(0x7F, 0xFF, 0x80, 0x01); // +32767, -32767
    }

    [Fact]
    public void Float32_write_samples_round_trips_exactly()
    {
        var wire = new byte[3 * 4];
        float[] input = [0.123456f, -0.987654f, 42.5f];
        DaxStreamFormat.FullBandwidth.WriteSamples(input, wire);

        var recovered = new float[3];
        DaxStreamFormat.FullBandwidth.Depacketize(wire, recovered);
        recovered.Should().Equal(input);
    }

    [Fact]
    public void Build_packet_uses_the_format_stream_class_and_samples()
    {
        byte[] packet = DaxStreamFormat.ReducedBandwidth.BuildPacket(
            streamId: 0x0000AAAA, packetCount: 3, samples: [1.0f, -1.0f]);

        Vita49.TryParsePreamble(packet, out VitaPreamble preamble).Should().BeTrue();
        preamble.StreamId.Should().Be(0x0000AAAAu);
        preamble.PacketCount.Should().Be(3);
        packet.AsSpan(preamble.PayloadOffset, preamble.PayloadLength).ToArray()
            .Should().Equal(0x7F, 0xFF, 0x80, 0x01);
    }
}

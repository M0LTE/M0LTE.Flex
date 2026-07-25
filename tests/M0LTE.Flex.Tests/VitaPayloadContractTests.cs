using System.Buffers.Binary;

namespace M0LTE.Flex.Tests;

/// <summary>
/// Locks the contract that <see cref="VitaPacket"/> exists to enforce: a handler is given the
/// <b>payload</b>, header already stripped, and must never re-index it by
/// <see cref="VitaPreamble.PayloadOffset"/>.
/// </summary>
/// <remarks>
/// <para>This was a real, shipped bug, made independently twice — once in this library's own
/// <c>FlexDaxIqSource</c> and once in a consumer. It is worth a dedicated test file because of
/// how it fails: not with an exception or a corrupt-looking result, but by <em>quietly
/// deleting the front of every packet</em>. Downstream everything still looks structurally
/// valid, so the loss reads as "the radio didn't send that" rather than "we threw it away".</para>
/// <para>On a FLEX-6500 meter stream (28-byte preamble) it discarded short packets whole and
/// ate the first seven values of long ones. On the DAX-IQ path it would skip 3.5 complex
/// samples off each packet — and since that is not a whole I/Q pair, it also transposes I and
/// Q and mirrors the spectrum, which a noise-floor sanity check cannot see.</para>
/// </remarks>
public class VitaPayloadContractTests
{
    /// <summary>Builds a packet with a 28-byte preamble — the real 6500 shape: data-with-stream,
    /// class id present, and both timestamp fields.</summary>
    private static byte[] PacketWith28ByteHeader(uint streamId, ushort packetClass, byte[] payload)
    {
        var packet = new byte[28 + payload.Length];
        uint header = (1u << 28) | 0x08000000u | (1u << 22) | (1u << 20) | (uint)(packet.Length / 4);
        BinaryPrimitives.WriteUInt32BigEndian(packet, header);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), streamId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), Vita49.FlexOui);
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(12), ((uint)Vita49.FlexInformationClass << 16) | packetClass);
        payload.CopyTo(packet.AsSpan(28));
        return packet;
    }

    [Fact]
    public async Task A_handler_receives_the_payload_with_the_header_already_removed()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] packet = PacketWith28ByteHeader(0x700, Vita49.MeterClass, payload);

        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.Start();
        await using (mock)
        {
            FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
            await using (client)
            {
                VitaPacket? seen = null;
                client.VitaPacketReceived += p => seen = p;
                client.DeliverVitaPacket(packet);

                seen.Should().NotBeNull();
                seen!.Value.Payload.ToArray().Should().Equal(payload);

                // The preamble still describes the ORIGINAL datagram — that is what makes the
                // double-application so easy to write, and why handlers must use Payload.
                seen.Value.Preamble.PayloadOffset.Should().Be(28);
                seen.Value.Preamble.PayloadLength.Should().Be(payload.Length);
            }
        }
    }

    [Fact]
    public async Task A_packet_whose_payload_is_shorter_than_its_header_still_reaches_the_handler()
    {
        // 8 bytes of payload behind a 28-byte header. The old mistake compared 28 against the
        // payload length, decided the packet was too short, and dropped it silently — which is
        // exactly how the low-numbered transmit meters disappeared on a live radio.
        byte[] payload = [0x00, 0x08, 0x00, 0x80];
        byte[] packet = PacketWith28ByteHeader(0x700, Vita49.MeterClass, payload);

        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.Start();
        await using (mock)
        {
            FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
            await using (client)
            {
                var received = new List<int>();
                client.VitaPacketReceived += p => received.Add(p.Payload.Length);
                client.DeliverVitaPacket(packet);

                received.Should().Equal([4]);
            }
        }
    }

    [Fact]
    public async Task Dax_iq_ingests_every_complex_sample_with_none_shaved_off_the_front()
    {
        // The DAX-IQ half of the same bug. A wideband IQ packet is little-endian float32 I/Q
        // (host order — unlike DAX audio); if a header's worth were skipped again here, the
        // first samples would vanish AND I/Q would transpose. Both are invisible on noise,
        // which is all this path had ever been pointed at, so assert on a known ramp instead.
        var payload = new byte[8 * 4];
        for (int k = 0; k < 4; k++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(8 * k), k + 1);        // I
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan((8 * k) + 4), -(k + 1)); // Q
        }

        var buffer = new DaxIqStreamBuffer(16);
        buffer.Ingest(0, payload);

        var destination = new float[8];
        buffer.Read(destination).Should().Be(8);
        destination.Should().Equal([1f, -1f, 2f, -2f, 3f, -3f, 4f, -4f]);
    }
}

using System.Buffers.Binary;
using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>The DAX-IQ depacketize + jitter-buffer core behind <see cref="FlexDaxIqSource"/>. The
/// load-bearing fact under test is the wire format: DAX-IQ payloads are <b>little-endian</b> float32
/// interleaved I/Q (unlike big-endian DAX audio). Synthetic packets; no radio.</summary>
public sealed class DaxIqStreamBufferTests
{
    [Fact]
    public void Depacketizes_little_endian_float32_iq_in_order()
    {
        (float I, float Q)[] pairs = [(1.0f, -1.0f), (0.5f, 0.25f), (-0.75f, 0.125f)];
        byte[] payload = BuildPayload(pairs);

        var buffer = new DaxIqStreamBuffer(capacityPairs: 16);
        buffer.Ingest(packetCount: 0, payload);
        buffer.Stop();

        var output = new float[pairs.Length * 2];
        int got = buffer.Read(output);

        got.Should().Be(pairs.Length * 2);
        for (int i = 0; i < pairs.Length; i++)
        {
            output[2 * i].Should().Be(pairs[i].I);
            output[(2 * i) + 1].Should().Be(pairs[i].Q);
        }
    }

    [Fact]
    public void Counts_lost_packets_from_the_vita_4_bit_counter()
    {
        byte[] one = BuildPayload([(0f, 0f)]);
        var buffer = new DaxIqStreamBuffer(capacityPairs: 16);

        buffer.Ingest(0, one);
        buffer.Ingest(1, one);
        buffer.Ingest(3, one); // skipped 2 -> one lost
        buffer.Ingest(15, one); // skipped 4..14 -> eleven lost
        buffer.Ingest(0, one); // wrap 15 -> 0, contiguous, none lost

        buffer.PacketsReceived.Should().Be(5);
        buffer.PacketsLost.Should().Be(12);
    }

    [Fact]
    public async Task Read_blocks_until_data_then_returns_zero_once_stopped_and_drained()
    {
        var buffer = new DaxIqStreamBuffer(capacityPairs: 16);
        var reader = Task.Run(() =>
        {
            var output = new float[8];
            return buffer.Read(output); // blocks — nothing ingested yet
        });

        Task first = await Task.WhenAny(reader, Task.Delay(200));
        first.Should().NotBeSameAs(reader, "Read must block while the buffer is empty and not stopped");

        buffer.Ingest(0, BuildPayload([(2f, 3f)]));
        Task done = await Task.WhenAny(reader, Task.Delay(2000));
        done.Should().BeSameAs(reader);
        (await reader).Should().Be(2);

        buffer.Stop();
        buffer.Read(new float[4]).Should().Be(0, "stopped and drained is end-of-stream");
    }

    [Fact]
    public void A_block_ends_before_packets_lost_in_flight_and_the_next_one_reports_them()
    {
        var buffer = new DaxIqStreamBuffer(capacityPairs: 64);
        buffer.Ingest(0, BuildPayload([(1f, 1f), (2f, 2f), (3f, 3f)]));
        buffer.Ingest(3, BuildPayload([(4f, 4f), (5f, 5f), (6f, 6f)])); // packets 1 and 2 lost

        var output = new float[64];
        buffer.Read(output).Should().Be(6, "the block stops at the last pair before the hole");
        output[4].Should().Be(3f);
        buffer.SamplesLost.Should().Be(0, "the hole is reported on the block after it");

        buffer.Read(output).Should().Be(6);
        output[0].Should().Be(4f);
        buffer.SamplesLost.Should().Be(6, "two missing packets, estimated at three pairs each");
    }

    [Fact]
    public void Ring_overflow_is_reported_exactly_before_the_oldest_pair_still_held()
    {
        var buffer = new DaxIqStreamBuffer(capacityPairs: 4);
        buffer.Ingest(0, BuildPayload([(1f, 1f), (2f, 2f), (3f, 3f), (4f, 4f), (5f, 5f), (6f, 6f)]));

        var output = new float[64];
        buffer.Read(output).Should().Be(8);
        output[0].Should().Be(3f, "the two oldest pairs were overwritten");
        buffer.SamplesLost.Should().Be(2);
        buffer.SamplesDropped.Should().Be(2);
    }

    [Fact]
    public void An_in_flight_hole_that_overflow_has_dropped_past_is_still_reported_once()
    {
        var buffer = new DaxIqStreamBuffer(capacityPairs: 4);
        buffer.Ingest(0, BuildPayload([(1f, 1f), (2f, 2f)]));
        buffer.Ingest(2, BuildPayload([(3f, 3f), (4f, 4f)])); // packet 1 lost: 2 pairs, before pair 3
        buffer.Ingest(3, BuildPayload([(5f, 5f), (6f, 6f), (7f, 7f)])); // overflow drops pairs 1 to 3

        var output = new float[64];
        buffer.Read(output).Should().Be(8);
        output[0].Should().Be(4f);
        buffer.SamplesLost.Should().Be(5, "three pairs overwritten plus the two-pair packet that never came");

        buffer.Stop();
        buffer.Read(output).Should().Be(0);
        buffer.SamplesLost.Should().Be(5);
    }

    [Fact]
    public void A_gapless_stream_reports_no_loss_and_reads_across_packet_boundaries()
    {
        var buffer = new DaxIqStreamBuffer(capacityPairs: 64);
        buffer.Ingest(0, BuildPayload([(1f, 1f), (2f, 2f)]));
        buffer.Ingest(1, BuildPayload([(3f, 3f), (4f, 4f)]));

        buffer.Read(new float[64]).Should().Be(8);
        buffer.SamplesLost.Should().Be(0);
    }

    private static byte[] BuildPayload((float I, float Q)[] pairs)
    {
        var payload = new byte[pairs.Length * 8];
        for (int i = 0; i < pairs.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(i * 8, 4), pairs[i].I);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan((i * 8) + 4, 4), pairs[i].Q);
        }

        return payload;
    }
}

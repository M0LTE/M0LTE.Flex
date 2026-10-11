using System.Buffers.Binary;
using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>A DAX-IQ stream that stops and restarts (issue packet-net/pdn-soundmodem#680): the
/// 4-bit packet counter cannot measure a silence longer than its 16-packet cycle, and a restarted
/// stream starts its count wherever it likes. Synthetic packets at 1 kSPS, 10 pairs each, so one
/// packet is 10 ms and the counter's cycle is 160 ms; the clock is driven by hand.</summary>
public sealed class DaxIqStreamRestartTests
{
    private const int Rate = 1000;
    private const int PacketPairs = 10;
    private static readonly TimeSpan PacketTime = TimeSpan.FromMilliseconds(10);

    [Fact]
    public void A_restart_whose_count_lands_behind_the_last_one_keeps_its_packets_and_reports_the_silence()
    {
        // Untimed, counts 0..3 then a restart at 0 would see 0, 1, 2, 3 as late or repeated
        // packets: all four dropped, no hole, and the silence before them never reported.
        var clock = new ManualClock();
        var buffer = new DaxIqStreamBuffer(capacityPairs: 4096, Rate, clock);
        Deliver(buffer, clock, counts: [0, 1, 2, 3], level: 1f);

        clock.Advance(TimeSpan.FromSeconds(1));
        buffer.Ingest(0, Payload(2f));
        Deliver(buffer, clock, counts: [1, 2, 3, 4], level: 2f);

        float[] output = new float[4096];
        buffer.Read(output).Should().Be(4 * PacketPairs * 2, "the four packets before the restart");
        buffer.SamplesLost.Should().Be(0);

        buffer.Read(output).Should().Be(5 * PacketPairs * 2, "every packet of the restarted stream is kept");
        output[0].Should().Be(2f);
        buffer.SamplesLost.Should().Be(1000 - PacketPairs, "one second of silence, less the packet that ended it");
        buffer.PacketsLost.Should().Be(99);
    }

    [Fact]
    public void A_restart_whose_count_lands_ahead_reports_the_silence_not_what_the_counter_says()
    {
        // Untimed, 0..10 then a restart at 0 reads as five missing packets: 50 pairs for a
        // 2-second hole.
        var clock = new ManualClock();
        var buffer = new DaxIqStreamBuffer(capacityPairs: 4096, Rate, clock);
        Deliver(buffer, clock, counts: [8, 9, 10], level: 1f);

        clock.Advance(TimeSpan.FromSeconds(2));
        buffer.Ingest(0, Payload(2f));

        float[] output = new float[4096];
        buffer.Read(output).Should().Be(3 * PacketPairs * 2);
        buffer.Read(output).Should().Be(PacketPairs * 2);
        buffer.SamplesLost.Should().Be(2000 - PacketPairs);
    }

    [Fact]
    public void An_outage_that_wraps_the_counter_is_measured_by_its_length()
    {
        // 20 packets go missing: the counter can only say 4.
        var clock = new ManualClock();
        var buffer = new DaxIqStreamBuffer(capacityPairs: 4096, Rate, clock);
        Deliver(buffer, clock, counts: [0, 1], level: 1f);

        clock.Advance(PacketTime * 21);
        buffer.Ingest((2 + 20) & 0xF, Payload(2f));

        float[] output = new float[4096];
        buffer.Read(output);
        buffer.Read(output);
        buffer.SamplesLost.Should().Be(20 * PacketPairs);
        buffer.PacketsLost.Should().Be(20);
    }

    [Fact]
    public void A_long_silence_that_ends_on_the_next_count_is_a_held_up_reader_and_reports_nothing()
    {
        // The packets waited in the socket while the receive thread was held up; nothing is lost.
        var clock = new ManualClock();
        var buffer = new DaxIqStreamBuffer(capacityPairs: 4096, Rate, clock);
        Deliver(buffer, clock, counts: [0, 1, 2], level: 1f);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        buffer.Ingest(3, Payload(2f));
        buffer.Ingest(4, Payload(2f));

        float[] output = new float[4096];
        buffer.Read(output).Should().Be(5 * PacketPairs * 2);
        buffer.SamplesLost.Should().Be(0);
        buffer.PacketsLost.Should().Be(0);
    }

    [Fact]
    public void Inside_the_counters_cycle_a_late_packet_is_still_dropped_and_its_hole_counted_once()
    {
        var clock = new ManualClock();
        var buffer = new DaxIqStreamBuffer(capacityPairs: 4096, Rate, clock);
        Deliver(buffer, clock, counts: [0, 1, 3], level: 1f);
        clock.Advance(PacketTime);
        buffer.Ingest(2, Payload(9f)); // late: dropped
        Deliver(buffer, clock, counts: [4], level: 1f);

        float[] output = new float[4096];
        buffer.Read(output).Should().Be(2 * PacketPairs * 2);
        buffer.Read(output).Should().Be(2 * PacketPairs * 2);
        buffer.SamplesLost.Should().Be(PacketPairs);
        buffer.PacketsLost.Should().Be(1);
    }

    [Fact]
    public void A_short_restart_inside_the_counters_cycle_is_left_to_the_counter()
    {
        // The documented limit: a restart quicker than 16 packets looks like reordering to the
        // counter and to the clock alike. Pinned so a change to it is a decision.
        var clock = new ManualClock();
        var buffer = new DaxIqStreamBuffer(capacityPairs: 4096, Rate, clock);
        Deliver(buffer, clock, counts: [0, 1, 2, 3], level: 1f);

        clock.Advance(PacketTime * 5);
        buffer.Ingest(0, Payload(2f));

        buffer.PacketsLost.Should().Be(0);
        buffer.PacketsReceived.Should().Be(5);
    }

    // Delivers one packet per count, the clock moving one packet's time before each.
    private static void Deliver(DaxIqStreamBuffer buffer, ManualClock clock, int[] counts, float level)
    {
        foreach (int count in counts)
        {
            clock.Advance(PacketTime);
            buffer.Ingest(count, Payload(level));
        }
    }

    private static byte[] Payload(float level)
    {
        var payload = new byte[PacketPairs * 8];
        for (int i = 0; i < PacketPairs; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(i * 8, 4), level);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan((i * 8) + 4, 4), level);
        }

        return payload;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}

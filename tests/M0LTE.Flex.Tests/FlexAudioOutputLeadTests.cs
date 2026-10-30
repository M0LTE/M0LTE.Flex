using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>The DAX-TX sink's lead over the radio's playout, on a fake clock: the lead holds
/// steady, a hold-up of the sending thread shorter than the lead leaves no gap at the radio, a
/// longer one does not grow the lead, the drain waits for the radio's real end, and a lead of
/// zero is the one-packet pacing of earlier versions.</summary>
/// <remarks>The "radio" here plays each packet in order from the moment the first arrives, and
/// a packet that arrives after the previous one finished playing leaves a gap of the
/// difference: the behaviour seen on the air when the sender stalled. It is a model of what is
/// known, not of the radio's undocumented buffer depth.</remarks>
public sealed class FlexAudioOutputLeadTests
{
    private static readonly DaxStreamFormat Format = DaxStreamFormat.ReducedBandwidth;
    private static readonly double PacketMs = 1000.0 * Format.SamplesPerPacket / Format.SampleRate;

    [Fact]
    public void The_lead_holds_steady_through_a_long_burst()
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(80));

        rig.Output.Write(new float[Format.SampleRate * 3]);
        rig.Output.Drain();

        rig.Gaps.Should().BeEmpty();
        // Once the first lead's worth has gone out, the radio holds between the lead and the
        // lead plus one packet, all the way through.
        double[] held = rig.HeldAtArrivalMs.Skip(20).ToArray();
        held.Should().OnlyContain(ms => ms >= 80 - PacketMs - 1 && ms <= 80 + PacketMs + 1.5);
    }

    [Fact]
    public void A_hold_shorter_than_the_lead_leaves_no_gap_at_the_radio()
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(80)) { HoldAtPacket = 100, Hold = TimeSpan.FromMilliseconds(50) };

        rig.Output.Write(new float[Format.SampleRate * 2]);
        rig.Output.Drain();

        rig.Gaps.Should().BeEmpty();
        rig.LeftQueuedAtDrainMs.Should().BeLessThanOrEqualTo(1e-6);
    }

    [Fact]
    public void Without_a_lead_the_same_hold_leaves_a_gap_at_the_radio()
    {
        var rig = new Rig(TimeSpan.Zero) { HoldAtPacket = 100, Hold = TimeSpan.FromMilliseconds(50) };

        rig.Output.Write(new float[Format.SampleRate * 2]);
        rig.Output.Drain();

        rig.Gaps.Should().ContainSingle().Which.Should().BeInRange(50 - PacketMs - 1, 50);
    }

    [Fact]
    public void A_hold_longer_than_the_lead_does_not_grow_the_lead_and_the_drain_waits_for_the_late_end()
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(80)) { HoldAtPacket = 100, Hold = TimeSpan.FromMilliseconds(150) };

        rig.Output.Write(new float[Format.SampleRate * 2]);
        rig.Output.Drain();

        // The radio ran dry for what the lead could not cover.
        rig.Gaps.Should().ContainSingle().Which.Should().BeInRange(150 - 80 - PacketMs - 2, 150 - 80 + 1);
        // After it the lead is rebuilt, not grown by the stall.
        rig.HeldAtArrivalMs.Skip(130).Should().OnlyContain(ms => ms <= 80 + PacketMs + 1.5);
        // The drain covers the radio's real (late) end, so the unkey cuts nothing, and returns
        // promptly after it.
        rig.LeftQueuedAtDrainMs.Should().BeInRange(-2, 1e-6);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(0)]
    public void Drain_returns_when_the_radio_has_played_everything_and_no_later(int leadMs)
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(leadMs));

        // Not a whole number of packets: the drain flushes the last part, zero-padded.
        rig.Output.Write(new float[(Format.SamplesPerPacket * 50) + 17]);
        rig.Output.Drain();

        rig.Arrivals.Should().HaveCount(51);
        rig.LeftQueuedAtDrainMs.Should().BeInRange(-2, 1e-6);

        // The next keyup starts its own clock, so its first packets are not held back by the
        // last one's: with a lead, four packets are inside it and go out at once.
        int before = rig.Arrivals.Count;
        rig.Output.Write(new float[Format.SamplesPerPacket * 4]);
        double[] next = rig.Arrivals.Skip(before).Select(a => a.AtMs).ToArray();
        (next[^1] - next[0]).Should().BeLessThanOrEqualTo(leadMs > 0 ? 0 : (3 * PacketMs) + 1);
    }

    [Fact]
    public void A_lead_of_zero_paces_one_packet_at_a_time_as_before()
    {
        var rig = new Rig(TimeSpan.Zero);

        rig.Output.Write(new float[Format.SampleRate]);
        rig.Output.Drain();

        rig.Gaps.Should().BeEmpty();
        rig.HeldAtArrivalMs.Should().OnlyContain(ms => ms <= PacketMs + 1);
        rig.Sleeps.Should().HaveCountGreaterThan(150, "one sleep per packet, as the old pacing did");
    }

    [Theory]
    [InlineData(null, 80)]
    [InlineData("", 80)]
    [InlineData("  ", 80)]
    [InlineData("0", 0)]
    [InlineData("off", 0)]
    [InlineData("OFF", 0)]
    [InlineData(" off ", 0)]
    [InlineData("50", 50)]
    [InlineData("500", 500)]
    [InlineData("501", 80)]
    [InlineData("-5", 80)]
    [InlineData("40ms", 80)]
    [InlineData("1.5", 80)]
    public void The_environment_value_is_read_as_whole_milliseconds_or_off(string? value, int expectedMs)
    {
        FlexAudioOutput.ParseTransmitLead(value).Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    /// <summary>A sink on a fake clock, with the radio it feeds.</summary>
    private sealed class Rig
    {
        private long _ticks;

        public Rig(TimeSpan lead)
        {
            Output = new FlexAudioOutput(
                Send, () => 0x08000000u, Format, paceRealTime: true, lead,
                nowTicks: () => _ticks,
                sleep: ms =>
                {
                    Sleeps.Add(ms);
                    _ticks += TimeSpan.FromMilliseconds(ms).Ticks;
                });
        }

        public FlexAudioOutput Output { get; }

        /// <summary>The packet the sending thread is held up just before, if any.</summary>
        public int HoldAtPacket { get; init; } = -1;

        public TimeSpan Hold { get; init; }

        public List<(double AtMs, int Index)> Arrivals { get; } = [];

        public List<int> Sleeps { get; } = [];

        /// <summary>Each gap the radio had to fill with nothing, in ms.</summary>
        public List<double> Gaps
        {
            get
            {
                var gaps = new List<double>();
                Play(gaps, held: null);
                return gaps;
            }
        }

        /// <summary>How much audio the radio held as each packet arrived, in ms.</summary>
        public List<double> HeldAtArrivalMs
        {
            get
            {
                var held = new List<double>();
                Play(gaps: null, held);
                return held;
            }
        }

        /// <summary>How much the radio still had to play when the drain returned: positive is
        /// audio the unkey would cut off, negative is time waited after it had finished.</summary>
        public double LeftQueuedAtDrainMs => Play(null, null) - NowMs;

        private double NowMs => TimeSpan.FromTicks(_ticks).TotalMilliseconds;

        private void Send(byte[] packet)
        {
            if (Arrivals.Count == HoldAtPacket)
            {
                _ticks += Hold.Ticks;
            }

            Arrivals.Add((NowMs, Arrivals.Count));
        }

        /// <summary>Plays the packets that arrived since the last keyup began, in order, and
        /// returns when the last one finishes.</summary>
        private double Play(List<double>? gaps, List<double>? held)
        {
            double end = double.NegativeInfinity;
            foreach ((double at, _) in Arrivals)
            {
                if (!double.IsNegativeInfinity(end))
                {
                    held?.Add(Math.Max(0, end - at));
                    if (at > end + 1e-9)
                    {
                        gaps?.Add(at - end);
                    }
                }

                end = Math.Max(at, end) + PacketMs;
            }

            return end;
        }
    }
}

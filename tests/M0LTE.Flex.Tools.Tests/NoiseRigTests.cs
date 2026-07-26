using M0LTE.Flex.Tools.IqNoise;

namespace M0LTE.Flex.Tools.Tests;

/// <summary>
/// The characterisation rig's own signal generation and measurement. Its whole value is that a
/// finding can be attributed to the radio rather than the instrument, so the instrument's own
/// flatness, band edges and level have to hold.
/// </summary>
public sealed class NoiseRigTests
{
    private static Options Parse(params string[] args) => Options.Parse(args);

    private static float[] Generate(Options options, int pairs)
    {
        var source = new ComplexNoiseSource(options, pairs);
        var block = new float[pairs * 2];
        source.Fill(block);
        return block;
    }

    private static double PowerAt(float[] iq, double hz)
    {
        double re = 0;
        double im = 0;
        int count = iq.Length / 2;
        for (int n = 0; n < count; n++)
        {
            (double sin, double cos) = Math.SinCos(-2 * Math.PI * hz * n / Options.SampleRate);
            re += (iq[2 * n] * cos) - (iq[(2 * n) + 1] * sin);
            im += (iq[2 * n] * sin) + (iq[(2 * n) + 1] * cos);
        }

        return Math.Sqrt((re * re) + (im * im)) / count;
    }

    [Fact]
    public void The_band_is_placed_below_the_carrier_by_default()
    {
        // Only content below DC transmits, so a default that straddled it would put half the
        // requested width on air — which is exactly the bug this default was changed to fix.
        Options options = Parse("--freq", "14.2", "--bw", "3k");
        options.LowEdgeHz.Should().Be(-3000);
        options.HighEdgeHz.Should().Be(0);
    }

    [Fact]
    public void An_explicit_offset_still_wins()
    {
        Options options = Parse("--freq", "14.2", "--bw", "3k", "--offset", "0");
        options.LowEdgeHz.Should().Be(-1500);
        options.HighEdgeHz.Should().Be(1500);
        options.UntransmittableHighHz.Should().Be(1500, "the half above DC will not reach the air");
        options.IsEntirelyAboveDc.Should().BeFalse();
    }

    [Fact]
    public void A_band_entirely_above_dc_is_recognised_as_transmitting_nothing()
    {
        Options options = Parse("--freq", "14.2", "--bw", "10k", "--offset", "5k");
        options.IsEntirelyAboveDc.Should().BeTrue();
        options.UntransmittableHighHz.Should().Be(10000);
    }

    [Fact]
    public void Noise_fills_the_requested_band_and_stops_at_its_edges()
    {
        Options options = Parse("--freq", "14.2", "--bw", "3k");
        float[] iq = Generate(options, 24000);

        PowerAt(iq, -1500).Should().BeGreaterThan(PowerAt(iq, -4000) * 5);
        PowerAt(iq, -4000).Should().BeLessThan(0.003);
        PowerAt(iq, 1500).Should().BeLessThan(0.003);
    }

    [Fact]
    public void The_transmitted_level_is_the_same_however_narrow_the_band()
    {
        // The FIR's noise gain is compensated, so a 500 Hz band and a 10 kHz band hit the PA
        // equally. Without that, narrowing the band would quietly reduce transmit power.
        foreach (string bw in (string[])["500", "3k", "10k"])
        {
            var source = new ComplexNoiseSource(Parse("--freq", "14.2", "--bw", bw), 24000);
            var block = new float[48000];
            source.Fill(block);
            source.MeasuredRms.Should().BeApproximately(0.15, 0.02, $"at --bw {bw}");
        }
    }

    [Fact]
    public void Clipping_is_counted_rather_than_passed_off_as_the_radios_doing()
    {
        var clean = new ComplexNoiseSource(Parse("--freq", "14.2", "--bw", "3k"), 24000);
        clean.Fill(new float[48000]);
        clean.ClippedSamples.Should().Be(0);

        var hot = new ComplexNoiseSource(Parse("--freq", "14.2", "--bw", "3k", "--rms", "0.6"), 24000);
        hot.Fill(new float[48000]);
        hot.ClippedSamples.Should().BeGreaterThan(0, "Gaussian peaks past full scale must be flagged");
    }

    [Fact]
    public void Retuning_the_explore_tone_keeps_its_phase_continuous()
    {
        // The tone is moved by changing the NCO's phase increment while the accumulator runs on. A
        // discontinuity at the moment of retune would splatter a click across the whole band — the
        // very thing explore mode is used to measure — and it would look like a radio artefact.
        var source = new TunableToneSource(0.2, -1000);
        var before = new float[64];
        source.Fill(before);

        source.OffsetHz = -2000;
        var after = new float[64];
        source.Fill(after);

        // The last sample of the old frequency and the first of the new must differ by no more than
        // one step of the NEW frequency — i.e. the phase carried over rather than restarting.
        double lastPhase = Math.Atan2(before[^1], before[^2]);
        double firstPhase = Math.Atan2(after[1], after[0]);
        double delta = Math.Abs(Math.IEEERemainder(firstPhase - lastPhase, 2 * Math.PI));
        double expectedStep = 2 * Math.PI * 2000 / Options.SampleRate;

        delta.Should().BeLessThan(expectedStep * 1.5,
            "a reset phase would jump by an arbitrary amount, not by one sample's worth");
    }

    [Fact]
    public void The_tunable_tone_actually_moves_where_it_is_told()
    {
        var source = new TunableToneSource(0.2, -1000);
        var first = new float[4800];
        source.Fill(first);
        PowerAt(first, -1000).Should().BeGreaterThan(0.15);

        source.OffsetHz = -4000;
        var second = new float[4800];
        source.Fill(second);
        PowerAt(second, -4000).Should().BeGreaterThan(0.15);
        PowerAt(second, -1000).Should().BeLessThan(0.02);
    }

    [Fact]
    public void The_spectrum_estimator_measures_a_known_band_correctly()
    {
        // The rig's conclusions are only as good as this: if the measurement is wrong, so is every
        // width it has ever reported.
        float[] iq = Generate(Parse("--freq", "14.2", "--bw", "4k"), 65536);
        double[] spectrum = Spectrum.Estimate(iq, 4096);

        (double low, double high) = Spectrum.OccupiedBandwidth(spectrum, Options.SampleRate, 0.99);
        (high - low).Should().BeInRange(3600, 4400, "a 4 kHz band should measure as ~4 kHz");
        low.Should().BeInRange(-4400, -3600);
        high.Should().BeInRange(-400, 400);
    }

    [Fact]
    public void A_tone_probe_that_is_symmetric_cannot_be_read_as_an_image_measurement()
    {
        // Guarding the trap directly: with +f and -f both requested, each is the other's mirror and
        // the rejection figure is 0 dB by construction. Reading that as "the path is not complex"
        // is the mistake that produced two wrong conclusions about this radio.
        Options symmetric = Parse("--freq", "14.2", "--tone", "3k,-3k");
        symmetric.ToneOffsetsHz.Should().Equal(3000, -3000);

        Options single = Parse("--freq", "14.2", "--tone", "-3k");
        single.ToneOffsetsHz.Should().Equal(-3000);

        // A bare --sweep must use the single asymmetric probe, not the symmetric pair.
        Options swept = Parse("--freq", "14.2", "--sweep");
        swept.ToneOffsetsHz.Should().Equal(-3000);
    }
}

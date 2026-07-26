using M0LTE.Flex.Tools.IqGen;

namespace M0LTE.Flex.Tools.Tests;

/// <summary>
/// The generated test signals, checked against what the corpus README promises they contain. These
/// are the probes every conclusion about the radio rests on, so a signal that quietly drifts from
/// its description would invalidate the measurements rather than fail visibly.
/// </summary>
public sealed class SignalsTests
{
    private const int Rate = Signals.SampleRate;

    /// <summary>Magnitude of a signal's content at a given baseband frequency.</summary>
    private static double PowerAt(float[] iq, double hz)
    {
        double re = 0;
        double im = 0;
        int count = iq.Length / 2;
        for (int n = 0; n < count; n++)
        {
            (double sin, double cos) = Math.SinCos(-2 * Math.PI * hz * n / Rate);
            re += (iq[2 * n] * cos) - (iq[(2 * n) + 1] * sin);
            im += (iq[2 * n] * sin) + (iq[(2 * n) + 1] * cos);
        }

        return Math.Sqrt((re * re) + (im * im)) / count;
    }

    /// <summary>Total power between two baseband frequencies, by direct DFT over the range.</summary>
    private static double BandPower(float[] iq, double lowHz, double highHz)
    {
        double total = 0;
        for (double hz = lowHz; hz <= highHz; hz += 100)
        {
            double p = PowerAt(iq, hz);
            total += p * p;
        }

        return total;
    }

    private static double RmsPerComponent(float[] iq)
    {
        double sum = 0;
        foreach (float value in iq)
        {
            sum += value * value;
        }

        return Math.Sqrt(sum / iq.Length);
    }

    [Theory]
    [InlineData(-3000)]
    [InlineData(-500)]
    [InlineData(3000)]
    public void A_tone_sits_at_the_frequency_it_was_asked_for(double offsetHz)
    {
        float[] iq = Signals.Tone(offsetHz, 0.5, 0.2);

        PowerAt(iq, offsetHz).Should().BeGreaterThan(0.15);

        // And nowhere else — an image at -f would mean the generator, not the radio, was mirroring.
        PowerAt(iq, -offsetHz).Should().BeLessThan(0.001);
    }

    [Fact]
    public void A_tone_has_a_constant_envelope_so_it_cannot_be_mistaken_for_a_level_problem()
    {
        float[] iq = Signals.Tone(-3000, 0.2, 0.2);
        for (int n = 0; n < iq.Length / 2; n++)
        {
            double magnitude = Math.Sqrt((iq[2 * n] * iq[2 * n]) + (iq[(2 * n) + 1] * iq[(2 * n) + 1]));
            magnitude.Should().BeApproximately(0.2, 1e-5);
        }
    }

    [Fact]
    public void The_two_tone_probe_is_asymmetric_so_it_can_distinguish_a_mirror()
    {
        // A SYMMETRIC pair cannot: it contains the tone that would explain either answer. That
        // ambiguity produced two wrong conclusions about this radio, so it is worth a test.
        float[] iq = Signals.TwoTone(-2000, -5000, 0.5, 0.2);

        PowerAt(iq, -2000).Should().BeGreaterThan(0.05);
        PowerAt(iq, -5000).Should().BeGreaterThan(0.05);
        PowerAt(iq, 2000).Should().BeLessThan(0.005);
        PowerAt(iq, 5000).Should().BeLessThan(0.005);
    }

    [Fact]
    public void Noise_fills_the_band_it_was_given_and_stops_at_the_edges()
    {
        float[] iq = Signals.Noise(-3000, 0, 2, 0.15, seed: 1);

        PowerAt(iq, -1500).Should().BeGreaterThan(PowerAt(iq, -4000) * 5, "in band beats out of band");
        PowerAt(iq, -4000).Should().BeLessThan(0.002);
        PowerAt(iq, 1500).Should().BeLessThan(0.002, "nothing above DC — that half never transmits");
        RmsPerComponent(iq).Should().BeApproximately(0.15, 0.005);
    }

    [Fact]
    public void The_staircase_descends_which_is_the_whole_point_of_it()
    {
        // The corpus README promises "STRONGEST at the bottom". If that ever inverts, the entry
        // silently stops being an orientation check and starts confirming whatever it is shown.
        float[] iq = Signals.Staircase(-10000, 0, 5, 6, 4, 0.15, seed: 1);

        double lowest = PowerAt(iq, -9000);
        double middle = PowerAt(iq, -5000);
        double highest = PowerAt(iq, -1000);

        lowest.Should().BeGreaterThan(middle, "the step nearest -10 kHz is the strongest");
        middle.Should().BeGreaterThan(highest, "and it weakens monotonically toward DC");
    }

    [Fact]
    public void The_chirp_sweeps_upward_from_the_bottom_of_the_band()
    {
        float[] iq = Signals.Chirp(-10000, 0, 2, 0.2);
        int count = iq.Length / 2;

        // Take the first and last tenth; the instantaneous frequency should have climbed.
        float[] head = iq[..(count / 10 * 2)];
        float[] tail = iq[(count * 9 / 10 * 2)..];

        PowerAt(head, -9500).Should().BeGreaterThan(PowerAt(head, -500));
        PowerAt(tail, -500).Should().BeGreaterThan(PowerAt(tail, -9500));
    }

    [Fact]
    public void Qpsk_lands_at_its_centre_frequency_at_the_level_it_was_asked_for()
    {
        float[] iq = Signals.Qpsk(2400, 0.35, -2000, 2, 0.15, seed: 1);

        // A spread signal needs band power, not a single-frequency correlation: RRC at beta=0.35
        // and 2400 Bd occupies about 3.2 kHz, so essentially all of it sits within +/-1.8 kHz of
        // the centre and essentially none of it outside.
        double inBand = BandPower(iq, -3800, -200);
        double outOfBand = BandPower(iq, -12000, -3800) + BandPower(iq, -200, 12000);
        inBand.Should().BeGreaterThan(outOfBand * 50);

        // The level must match the other corpus entries, or comparing them on air means nothing.
        RmsPerComponent(iq).Should().BeApproximately(0.15, 0.005);
    }

    [Fact]
    public void The_same_seed_produces_byte_identical_noise_so_the_corpus_is_reproducible()
    {
        float[] first = Signals.Noise(-3000, 0, 1, 0.15, seed: 7);
        float[] second = Signals.Noise(-3000, 0, 1, 0.15, seed: 7);
        float[] different = Signals.Noise(-3000, 0, 1, 0.15, seed: 8);

        second.Should().Equal(first);
        different.Should().NotEqual(first);
    }
}

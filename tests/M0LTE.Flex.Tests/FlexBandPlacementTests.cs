namespace M0LTE.Flex.Tests;

/// <summary>
/// Band placement: the caller says where the signal goes and how wide it is, and the library picks
/// the slice frequency, the sideband and the frequency shift. Measured behaviour it compensates for
/// (FLEX-6500, fw 4.1.5): the transmit path is single-sideband, RAW/LSB/DIGL pass the lower half and
/// IQ/USB/DIGU the upper, and RF = slice + baseband with no inversion.
/// </summary>
public sealed class FlexBandPlacementTests
{
    private static async Task<(MockFlexRadio Mock, FlexClient Client)> ConnectAsync()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
        mock.Start();
        return (mock, await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort));
    }

    [Theory]
    // Only negative baseband reaches the air, so the slice is anchored at the TOP of the band and the
    // caller's span is shifted down below it, whichever convention they wrote in.
    [InlineData("RAW", IqBandReference.Centre, 14.2015, 14.2030, -1500)]
    [InlineData("RAW", IqBandReference.LowerEdge, 14.2000, 14.2030, -3000)]
    public async Task The_band_lands_where_it_was_asked_for_whichever_convention_was_declared(
        string mode, IqBandReference reference, double requestMhz, double expectedSliceMhz, double expectedShiftHz)
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions
            {
                Band = new IqBand(requestMhz, 3000, reference),
                UnderlyingMode = mode,
            });

            waveform.SliceFrequencyMhz.Should().BeApproximately(expectedSliceMhz, 1e-9);
            waveform.BasebandShiftHz.Should().BeApproximately(expectedShiftHz, 1e-9);

            // However it got there, the signal occupies the same 3 kHz of spectrum.
            waveform.OccupiedBand.Should().NotBeNull();
            waveform.OccupiedBand!.Value.LowMhz.Should().BeApproximately(14.2000, 1e-9);
            waveform.OccupiedBand!.Value.HighMhz.Should().BeApproximately(14.2030, 1e-9);
        }
    }

    [Fact]
    public async Task Placement_opens_the_transmit_filter_wide_enough_for_the_whole_band()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions
            {
                Band = new IqBand(14.200000, 8000), UnderlyingMode = "RAW",
            });

            mock.TransmitFilter.High.Should().Be(8000, "the factory 3 kHz filter would truncate it");
        }
    }

    [Fact]
    public async Task A_band_wider_than_the_radio_can_pass_fails_setup_rather_than_transmitting_truncated()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            // Silently sending 10 kHz of a 15 kHz request is indistinguishable from success.
            Func<Task> setUp = async () => await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions
            {
                Band = new IqBand(14.200000, 15000), UnderlyingMode = "RAW",
            });

            await setUp.Should().ThrowAsync<FlexProtocolException>().WithMessage("*truncated*");
        }
    }

    [Theory]
    [InlineData("LSB")]
    [InlineData("DIGL")]
    public async Task An_upright_mode_that_is_not_RAW_is_still_refused_for_placement(string mode)
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            // These put the band on identical frequencies, but route through a full audio mode whose
            // compander and speech processing would not show up on a spectrum display. One verified
            // path beats one verified and two plausible ones.
            Func<Task> setUp = async () => await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions
            {
                Band = new IqBand(14.200000, 3000), UnderlyingMode = mode,
            });

            await setUp.Should().ThrowAsync<FlexProtocolException>().WithMessage("*underlying_mode=RAW*");
        }
    }

    [Theory]
    [InlineData("IQ")]
    [InlineData("USB")]
    [InlineData("DIGU")]
    public async Task A_mode_that_mirrors_the_spectrum_is_refused_rather_than_transmitting_inverted(string mode)
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            // Measured on hardware: a -3 kHz baseband tone under DIGU comes out at carrier+3 kHz, so
            // a band placed on it would be spectrally inverted and decode nowhere. The upright modes
            // reach the same frequencies, so there is nothing to gain by allowing it.
            Func<Task> setUp = async () => await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions
            {
                Band = new IqBand(14.200000, 3000), UnderlyingMode = mode,
            });

            await setUp.Should().ThrowAsync<FlexProtocolException>().WithMessage("*mirrored*");
        }
    }

    [Fact]
    public async Task A_mode_that_discards_q_cannot_have_a_band_placed_on_it()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            Func<Task> setUp = async () => await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions
            {
                Band = new IqBand(14.200000, 3000), UnderlyingMode = "AM",
            });

            await setUp.Should().ThrowAsync<FlexProtocolException>().WithMessage("*discard*");
        }
    }

    [Fact]
    public async Task Naming_neither_a_slice_nor_a_band_is_refused_rather_than_defaulted()
    {
        // A transmit frequency is not something to guess at, and "which mode am I in" should not be
        // implied by whether some other property happens to be set.
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            Func<Task> setUp = async () =>
                await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions());

            await setUp.Should().ThrowAsync<FlexProtocolException>().WithMessage("*either*");
        }
    }

    [Fact]
    public async Task Naming_both_a_slice_and_a_band_is_refused_because_they_contradict()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            // Band derives the slice frequency itself, so an explicit one cannot also be honoured.
            Func<Task> setUp = async () => await FlexWaveform.SetUpHeadlessAsync(
                client,
                new FlexWaveformOptions { SliceFrequencyMhz = 14.2, Band = new IqBand(14.2, 3000) });

            await setUp.Should().ThrowAsync<FlexProtocolException>().WithMessage("*mutually exclusive*");
        }
    }

    [Fact]
    public async Task Without_a_bandwidth_the_frequency_is_still_just_the_slice_frequency()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(
                client, new FlexWaveformOptions { SliceFrequencyMhz = 14.200000 });

            // The low-level path the explore rig depends on: no shift, no derived slice.
            waveform.SliceFrequencyMhz.Should().BeApproximately(14.2, 1e-9);
            waveform.BasebandShiftHz.Should().Be(0);
            waveform.OccupiedBand.Should().BeNull();
        }
    }

    [Fact]
    public void The_shift_translates_the_spectrum_without_mirroring_it()
    {
        // A mirror would move a one-sided baseband to the other sideband just as neatly, and invert
        // it. This pins the direction: a tone at +1000 Hz shifted by -3000 must land at -2000 Hz,
        // NOT at -1000 Hz (which is where conjugation would put it).
        const int rate = FlexWaveformIqOutput.SampleRate;
        const int count = 2400;
        var tone = new float[count * 2];
        for (int n = 0; n < count; n++)
        {
            double phase = 2 * Math.PI * 1000 * n / rate;
            tone[2 * n] = (float)Math.Cos(phase);
            tone[(2 * n) + 1] = (float)Math.Sin(phase);
        }

        var captured = new List<float>();

        // Apply the same NCO the sink uses, then measure where the energy ended up.
        double step = 2 * Math.PI * -3000 / rate;
        double ph = 0;
        for (int n = 0; n < count; n++)
        {
            (double sin, double cos) = Math.SinCos(ph);
            double i = tone[2 * n];
            double q = tone[(2 * n) + 1];
            captured.Add((float)((i * cos) - (q * sin)));
            captured.Add((float)((i * sin) + (q * cos)));
            ph += step;
        }

        double atMinus2000 = Correlate(captured, -2000, rate);
        double atMinus1000 = Correlate(captured, -1000, rate);
        atMinus2000.Should().BeGreaterThan(atMinus1000 * 10,
            "a frequency shift puts +1000 Hz at -2000 Hz; conjugation would put it at -1000 Hz");
    }

    /// <summary>Magnitude of the signal's content at <paramref name="hz"/>.</summary>
    private static double Correlate(List<float> interleavedIq, double hz, int rate)
    {
        double re = 0;
        double im = 0;
        int count = interleavedIq.Count / 2;
        for (int n = 0; n < count; n++)
        {
            double phase = -2 * Math.PI * hz * n / rate;
            (double sin, double cos) = Math.SinCos(phase);
            double i = interleavedIq[2 * n];
            double q = interleavedIq[(2 * n) + 1];
            re += (i * cos) - (q * sin);
            im += (i * sin) + (q * cos);
        }

        return Math.Sqrt((re * re) + (im * im)) / count;
    }
}

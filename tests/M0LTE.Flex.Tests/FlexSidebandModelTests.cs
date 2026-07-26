namespace M0LTE.Flex.Tests;

/// <summary>
/// Cements the transmit-path model measured on M0LTE's FLEX-6500 (firmware 4.1.5, 2026-07-26) with a
/// single asymmetric tone per mode, and confirmed interactively by the direction the tone travels.
/// </summary>
/// <remarks>
/// <para>The model: <b>only the negative half of the baseband is transmitted, in every mode.</b> The
/// mode chooses which side of the carrier the surviving half lands on, and hence whether the caller's
/// spectrum arrives upright or mirrored.</para>
/// <para>These tests exist because the model was got wrong twice from a <i>symmetric</i> probe, which
/// cannot distinguish "the +f tone passed" from "the −f tone passed and the mode inverts". They pin
/// the conclusions so a future change has to argue with the measurement rather than re-derive it.</para>
/// </remarks>
public sealed class FlexSidebandModelTests
{
    private static async Task<(MockFlexRadio Mock, FlexClient Client)> ConnectAsync()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
        mock.Start();
        return (mock, await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort));
    }

    private static async Task<FlexProtocolException?> PlacementFailureAsync(string mode)
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            try
            {
                await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(
                    client,
                    new FlexWaveformOptions
                    {
                        Band = new IqBand(14.200000, 3000), UnderlyingMode = mode,
                    });
                return null;
            }
            catch (FlexProtocolException ex)
            {
                return ex;
            }
        }
    }

    [Theory]
    [InlineData("IQ")]
    [InlineData("USB")]
    [InlineData("DIGU")]
    public async Task Mirroring_modes_are_named_as_such_and_never_used_for_placement(string mode)
    {
        // Measured: a -3 kHz baseband tone comes out at carrier+3 kHz under each of these.
        FlexProtocolException? failure = await PlacementFailureAsync(mode);
        failure.Should().NotBeNull($"{mode} inverts the spectrum, so a band placed on it would decode nowhere");
        failure!.Message.Should().Contain("mirrored");
    }

    [Theory]
    [InlineData("AM")]
    [InlineData("FM")]
    public async Task Modes_that_discard_q_are_rejected_for_placement(string mode)
    {
        // Measured: both put the carrier plus BOTH sidebands on air — the signature of real-signal
        // modulation of the I channel alone.
        FlexProtocolException? failure = await PlacementFailureAsync(mode);
        failure.Should().NotBeNull($"{mode} discards Q, so it cannot carry complex IQ");
        failure!.Message.Should().Contain("discard");
    }

    [Theory]
    [InlineData("LSB")]
    [InlineData("DIGL")]
    public async Task Upright_modes_other_than_RAW_are_still_declined_for_placement(string mode)
    {
        // These place a band on identical frequencies, but route through a full audio mode whose
        // compander and speech processing are not known to be bypassed — and which no spectrum or
        // tone check would reveal. One verified path beats one verified and two plausible.
        FlexProtocolException? failure = await PlacementFailureAsync(mode);
        failure.Should().NotBeNull();
        failure!.Message.Should().Contain("underlying_mode=RAW");
    }

    [Fact]
    public async Task RAW_is_the_mode_placement_accepts()
    {
        FlexProtocolException? failure = await PlacementFailureAsync("RAW");
        failure.Should().BeNull("RAW is upright and the only mode verified end to end for arbitrary IQ");
    }

    [Fact]
    public async Task A_placed_band_is_shifted_entirely_below_dc_because_that_is_the_half_that_transmits()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions
            {
                Band = new IqBand(14.200000, 4000, IqBandReference.Centre),
            });

            // Caller writes -2000..+2000; a -4000 Hz shift lands it on -4000..0, entirely below DC.
            waveform.BasebandShiftHz.Should().Be(-2000);
            double lowestBasebandHz = -2000 + waveform.BasebandShiftHz;
            double highestBasebandHz = 2000 + waveform.BasebandShiftHz;
            lowestBasebandHz.Should().Be(-4000);
            highestBasebandHz.Should().Be(0, "any content above DC would simply not be transmitted");
        }
    }

    [Fact]
    public async Task The_slice_is_anchored_at_the_top_of_the_band_so_baseband_zero_is_its_upper_edge()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions
            {
                Band = new IqBand(14.190000, 10000, IqBandReference.LowerEdge),
            });

            // RF = slice + baseband and baseband is negative, so the slice sits at the TOP.
            waveform.SliceFrequencyMhz.Should().BeApproximately(14.200, 1e-9);
            waveform.OccupiedBand!.Value.LowMhz.Should().BeApproximately(14.190, 1e-9);
            waveform.OccupiedBand!.Value.HighMhz.Should().BeApproximately(14.200, 1e-9);
        }
    }
}

namespace M0LTE.Flex.Tests;

/// <summary>
/// The radio's transmit filter — the setting that actually caps occupied bandwidth on the waveform
/// IQ path. Measured on a FLEX-6500 (fw 4.1.5): it defaults to a 3 kHz SSB passband, is set with
/// <c>filter_low=</c>/<c>filter_high=</c> but reported as <c>lo</c>/<c>hi</c>, and clamps at 10 kHz.
/// </summary>
public sealed class FlexTransmitFilterTests
{
    [Fact]
    public async Task Headless_setup_widens_the_transmit_filter_so_a_wide_signal_is_not_truncated()
    {
        await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
        mock.Start();
        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);

        await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(client, new FlexWaveformOptions());

        // Left at the factory 3 kHz, an 8 kHz-wide burst would go out 3 kHz wide with no error.
        mock.TransmitFilter.Should().Be((0, FlexWaveformOptions.MaxTransmitFilterHighHz));
        waveform.TransmitFilter.Should().Be((0, FlexWaveformOptions.MaxTransmitFilterHighHz));
        waveform.TransmitFilterWarning.Should().BeNull();
    }

    [Fact]
    public async Task A_transmit_filter_above_the_radios_ceiling_is_clamped_and_warned_about()
    {
        await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
        mock.Start();
        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);

        await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(
            client, new FlexWaveformOptions { TransmitFilterHighHz = 20000 });

        // Silently clamped by the radio — the command still returns err=0, so the read-back is the
        // only way to know the band will be cut.
        mock.TransmitFilter.High.Should().Be(FlexWaveformOptions.MaxTransmitFilterHighHz);
        waveform.TransmitFilterWarning.Should().NotBeNull();
        waveform.TransmitFilterWarning.Should().Contain("clamps");
    }

    [Fact]
    public async Task A_null_transmit_filter_leaves_the_radios_own_setting_alone()
    {
        await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
        mock.Start();
        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);

        await using FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(
            client, new FlexWaveformOptions { TransmitFilterLowHz = null, TransmitFilterHighHz = null });

        mock.TransmitFilter.Should().Be((0, 3000), "the factory SSB passband must survive untouched");
        waveform.TransmitFilterWarning.Should().BeNull();
    }

    [Fact]
    public async Task The_transmit_filter_cannot_be_set_with_the_name_it_is_reported_under()
    {
        await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
        mock.Start();
        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);

        // Reported as lo/hi, but only filter_low=/filter_high= move it. Setting what you read back
        // fails — and looks like a working command if the result code is ignored.
        FlexResult rejected = await client.SendCommandAsync("transmit set hi=8000");
        rejected.IsOk.Should().BeFalse();
        rejected.Error.Should().Be(0x5000002D);

        FlexResult accepted = await client.SendCommandAsync("transmit set filter_high=8000");
        accepted.IsOk.Should().BeTrue();
        mock.TransmitFilter.High.Should().Be(8000);
    }
}

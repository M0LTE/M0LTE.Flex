namespace M0LTE.Flex.Tests;

/// <summary>
/// The transmitter's audio-source selection — the step whose absence made DAX transmit produce a
/// completely successful-looking transmission with nothing modulated onto it.
/// </summary>
/// <remarks>
/// Measured on a FLEX-6500 (fw 4.1.5, 2026-07-26): creating the DAX streams and pushing packets into
/// them is not sufficient. The transmitter has its own source selection, defaulting to the mic, and
/// every command in the DAX enable returns <c>err=0</c> either way. A 1 kHz tone produced no
/// modulation at all until <c>transmit set dax=1</c> was sent.
/// </remarks>
public sealed class FlexDaxTransmitSourceTests
{
    private static async Task<(MockFlexRadio Mock, FlexClient Client)> ConnectAsync()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.Start();
        return (mock, await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort));
    }

    [Fact]
    public async Task A_radio_takes_its_transmit_audio_from_the_mic_until_told_otherwise()
    {
        // The default the mock models, and the reason the bug was invisible: nothing about a fresh
        // radio suggests DAX audio will not be transmitted.
        await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        mock.TransmitSourceIsDax.Should().BeFalse();
    }

    [Fact]
    public async Task Headless_setup_points_the_transmitter_at_dax()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client, DaxStreamFormat.FullBandwidth, new FlexStationOptions());

            mock.TransmitSourceIsDax.Should().BeTrue("otherwise transmitted audio is silent");
            station.TransmitSourceIsDax.Should().BeTrue();
            station.TransmitSourceWarning.Should().BeNull();
            mock.CommandLog.Should().Contain("transmit set dax=1");
        }
    }

    [Fact]
    public async Task Opting_out_leaves_the_radios_own_selection_alone()
    {
        // It is a global setting that persists, so a receive-only session must be able to decline
        // to change what the radio transmits from.
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client,
                DaxStreamFormat.FullBandwidth,
                new FlexStationOptions { SelectDaxAsTransmitSource = false });

            mock.TransmitSourceIsDax.Should().BeFalse();
            station.TransmitSourceIsDax.Should().BeNull();
            station.TransmitSourceWarning.Should().BeNull("declining is not a failure");
            mock.CommandLog.Should().NotContain("transmit set dax=1");
        }
    }

    [Fact]
    public async Task The_selection_is_read_back_rather_than_assumed_from_a_zero_error_code()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client, DaxStreamFormat.FullBandwidth, new FlexStationOptions());

            // The command returning err=0 is exactly what happened while nothing reached the air, so
            // the contract is that the radio's own report is what gets believed.
            station.TransmitSourceIsDax.Should().Be(mock.TransmitSourceIsDax);
        }
    }

    [Fact]
    public async Task The_transmit_filter_is_reported_because_it_is_what_limits_audio_bandwidth()
    {
        // Measured on a FLEX-6500: an audio sweep through DAX was cut at exactly 10 kHz with the
        // filter at 10000 and at exactly 3 kHz with it at 3000. DAX is not a ~3 kHz path, and the
        // limit is global state that some other client may have left anywhere — so it is read back
        // rather than assumed.
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client, DaxStreamFormat.FullBandwidth, new FlexStationOptions());

            station.TransmitFilter.Should().NotBeNull();
            station.TransmitFilter!.Value.High.Should().Be(3000, "the radio's factory SSB passband");
        }
    }

    [Fact]
    public async Task Asking_for_a_wider_audio_passband_widens_it()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client,
                DaxStreamFormat.FullBandwidth,
                new FlexStationOptions { TransmitFilterHighHz = 8000 });

            station.TransmitFilter!.Value.High.Should().Be(8000);
            mock.TransmitFilter.High.Should().Be(8000);
        }
    }

    [Fact]
    public async Task Leaving_the_filter_alone_is_the_default_because_it_is_global()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client, DaxStreamFormat.FullBandwidth, new FlexStationOptions());

            // A station that quietly widened it would change what every other client transmits.
            mock.CommandLog.Should().NotContain(c => c.StartsWith("transmit set filter_high", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Transmitted_audio_still_reaches_the_radio_after_the_extra_step()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client, DaxStreamFormat.FullBandwidth, new FlexStationOptions());

            FlexAudioOutput output = station.CreateAudioOutput(paceRealTime: false);
            var tone = new float[DaxStreamFormat.FullBandwidth.SamplesPerPacket * 2];
            for (int n = 0; n < tone.Length; n++)
            {
                tone[n] = (float)Math.Sin(2 * Math.PI * 1000 * n / DaxStreamFormat.FullBandwidth.SampleRate) * 0.5f;
            }

            output.Write(tone);
            output.Drain();

            long deadline = Environment.TickCount64 + 2000;
            while (mock.CapturedTxSamples.Count < tone.Length && Environment.TickCount64 < deadline)
            {
                await Task.Delay(10);
            }

            mock.CapturedTxSamples.Should().HaveCountGreaterThanOrEqualTo(tone.Length);
            mock.CapturedTxSamples.Take(tone.Length).Should().Equal(tone);
        }
    }
}

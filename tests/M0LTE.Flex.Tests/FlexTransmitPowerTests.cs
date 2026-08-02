namespace M0LTE.Flex.Tests;

/// <summary>
/// Transmit RF power on the DAX station path.
/// </summary>
/// <remarks>
/// <para>Measured against a FLEX-6500 (fw 4.2.20.41343, 2026-08-02). Three findings shape this:</para>
/// <list type="number">
/// <item>RF power is held <b>per station</b>. An unbound command client's <c>transmit set
/// rfpower=N</c> is answered <c>err=0</c> and discarded — the value never moves. Registering as a
/// GUI client makes <c>transmit rfpower</c> report that station's own value instead.</item>
/// <item>A value above <c>max_power_level</c> is <b>rejected</b> (<c>0x50000048</c>), not clamped
/// and not rescaled. Asking for 30 with a ceiling of 15 is an error, not 15 W and not 4.5 W.</item>
/// <item><c>max_power_level</c> itself is radio-global and settable from any client — which is why
/// a station can read the ceiling it is about to be judged against.</item>
/// </list>
/// <para>Together those mean a wrong power cannot be detected from the command's reply. Every case
/// here is therefore asserted on the read-back.</para>
/// </remarks>
public sealed class FlexTransmitPowerTests
{
    [Fact]
    public async Task Headless_setup_sets_the_transmit_power_it_was_asked_for()
    {
        await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
        mock.Start();
        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);

        await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth, new FlexStationOptions { RfPower = 30 });

        mock.RfPower.Should().Be(30);
        station.RfPowerApplied.Should().Be(30);
    }

    [Fact]
    public async Task A_null_power_leaves_the_radios_own_setting_alone_but_still_reports_it()
    {
        await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth);
        mock.Start();
        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);

        await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth, new FlexStationOptions { RfPower = null });

        mock.RfPower.Should().Be(100, "an unasked-for power must not be changed");
        station.RfPowerApplied.Should().Be(
            100, "an inherited power is still worth reporting — otherwise it shapes the signal unseen");
        station.MaxPowerLevel.Should().Be(100);
    }

    [Fact]
    public async Task A_power_above_the_operators_ceiling_fails_setup_rather_than_transmitting_at_it()
    {
        // The radio rejects rather than reduces, so there is no "close enough" outcome to accept.
        await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth) { MaxPowerLevel = 15 };
        mock.Start();
        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);

        Func<Task> setUp = () => FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth, new FlexStationOptions { RfPower = 30 });

        (await setUp.Should().ThrowAsync<FlexProtocolException>())
            .WithMessage("*above this radio's Max Power Level of 15*")
            .WithMessage("*raise the limit at the rig*", "the message has to say what to do about it");
        mock.RfPower.Should().Be(100, "a refused request must not half-apply");
    }

    [Fact]
    public async Task A_power_the_radio_accepts_and_discards_fails_setup_rather_than_lying()
    {
        // The failure mode that cost an afternoon: err=0 and nothing changes, because the radio
        // does not consider the client a station. Only the read-back can tell.
        await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth) { DiscardRfPowerWrites = true };
        mock.Start();
        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);

        Func<Task> setUp = () => FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth,
            new FlexStationOptions { RfPower = 30, SetupTimeout = TimeSpan.FromSeconds(1) });

        (await setUp.Should().ThrowAsync<FlexProtocolException>())
            .WithMessage("*asked for transmit power 30*")
            .WithMessage("*still reports 100*", "the number it is actually running at is the point");
    }

    [Fact]
    public async Task The_attach_path_sets_power_too_since_it_is_also_a_station()
    {
        await using var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Attach);
        mock.Start();
        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);

        await using FlexStation station = await FlexStation.SetUpAsync(
            client, DaxStreamFormat.FullBandwidth, new FlexStationOptions { RfPower = 25 });

        mock.RfPower.Should().Be(25);
        station.RfPowerApplied.Should().Be(25);
    }
}

namespace M0LTE.Flex.Tests;

/// <summary>
/// The slice's receive filter — the other half of making DAX carry more than ~3 kHz.
/// </summary>
/// <remarks>
/// Transmitted bandwidth is capped by the global transmit filter (measured: an audio sweep is cut
/// exactly where it is set, up to a 10 kHz clamp). What reaches DAX-RX is capped separately, per
/// slice, by the slice's own filter — so a client that widens only the transmit side gets a wide
/// signal out and a 3 kHz window back in. Unlike the transmit clamp, the radio's limit on receive
/// width is <b>not</b> measured, so nothing here asserts one: the filter is asked for, read back,
/// and any disagreement is reported.
/// </remarks>
public sealed class FlexReceiveFilterTests
{
    private static async Task<(MockFlexRadio Mock, FlexClient Client)> ConnectAsync(
        Action<MockFlexRadio>? configure = null)
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        configure?.Invoke(mock);
        mock.Start();
        return (mock, await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort));
    }

    [Fact]
    public async Task A_fresh_slice_comes_up_on_an_ordinary_data_filter()
    {
        // The reason this setting exists: the default is narrow enough to cut anything wide, and
        // it is reported rather than assumed so that a stale one is visible.
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client, DaxStreamFormat.FullBandwidth, new FlexStationOptions());

            station.ReceiveFilter.Should().Be(
                (MockFlexRadio.DefaultSliceFilterLowHz, MockFlexRadio.DefaultSliceFilterHighHz));
            station.ReceiveFilterWarning.Should().BeNull();
        }
    }

    [Fact]
    public async Task Asking_for_a_wider_receive_passband_widens_it()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client,
                DaxStreamFormat.FullBandwidth,
                new FlexStationOptions { ReceiveFilterLowHz = 200, ReceiveFilterHighHz = 8000 });

            station.ReceiveFilter.Should().Be((200, 8000));
            mock.SliceFilter.Should().Be((200, 8000), "the radio has to have actually moved it");
            station.ReceiveFilterWarning.Should().BeNull();
        }
    }

    [Fact]
    public async Task Either_edge_can_be_set_without_the_other()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client,
                DaxStreamFormat.FullBandwidth,
                new FlexStationOptions { ReceiveFilterHighHz = 7200 });

            station.ReceiveFilter.Should().Be((MockFlexRadio.DefaultSliceFilterLowHz, 7200));
        }
    }

    [Fact]
    public async Task Leaving_the_filter_alone_is_the_default()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client, DaxStreamFormat.FullBandwidth, new FlexStationOptions());

            mock.CommandLog.Should().NotContain(
                c => c.Contains("filter_lo", StringComparison.Ordinal)
                     || c.Contains("filter_hi", StringComparison.Ordinal),
                "an unasked-for filter change is a change to what the operator hears");
        }
    }

    [Fact]
    public async Task A_radio_that_will_not_go_that_wide_is_reported_rather_than_believed()
    {
        // Nobody has measured whether a real slice limits its receive width, so a client cannot
        // assume the request took. Here the modelled radio clamps, and the station says so.
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync(m => m.MaxSliceFilterHighHz = 4000);
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client,
                DaxStreamFormat.FullBandwidth,
                new FlexStationOptions { ReceiveFilterHighHz = 9000 });

            station.ReceiveFilter!.Value.High.Should().Be(4000, "what the radio reports is the truth");
            station.ReceiveFilterWarning.Should().NotBeNull();
            station.ReceiveFilterWarning.Should().Contain("9000").And.Contain("4000");
        }
    }

    [Fact]
    public async Task An_upside_down_filter_is_refused_rather_than_sent()
    {
        // A low cut above the high cut would be a config error at the caller; sending it would
        // leave the slice in whatever state the radio makes of it.
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client,
                DaxStreamFormat.FullBandwidth,
                new FlexStationOptions { ReceiveFilterLowHz = 6000, ReceiveFilterHighHz = 3000 });

            mock.SliceFilter.Should().Be(
                (MockFlexRadio.DefaultSliceFilterLowHz, MockFlexRadio.DefaultSliceFilterHighHz));
            station.ReceiveFilterWarning.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Attach_mode_does_not_touch_the_slice_it_borrowed()
    {
        // In attach mode the slice belongs to SmartSDR, and its filter is what the operator is
        // watching the band through — same rule as the dial and the transmit filter.
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Attach);
        mock.Start();
        await using (mock)
        await using (FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort))
        {
            await using FlexStation station = await FlexStation.SetUpAsync(
                client,
                DaxStreamFormat.FullBandwidth,
                new FlexStationOptions { Keepalive = false, ReceiveFilterHighHz = 8000 });

            mock.SliceFilter.Should().Be(
                (MockFlexRadio.DefaultSliceFilterLowHz, MockFlexRadio.DefaultSliceFilterHighHz));
        }
    }
}

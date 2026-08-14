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
    public async Task The_passband_is_moved_with_filt_not_by_writing_back_what_the_slice_reports()
    {
        // The regression this file exists for now. A slice REPORTS its passband as filter_lo/
        // filter_hi and is MOVED by `filt <n> <lo> <hi>`; 0.11.0 through 0.13.0 wrote the reported
        // names back with `slice set`, which a 6500 does not act on, so the filter never moved on
        // hardware while every offline test passed against a mock that honoured the write.
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client,
                DaxStreamFormat.FullBandwidth,
                new FlexStationOptions { ReceiveFilterLowHz = 450, ReceiveFilterHighHz = 2550 });

            mock.CommandLog.Should().Contain("filt 0 450 2550");
            mock.CommandLog.Should().NotContain(
                c => c.StartsWith("slice set", StringComparison.Ordinal)
                     && c.Contains("filter_", StringComparison.Ordinal),
                "the radio answers that err=0 and discards it");
        }
    }

    [Fact]
    public async Task The_read_back_asks_the_radio_again_rather_than_waiting_to_be_told()
    {
        // The second half of the same lesson. `filt` moves the filter and says nothing further:
        // measured on a 6500, a filt that visibly narrowed the DSP produced no slice status on the
        // session that sent it, so 0.14.0 read back the value from `slice create` for the whole
        // window and reported a filter it had itself gone stale on. Re-subscribing re-dumps the
        // slice, and the dump lands before the subscribe's own reply, so it is a synchronisation
        // point rather than a poll.
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync();
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client,
                DaxStreamFormat.FullBandwidth,
                new FlexStationOptions { ReceiveFilterLowHz = 450, ReceiveFilterHighHz = 2550 });

            List<string> log = [.. mock.CommandLog];
            int filt = log.IndexOf("filt 0 450 2550");
            filt.Should().BeGreaterThanOrEqualTo(0, "the filter has to have been set at all");
            log.FindIndex(filt + 1, c => c == "sub slice all").Should().BeGreaterThan(
                filt, "the state after the write is only knowable by asking for it again");
            station.ReceiveFilter.Should().Be((450, 2550));
            station.ReceiveFilterWarning.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_radio_that_takes_the_command_and_ignores_it_is_not_reported_as_too_narrow()
    {
        // What the station actually saw: asked for 450-2550, told 0-3000. Wider, not narrower, so
        // nothing is deaf - and calling that "the radio would not go that wide", as this did in
        // either direction, sends the reader hunting a bandwidth limit that is not there.
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync(m => m.DiscardSliceFilterWrites = true);
        await using (mock)
        await using (client)
        {
            await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
                client,
                DaxStreamFormat.FullBandwidth,
                new FlexStationOptions { ReceiveFilterLowHz = 450, ReceiveFilterHighHz = 2550 });

            station.ReceiveFilterWarning.Should().NotBeNull();
            station.ReceiveFilterWarning.Should().NotContain(
                "would not go that wide", "the filter it reports contains the one that was asked for");
            station.ReceiveFilterWarning.Should().Contain("did not act on it")
                .And.Contain("filt 0 450 2550", "the command that was ignored is the thing to check");
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

            // `filt` carries both edges, so the one not asked for has to be read off the slice and
            // sent back unchanged rather than defaulted to something.
            station.ReceiveFilter.Should().Be((MockFlexRadio.DefaultSliceFilterLowHz, 7200));
            mock.CommandLog.Should().Contain($"filt 0 {MockFlexRadio.DefaultSliceFilterLowHz} 7200");
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
                c => c.StartsWith("filt ", StringComparison.Ordinal)
                     || c.Contains("filter_lo", StringComparison.Ordinal)
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
            station.ReceiveFilterWarning.Should().Contain(
                "would not go that wide", "this is the direction that phrase is for: the request was cut");
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

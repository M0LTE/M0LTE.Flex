using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>
/// The arbitration contract for a radio shared between transmitting clients. The mock is
/// single-session by design (multi-client TX semantics are unverified on hardware, and a mock
/// that encoded guesses would pin them as truth), so a phantom second station is scripted with
/// <see cref="MockFlexRadio.InjectStatusAsync"/> and a lost race with
/// <see cref="MockFlexRadio.SuppressInterlockOnXmit"/>. What is pinned here is OUR behaviour:
/// the command order, the sent-nothing-while-busy invariant, and the never-cut-the-winner rule.
/// </summary>
public sealed class FlexArbitratedPttTests
{
    private const string Peer = "SBBBBBBB";

    private static async Task<(MockFlexRadio Mock, FlexClient Client, FlexStation Station)> BringUpAsync()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth, new FlexStationOptions { Keepalive = false });
        return (mock, client, station);
    }

    private static async Task WaitForInterlockAsync(FlexClient client, string state)
    {
        long deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            if (client.TryGetObject("interlock", out IReadOnlyDictionary<string, string> interlock)
                && interlock.TryGetValue("state", out string? current)
                && current == state)
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"interlock never reached {state}");
    }

    [Fact]
    public async Task Key_Reasserts_The_Tx_Slice_On_Every_Keyup()
    {
        (MockFlexRadio mock, FlexClient client, FlexStation station) = await BringUpAsync();
        await using var _ = mock;
        FlexArbitratedPtt ptt = station.CreateArbitratedPtt();

        ptt.Key();
        ptt.Unkey();
        await WaitForInterlockAsync(client, "RECEIVE");
        ptt.Key();
        ptt.Unkey();

        mock.CommandLog.Count(c => c == "slice set 0 tx=1").Should().BeGreaterThanOrEqualTo(2,
            "a TX slice moved by someone else between keyups must be taken back");
        await station.DisposeAsync();
    }

    [Fact]
    public async Task A_Keyup_Orders_Filter_Then_Slice_Then_Xmit()
    {
        (MockFlexRadio mock, FlexClient client, FlexStation station) = await BringUpAsync();
        await using var _ = mock;
        FlexArbitratedPtt ptt = station.CreateArbitratedPtt(
            new FlexPttArbitrationOptions { TransmitFilterHighHz = 3400 });

        ptt.Key();
        ptt.Unkey();

        // The global writes land in dependency order, and only after the quiet check.
        List<string> log = [.. mock.CommandLog];
        int filter = log.FindIndex(c => c == "transmit set filter_high=3400");
        int slice = log.FindIndex(filter + 1, c => c == "slice set 0 tx=1");
        int xmit = log.FindIndex(slice + 1, c => c == "xmit 1");
        filter.Should().BeGreaterThan(-1, "the filter is re-asserted per keyup");
        slice.Should().BeGreaterThan(filter);
        xmit.Should().BeGreaterThan(slice);
        await station.DisposeAsync();
    }

    [Fact]
    public async Task A_Keyup_Waits_Out_Another_Stations_Burst()
    {
        (MockFlexRadio mock, FlexClient client, FlexStation station) = await BringUpAsync();
        await using var _ = mock;
        FlexArbitratedPtt ptt = station.CreateArbitratedPtt();

        await mock.InjectStatusAsync($"{Peer}|interlock state=TRANSMITTING");
        await WaitForInterlockAsync(client, "TRANSMITTING");
        int commandsBefore = mock.CommandLog.Count;

        Task key = Task.Run(ptt.Key);
        await Task.Delay(300);
        key.IsCompleted.Should().BeFalse("the peer holds the PA");
        mock.CommandLog.Count.Should().Be(commandsBefore,
            "waiting must put NOTHING on the wire - no filter write, no slice claim");

        await mock.InjectStatusAsync($"{Peer}|interlock state=RECEIVE");
        await key.WaitAsync(TimeSpan.FromSeconds(5));
        ptt.Unkey();
        await station.DisposeAsync();
    }

    [Fact]
    public async Task A_Contended_Giveup_Sends_Nothing_And_Throws()
    {
        (MockFlexRadio mock, FlexClient client, FlexStation station) = await BringUpAsync();
        await using var _ = mock;
        FlexArbitratedPtt ptt = station.CreateArbitratedPtt(
            new FlexPttArbitrationOptions
            {
                QuietWaitTimeout = TimeSpan.FromMilliseconds(300),
                TransmitFilterHighHz = 3400,
            });

        await mock.InjectStatusAsync($"{Peer}|interlock state=TRANSMITTING");
        await WaitForInterlockAsync(client, "TRANSMITTING");
        int commandsBefore = mock.CommandLog.Count;

        FluentActions.Invoking(ptt.Key).Should().Throw<FlexTxContendedException>()
            .WithMessage("*nothing was sent*");
        mock.CommandLog.Count.Should().Be(commandsBefore,
            "the give-up leaves the radio exactly as it found it");
        await station.DisposeAsync();
    }

    [Fact]
    public async Task A_Lost_Race_Is_Detected_And_Gives_Up_Contended()
    {
        (MockFlexRadio mock, FlexClient client, FlexStation station) = await BringUpAsync();
        await using var _ = mock;
        // xmit 1 is accepted but the interlock never says TRANSMITTING: somebody else won.
        mock.SuppressInterlockOnXmit = true;
        FlexArbitratedPtt ptt = station.CreateArbitratedPtt(
            new FlexPttArbitrationOptions
            {
                ConfirmTimeout = TimeSpan.FromMilliseconds(100),
                RetryBackoff = TimeSpan.FromMilliseconds(20),
                KeyAttempts = 2,
            });

        FluentActions.Invoking(ptt.Key).Should().Throw<FlexTxContendedException>()
            .WithMessage("*lost the keyup race*");

        // Each attempt withdrew its own xmit; attempts == 2.
        mock.CommandLog.Count(c => c == "xmit 1").Should().Be(2);
        mock.CommandLog.Count(c => c == "xmit 0").Should().Be(2);
        await station.DisposeAsync();
    }

    [Fact]
    public async Task Unkey_Is_Suppressed_When_The_Keyup_Was_Not_Won()
    {
        (MockFlexRadio mock, FlexClient client, FlexStation station) = await BringUpAsync();
        await using var _ = mock;
        mock.SuppressInterlockOnXmit = true;
        FlexArbitratedPtt ptt = station.CreateArbitratedPtt(
            new FlexPttArbitrationOptions
            {
                ConfirmTimeout = TimeSpan.FromMilliseconds(100),
                RetryBackoff = TimeSpan.FromMilliseconds(20),
                KeyAttempts = 1,
            });

        FluentActions.Invoking(ptt.Key).Should().Throw<FlexTxContendedException>();
        int unkeysAfterLoss = mock.CommandLog.Count(c => c == "xmit 0");

        // The transmitter loop's finally always calls Unkey after a failed Key; it must not
        // add an xmit 0 - the winner's burst is not ours to cut.
        ptt.Unkey();
        mock.CommandLog.Count(c => c == "xmit 0").Should().Be(unkeysAfterLoss);
        await station.DisposeAsync();
    }

    [Fact]
    public async Task A_Stale_Unkey_Requested_Counts_As_Quiet_After_The_Grace()
    {
        (MockFlexRadio mock, FlexClient client, FlexStation station) = await BringUpAsync();
        await using var _ = mock;
        FlexArbitratedPtt ptt = station.CreateArbitratedPtt(
            new FlexPttArbitrationOptions { StaleUnkeyGrace = TimeSpan.FromMilliseconds(200) });

        // The waveform path parks the interlock here and never announces RECEIVE (measured on
        // the 6500, modelled by the mock) - sm-ota's MS110D leg does exactly this, so without
        // the staleness bound one test burst would wedge the production daemon busy forever.
        await mock.InjectStatusAsync($"{Peer}|interlock state=UNKEY_REQUESTED");
        await WaitForInterlockAsync(client, "UNKEY_REQUESTED");

        ptt.AnotherStationTransmitting.Should().BeTrue("inside the grace it reads busy");
        Task key = Task.Run(ptt.Key);
        await key.WaitAsync(TimeSpan.FromSeconds(5));
        ptt.AnotherStationTransmitting.Should().BeFalse("a keyup we won is not another station");
        ptt.Unkey();
        await station.DisposeAsync();
    }

    [Fact]
    public async Task The_Busy_Predicate_Follows_The_Peer_And_Ignores_Our_Own_Win()
    {
        (MockFlexRadio mock, FlexClient client, FlexStation station) = await BringUpAsync();
        await using var _ = mock;
        FlexArbitratedPtt ptt = station.CreateArbitratedPtt();

        ptt.AnotherStationTransmitting.Should().BeFalse("a cold view reads quiet by design");

        await mock.InjectStatusAsync($"{Peer}|interlock state=TRANSMITTING");
        await WaitForInterlockAsync(client, "TRANSMITTING");
        ptt.AnotherStationTransmitting.Should().BeTrue();

        await mock.InjectStatusAsync($"{Peer}|interlock state=RECEIVE");
        await WaitForInterlockAsync(client, "RECEIVE");
        ptt.AnotherStationTransmitting.Should().BeFalse();

        ptt.Key();
        ptt.AnotherStationTransmitting.Should().BeFalse(
            "while this instance holds a won keyup, TRANSMITTING is us, not another station");
        ptt.Unkey();
        await station.DisposeAsync();
    }

    [Fact]
    public async Task The_Headless_Station_Name_Is_Offered_Best_Effort()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        await using var _ = mock;
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);

        // The mock answers unknown commands permissively; the pinned contract is only that the
        // name is OFFERED (and, by TryBestEffortAsync's construction, a rejection would not
        // fail bring-up - the same machinery every other best-effort setup step uses).
        FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth,
            new FlexStationOptions { Keepalive = false, HeadlessStationName = "pdn-test" });

        mock.CommandLog.Should().Contain("client station pdn-test");
        await station.DisposeAsync();
    }
}

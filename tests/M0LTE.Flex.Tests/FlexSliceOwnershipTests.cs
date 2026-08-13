using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>
/// The slice-ownership contract: noticing that the radio has taken our slice away, refusing to
/// transmit into it, rebuilding it, and knowing when to stop rebuilding.
/// </summary>
/// <remarks>
/// <para>Written against a real failure. A second headless station (a receive-only capture tool
/// on the same DAX channel) displaced GB7RDG's 40 m modem on a FLEX-6500 on 2026-08-07. The
/// modem stayed connected, kept its slice index, and went on keying: the radio answered
/// <c>slice set 0 tx=1</c> with <c>err=0</c> 10,528 consecutive times because slice index 0 now
/// resolved to the other client's slice, and every <c>xmit 1</c> failed with 0x50000043. The
/// station was deaf and mute for six days and nothing detected it.</para>
/// <para>So the properties under test are: a command's success is not evidence of its effect;
/// ownership is read back, not assumed; and a station that is being fought over stops fighting
/// rather than churning the radio.</para>
/// </remarks>
public sealed class FlexSliceOwnershipTests
{
    /// <summary>Fast policy for tests: the real defaults wait a second before the first rebuild.</summary>
    private static FlexContentionPolicy Fast(int lossThreshold = 3) => new()
    {
        MaxAttempts = 3,
        InitialBackoff = TimeSpan.FromMilliseconds(1),
        MaxBackoff = TimeSpan.FromMilliseconds(5),
        LossThreshold = lossThreshold,
        LossWindow = TimeSpan.FromMinutes(5),
    };

    private static FlexStationOptions Options(FlexContentionPolicy? policy = null) => new()
    {
        Keepalive = false,
        ContentionPolicy = policy ?? Fast(),
    };

    private static async Task<(MockFlexRadio Mock, FlexStation Station)> HeadlessAsync(
        FlexStationOptions? options = null)
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth, options ?? Options());
        return (mock, station);
    }

    /// <summary>Polls until <paramref name="until"/> holds, so tests do not race the status stream.</summary>
    private static async Task WaitForAsync(Func<bool> until, string what)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (until())
            {
                return;
            }

            await Task.Delay(5);
        }

        until().Should().BeTrue($"timed out waiting for {what}");
    }

    [Fact]
    public async Task Station_is_healthy_and_owns_its_slice_after_bring_up()
    {
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync();
        await using var _ = mock;
        await using var __ = station;

        station.Health.Should().Be(FlexStationHealth.Healthy);
        station.VerifyOwnership().IsOwned.Should().BeTrue();
        station.Lease.Current.SliceIndex.Should().Be("0");
        station.Lease.Current.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Station_notices_when_another_client_takes_its_slice()
    {
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync();
        await using var _ = mock;
        await using var __ = station;

        FlexOwnershipCheck? seen = null;
        station.SliceLost += check => seen = check;

        await mock.StealSliceAsync();
        await WaitForAsync(() => seen is not null, "the loss to be detected");

        seen!.Value.Fault.Should().Be(FlexOwnershipFault.ForeignOwner);
        station.Health.Should().Be(FlexStationHealth.SliceLost);
        station.Lease.Current.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Station_notices_when_its_slice_is_taken_out_of_use()
    {
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync();
        await using var _ = mock;
        await using var __ = station;

        await mock.RemoveSliceAsync();
        await WaitForAsync(
            () => station.Health == FlexStationHealth.SliceLost, "the removal to be detected");

        station.VerifyOwnership().Fault.Should().Be(FlexOwnershipFault.SliceNotInUse);
    }

    /// <summary>
    /// The regression test for the six-day outage: keying must fail loudly on a slice we no
    /// longer own, rather than succeeding into nothing.
    /// </summary>
    [Fact]
    public async Task Ptt_refuses_to_key_a_slice_another_client_owns()
    {
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync();
        await using var _ = mock;
        await using var __ = station;

        FlexPtt ptt = station.CreatePtt();

        // Before the theft, keying is normal.
        ptt.Key();
        ptt.Unkey();
        mock.CommandLog.Should().Contain("slice set 0 tx=1");

        await mock.StealSliceAsync();
        await WaitForAsync(
            () => station.Health == FlexStationHealth.SliceLost, "the theft to be detected");

        int commandsBefore = mock.CommandLog.Count;

        Action key = ptt.Key;
        FlexOwnershipException thrown = key.Should().Throw<FlexOwnershipException>().Which;
        thrown.Check.Fault.Should().Be(FlexOwnershipFault.ForeignOwner);

        // And it must not have touched the other client's slice on the way to failing. This is
        // the half that keeps the fix from being a better theft primitive than the bug was.
        mock.CommandLog.Skip(commandsBefore).Should().BeEmpty();
    }

    [Fact]
    public async Task Ptt_failure_is_catchable_as_a_protocol_exception()
    {
        // Hosts that already treat a keying failure as a dropped frame catch FlexProtocolException;
        // the ownership fault must not sail past them as an unhandled exception type.
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync();
        await using var _ = mock;
        await using var __ = station;

        FlexPtt ptt = station.CreatePtt();
        await mock.StealSliceAsync();
        await WaitForAsync(() => station.Health == FlexStationHealth.SliceLost, "the theft");

        Action key = ptt.Key;
        key.Should().Throw<FlexProtocolException>();
    }

    [Fact]
    public async Task Station_rebuilds_a_stolen_slice_and_keying_works_again()
    {
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync();
        await using var _ = mock;
        await using var __ = station;

        FlexPtt ptt = station.CreatePtt();
        int generationBefore = station.Lease.Current.Generation;

        await mock.StealSliceAsync();
        await WaitForAsync(() => station.Health == FlexStationHealth.SliceLost, "the theft");

        FlexRecoveryResult result = await station.RecoverAsync();

        result.Recovered.Should().BeTrue();
        result.Health.Should().Be(FlexStationHealth.Healthy);
        station.Health.Should().Be(FlexStationHealth.Healthy);
        station.VerifyOwnership().IsOwned.Should().BeTrue();

        // The lease moved on, which is how everything holding it follows the rebuild.
        station.Lease.Current.Generation.Should().BeGreaterThan(generationBefore);
        station.Lease.Current.IsValid.Should().BeTrue();

        // And the PTT, which was never recreated, keys again.
        ptt.Key();
        ptt.Unkey();
    }

    [Fact]
    public async Task Audio_streams_follow_a_rebuilt_slice_without_being_recreated()
    {
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync();
        await using var _ = mock;
        await using var __ = station;

        FlexAudioInput input = station.CreateAudioInput();
        FlexAudioOutput output = station.CreateAudioOutput(paceRealTime: false);
        input.Should().NotBeNull();
        output.Should().NotBeNull();

        await mock.StealSliceAsync();
        await WaitForAsync(() => station.Health == FlexStationHealth.SliceLost, "the theft");
        (await station.RecoverAsync()).Recovered.Should().BeTrue();

        // The shared lease is what they read, so a rebuild re-points them with no cooperation
        // from the host that owns the audio plumbing.
        station.Lease.Current.IsValid.Should().BeTrue();
        station.Lease.Current.RxStreamId.Should().NotBe(0);
    }

    /// <summary>
    /// The answer to "what if something persistently fights it": stop, and say so.
    /// </summary>
    [Fact]
    public async Task Station_stands_down_instead_of_fighting_over_a_contested_slice()
    {
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync(Options(Fast(lossThreshold: 2)));
        await using var _ = mock;
        await using var __ = station;

        var reports = new List<FlexStationHealthReport>();
        station.HealthChanged += report => reports.Add(report);

        // First loss: recoverable, and recovered.
        await mock.StealSliceAsync();
        await WaitForAsync(() => station.Health == FlexStationHealth.SliceLost, "the first theft");
        (await station.RecoverAsync()).Recovered.Should().BeTrue();

        // Second loss inside the window: that is a fight, not a transient.
        await mock.StealSliceAsync();
        await WaitForAsync(
            () => station.Health == FlexStationHealth.Contended, "the station to stand down");

        reports.Should().Contain(r => r.Health == FlexStationHealth.Contended);
        reports.Last(r => r.Health == FlexStationHealth.Contended).Detail
            .Should().Contain("Standing down");
    }

    [Fact]
    public async Task A_stood_down_station_refuses_to_rebuild()
    {
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync(Options(Fast(lossThreshold: 1)));
        await using var _ = mock;
        await using var __ = station;

        await mock.StealSliceAsync();
        await WaitForAsync(() => station.Health == FlexStationHealth.Contended, "the stand-down");

        int commandsBefore = mock.CommandLog.Count;
        FlexRecoveryResult result = await station.RecoverAsync();

        result.Recovered.Should().BeFalse();
        result.Health.Should().Be(FlexStationHealth.Contended);
        result.Detail.Should().Contain("stood down");

        // Nothing went to the radio: standing down means standing down.
        mock.CommandLog.Should().HaveCount(commandsBefore);
    }

    [Fact]
    public async Task Never_policy_reports_a_loss_but_never_retakes_the_slice()
    {
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync(
            Options(FlexContentionPolicy.Never));
        await using var _ = mock;
        await using var __ = station;

        await mock.StealSliceAsync();
        await WaitForAsync(() => station.Health == FlexStationHealth.Contended, "the stand-down");

        int commandsBefore = mock.CommandLog.Count;
        (await station.RecoverAsync()).Recovered.Should().BeFalse();
        mock.CommandLog.Should().HaveCount(commandsBefore);
    }

    [Fact]
    public async Task Station_does_not_remove_a_slice_another_client_now_owns()
    {
        // The slice-leak fix removes the slice we created on dispose. Once another client owns
        // that index, the same command deletes THEIR slice - so the ownership check gates it.
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync();
        await using var _ = mock;

        await mock.StealSliceAsync();
        await WaitForAsync(() => station.Health == FlexStationHealth.SliceLost, "the theft");

        await station.DisposeAsync();

        mock.CommandLog.Should().NotContain("slice remove 0");
    }

    [Fact]
    public async Task Receive_only_station_never_writes_the_radios_global_transmit_state()
    {
        // A capture tool has no business changing the transmit source, filter or power: all
        // three are global and persistent, so they outlive its process and change what the
        // operator's other clients put on air.
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        await using var _ = mock;

        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client,
            DaxStreamFormat.FullBandwidth,
            new FlexStationOptions
            {
                Keepalive = false,
                ReceiveOnly = true,
                TransmitFilterHighHz = 2550,
                RfPower = 50,
            });

        station.SliceIndex.Should().Be("0");
        mock.CommandLog.Should().NotContain(c => c.StartsWith("transmit set dax=", StringComparison.Ordinal));
        mock.CommandLog.Should().NotContain(c => c.StartsWith("transmit set filter_high=", StringComparison.Ordinal));
        mock.CommandLog.Should().NotContain(c => c.StartsWith("transmit set rfpower=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Claiming_a_dax_channel_another_client_holds_is_warned_about()
    {
        // The collision that started all this: two headless stations both taking the default DAX
        // channel, the second silently unwiring the first. Not fatal - a caller may mean to take
        // it - but never again silent.
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync();
        await using var _ = mock;
        await using var __ = station;

        station.DaxChannelWarning.Should().BeNull("nothing else held the channel at bring-up");

        // Another client turns up holding the channel this station uses.
        await mock.InjectStatusAsync(
            "S1A2B3C4D|slice 1 in_use=1 dax=1 client_handle=0xDEADBEEF index_letter=B");
        await WaitForAsync(
            () => station.Client.TryGetObject("slice 1", out IReadOnlyDictionary<string, string> s)
                && s.Count > 0,
            "the foreign slice to appear");

        // Force the DAX enable to run again by rebuilding.
        await mock.StealSliceAsync();
        await WaitForAsync(() => station.Health == FlexStationHealth.SliceLost, "the theft");
        (await station.RecoverAsync()).Recovered.Should().BeTrue();

        station.DaxChannelWarning.Should().NotBeNull();
        station.DaxChannelWarning.Should().Contain("slice 1").And.Contain("DEADBEEF");
    }

    [Fact]
    public async Task Recovery_is_a_no_op_when_the_slice_is_still_owned()
    {
        (MockFlexRadio mock, FlexStation station) = await HeadlessAsync();
        await using var _ = mock;
        await using var __ = station;

        int commandsBefore = mock.CommandLog.Count;
        FlexRecoveryResult result = await station.RecoverAsync();

        result.Recovered.Should().BeTrue();
        result.Attempts.Should().Be(0);
        mock.CommandLog.Should().HaveCount(commandsBefore);
    }
}

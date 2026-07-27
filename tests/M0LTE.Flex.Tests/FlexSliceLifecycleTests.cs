using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>
/// The slice-lifecycle contract on teardown — the headless slice-leak fix. A headless
/// <see cref="FlexStation"/>/<see cref="FlexWaveform"/> creates its own slice, and a real
/// FLEX-6500 does NOT auto-remove that slice when the client disconnects: leaked slices
/// accumulate (each tuned to the TX frequency, its client handle dead) until the radio's
/// four-slice limit is hit and every subsequent <c>slice create</c> fails with 0x50000003,
/// stalling all callers. So headless dispose must <c>slice remove</c> the slice it created;
/// the attach path, which binds to a slice a running SmartSDR owns, must NOT. Proven offline
/// against <see cref="MockFlexRadio"/>, whose <see cref="MockFlexRadio.CommandLog"/> records
/// every command the client sent.
/// </summary>
public sealed class FlexSliceLifecycleTests
{
    [Fact]
    public async Task Headless_station_removes_its_created_slice_on_dispose()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        await using var _ = mock;

        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth, new FlexStationOptions { Keepalive = false });

        // The headless bring-up created a slice, so one exists to leak.
        station.SliceIndex.Should().Be("0");

        await station.DisposeAsync();

        // Dispose must have removed the slice it created — otherwise it leaks on a real radio and
        // sessions pile up until the four-slice wall. This is the fix.
        mock.CommandLog.Should().Contain("slice remove 0");
    }

    [Fact]
    public async Task Attach_station_leaves_the_bound_slice_in_place_on_dispose()
    {
        // Attach mode binds to a slice a running SmartSDR already owns; removing it on teardown
        // would pull the slice out from under the operator. Dispose must NOT remove it — the
        // _createdSlice guard is what keeps the leak fix from over-reaching here.
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Attach);
        mock.Start();
        await using var _ = mock;

        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        FlexStation station = await FlexStation.SetUpAsync(
            client, DaxStreamFormat.FullBandwidth, new FlexStationOptions { Keepalive = false });

        station.SliceIndex.Should().Be("0");

        await station.DisposeAsync();

        mock.CommandLog.Should().NotContain("slice remove 0");
    }

    [Fact]
    public async Task Headless_waveform_removes_its_created_slice_on_dispose()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Headless);
        mock.Start();
        await using var _ = mock;

        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        FlexWaveform waveform = await FlexWaveform.SetUpHeadlessAsync(
            client, new FlexWaveformOptions { SliceFrequencyMhz = 14.1 });

        waveform.SliceIndex.Should().Be("0");

        await waveform.DisposeAsync();

        // The waveform path is always headless (it only has SetUpHeadlessAsync), so it always owns
        // the slice it created and must always remove it.
        mock.CommandLog.Should().Contain("slice remove 0");
    }
}

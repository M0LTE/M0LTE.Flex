using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>
/// Phase-3 headless bring-up against the in-process <see cref="MockFlexRadio"/> in
/// <see cref="MockSetupMode.Headless"/> (no SmartSDR): <c>client gui</c> → create-our-own-slice
/// → best-effort self-bind (tolerating the redundant-bind error the real FLEX-6500 returns) →
/// DAX enable. Proves the headless path reaches DAX-up and does not fail on the rejected bind.
/// See docs/flex-integration.md §8.
/// </summary>
public sealed class FlexHeadlessSetupTests
{
    private const uint AlreadyBoundError = 0x5000003E;

    private static async Task<(MockFlexRadio Mock, FlexClient Client)> ConnectAsync(
        DaxStreamFormat format, MockRxMode mode = MockRxMode.Silence, string sliceLetter = "A")
    {
        var mock = new MockFlexRadio(format, mode, MockSetupMode.Headless, sliceLetter: sliceLetter);
        mock.Start();
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        return (mock, client);
    }

    [Fact]
    public async Task Headless_setup_creates_a_slice_and_reaches_dax_up()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync(DaxStreamFormat.FullBandwidth);
        await using var _ = mock;
        await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth, new FlexStationOptions { Keepalive = false });

        // Found our created slice by client handle (not a station name), and both DAX streams
        // came up.
        station.SliceIndex.Should().Be("0");
        station.RxStreamId.Should().Be(0x04000000u);
        station.TxStreamId.Should().Be(0x08000000u);
    }

    [Fact]
    public async Task Headless_setup_tolerates_the_redundant_bind_rejection()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync(DaxStreamFormat.FullBandwidth);
        await using var _ = mock;
        await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth, new FlexStationOptions { Keepalive = false });

        // The mock returns the same error the real FLEX-6500 returns for the redundant
        // self-bind; setup must swallow it (surfaced here) and still complete.
        station.HeadlessBindResult.Should().NotBeNull();
        station.HeadlessBindResult!.Value.IsOk.Should().BeFalse();
        station.HeadlessBindResult!.Value.Error.Should().Be(AlreadyBoundError);
        station.TxStreamId.Should().NotBe(0u); // reached DAX-up regardless
    }

    [Fact]
    public async Task Headless_setup_tunes_the_slice_off_the_persisted_band_to_the_requested_freq()
    {
        // The mock models band persistence: `slice create` reports the PERSISTED band
        // (14.100000), ignoring the requested 7.050100. Only EnsureTunedAsync's `slice t` moves
        // the slice — so a slice that ends up on 7.050100 proves the tune fix ran.
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync(DaxStreamFormat.FullBandwidth);
        await using var _ = mock;
        await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth,
            new FlexStationOptions { Keepalive = false, Frequency = "7.050100" });

        station.Client.TryGetObject("slice " + station.SliceIndex,
            out IReadOnlyDictionary<string, string> slice).Should().BeTrue();
        slice.GetValueOrDefault("RF_frequency").Should().Be("7.050100");
        station.TuneWarning.Should().BeNull(); // verified on-frequency
    }

    [Fact]
    public async Task Headless_setup_issues_band_persistence_disable_and_explicit_tune()
    {
        // Assert the proven live sequence via the mock's command log: disable band persistence,
        // then `slice t <idx> <freq>` (%.6f). The redundant `radio set`/`slice set active` are
        // best-effort; the `slice t` is the load-bearing fix.
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync(DaxStreamFormat.FullBandwidth);
        await using var _ = mock;
        await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth,
            new FlexStationOptions { Keepalive = false, Frequency = "7.050100" });

        IReadOnlyList<string> log = mock.CommandLog;
        log.Should().Contain("radio set band_persistence_enabled=0");
        log.Should().Contain($"slice set {station.SliceIndex} active=1");
        log.Should().Contain($"slice t {station.SliceIndex} 7.050100");

        // Order: the tune fix runs AFTER slice create and BEFORE the DAX enable (dax set/stream).
        int create = IndexOfPrefix(log, "slice create");
        int tune = IndexOfPrefix(log, $"slice t {station.SliceIndex} 7.050100");
        int daxEnable = IndexOfPrefix(log, $"slice set {station.SliceIndex} dax=");
        create.Should().BeGreaterThanOrEqualTo(0);
        tune.Should().BeGreaterThan(create);
        daxEnable.Should().BeGreaterThan(tune);
    }

    [Fact]
    public async Task Headless_setup_uses_the_requested_slice_letter()
    {
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync(
            DaxStreamFormat.FullBandwidth, sliceLetter: "B");
        await using var _ = mock;
        await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, DaxStreamFormat.FullBandwidth, new FlexStationOptions { Keepalive = false, SliceLetter = "B" });

        station.SliceIndex.Should().Be("0"); // numeric index of the created slice
    }

    [Fact]
    public async Task Headless_rx_and_tx_audio_flow_after_setup()
    {
        DaxStreamFormat format = DaxStreamFormat.FullBandwidth;
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync(format);
        await using var _ = mock;
        await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, format, new FlexStationOptions { Keepalive = false });

        // TX: audio out is captured by the mock.
        FlexAudioOutput output = station.CreateAudioOutput(paceRealTime: false);
        float[] tone = MakeTone(count: 512, frequency: 1500, sampleRate: 48000);
        output.Write(tone);
        output.Drain();
        await WaitForAsync(() => mock.CapturedTxSamples.Count >= tone.Length);
        float[] captured = [.. mock.CapturedTxSamples];
        captured.Take(tone.Length).Should().Equal(tone);

        // RX: replayed audio reaches FlexAudioInput byte-exact (full-bw float32).
        using FlexAudioInput input = station.CreateAudioInput(packetBuffer: 3);
        int expectedPackets = (tone.Length + 255) / 256;
        await mock.ReplayRxAsync(tone);
        await WaitForAsync(() => input.PacketsReceived >= expectedPackets);
        input.Flush();
        float[] recovered = Drain(input, tone.Length);
        recovered.Should().Equal(tone);
    }

    [Fact]
    public async Task Headless_ptt_drives_the_interlock_state()
    {
        DaxStreamFormat format = DaxStreamFormat.ReducedBandwidth;
        (MockFlexRadio mock, FlexClient client) = await ConnectAsync(format);
        await using var _ = mock;
        await using FlexStation station = await FlexStation.SetUpHeadlessAsync(
            client, format, new FlexStationOptions { Keepalive = false });
        FlexPtt ptt = station.CreatePtt();

        ptt.Key();
        await WaitForAsync(() => InterlockState(station) == "TRANSMITTING");
        InterlockState(station).Should().Be("TRANSMITTING");

        ptt.Unkey();
        await WaitForAsync(() => InterlockState(station) == "RECEIVE");
        InterlockState(station).Should().Be("RECEIVE");
    }

    private static int IndexOfPrefix(IReadOnlyList<string> log, string prefix)
    {
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string InterlockState(FlexStation station) =>
        station.Client.TryGetObject("interlock", out IReadOnlyDictionary<string, string> interlock)
            ? interlock.GetValueOrDefault("state", "")
            : "";

    private static float[] MakeTone(int count, double frequency, int sampleRate)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = 0.5f * MathF.Sin(2f * MathF.PI * (float)frequency * i / sampleRate);
        }

        return samples;
    }

    private static float[] Drain(FlexAudioInput input, int expected)
    {
        var recovered = new List<float>(expected);
        var buffer = new float[1024];
        int got;
        while (recovered.Count < expected && (got = input.Read(buffer)) > 0)
        {
            recovered.AddRange(buffer.AsSpan(0, got).ToArray());
        }

        return [.. recovered.Take(expected)];
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        long start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException("condition not met in time");
            }

            await Task.Delay(10);
        }
    }
}

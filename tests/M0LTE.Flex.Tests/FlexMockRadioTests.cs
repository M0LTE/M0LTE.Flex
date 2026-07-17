using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>Phase-1 integration against the in-process <see cref="MockFlexRadio"/> in
/// <b>attach</b> mode (coexisting with a running SmartSDR): the DAX enable sequence, the
/// DAX-RX audio path (replay → <see cref="FlexAudioInput"/>), the DAX-TX audio path
/// (<see cref="FlexAudioOutput"/> → mock capture) and slice PTT. The headless bring-up has
/// its own coverage in <see cref="FlexHeadlessSetupTests"/>.</summary>
public sealed class FlexMockRadioTests
{
    private static async Task<(MockFlexRadio Mock, FlexStation Station)> SetUpAsync(
        DaxStreamFormat format, MockRxMode mode)
    {
        var mock = new MockFlexRadio(format, mode, MockSetupMode.Attach);
        mock.Start();
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        FlexStation station = await FlexStation.SetUpAsync(
            client, format, new FlexStationOptions { Keepalive = false });
        return (mock, station);
    }

    [Fact]
    public async Task Station_setup_binds_the_slice_and_creates_both_streams()
    {
        (MockFlexRadio mock, FlexStation station) = await SetUpAsync(
            DaxStreamFormat.ReducedBandwidth, MockRxMode.Silence);
        await using var _ = mock;
        await using var __ = station;

        station.SliceIndex.Should().Be("0");
        station.RxStreamId.Should().Be(0x04000000u);
        station.TxStreamId.Should().Be(0x08000000u);
    }

    [Fact]
    public async Task Replayed_full_bandwidth_rx_audio_round_trips_exactly()
    {
        (MockFlexRadio mock, FlexStation station) = await SetUpAsync(
            DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        await using var _ = mock;
        await using var __ = station;
        using FlexAudioInput input = station.CreateAudioInput(packetBuffer: 3);

        float[] tone = MakeTone(count: 300, frequency: 1000, sampleRate: 48000);
        int expectedPackets = (tone.Length + 255) / 256;
        await mock.ReplayRxAsync(tone);
        await WaitForAsync(() => input.PacketsReceived >= expectedPackets);
        input.Flush();

        float[] recovered = Drain(input, tone.Length);
        recovered.Should().HaveCount(tone.Length);
        recovered.Should().Equal(tone); // float32 is byte-exact through the DAX round-trip
    }

    [Fact]
    public async Task Replayed_reduced_bandwidth_rx_audio_round_trips_within_quantisation()
    {
        (MockFlexRadio mock, FlexStation station) = await SetUpAsync(
            DaxStreamFormat.ReducedBandwidth, MockRxMode.Silence);
        await using var _ = mock;
        await using var __ = station;
        using FlexAudioInput input = station.CreateAudioInput(packetBuffer: 3);

        float[] tone = MakeTone(count: 200, frequency: 800, sampleRate: 24000);
        int expectedPackets = (tone.Length + 127) / 128;
        await mock.ReplayRxAsync(tone);
        await WaitForAsync(() => input.PacketsReceived >= expectedPackets);
        input.Flush();

        float[] recovered = Drain(input, tone.Length);
        for (int i = 0; i < tone.Length; i++)
        {
            recovered[i].Should().BeApproximately(tone[i], 1e-3f); // s16 quantisation
        }
    }

    [Fact]
    public async Task Transmitted_audio_is_captured_by_the_mock()
    {
        (MockFlexRadio mock, FlexStation station) = await SetUpAsync(
            DaxStreamFormat.FullBandwidth, MockRxMode.Silence);
        await using var _ = mock;
        await using var __ = station;
        FlexAudioOutput output = station.CreateAudioOutput(paceRealTime: false);

        float[] tone = MakeTone(count: 512, frequency: 1500, sampleRate: 48000);
        output.Write(tone);
        output.Drain();

        await WaitForAsync(() => mock.CapturedTxSamples.Count >= tone.Length);
        float[] captured = [.. mock.CapturedTxSamples];
        captured.Take(tone.Length).Should().Equal(tone);
    }

    [Fact]
    public async Task Ptt_key_and_unkey_drive_the_interlock_state()
    {
        (MockFlexRadio mock, FlexStation station) = await SetUpAsync(
            DaxStreamFormat.ReducedBandwidth, MockRxMode.Silence);
        await using var _ = mock;
        await using var __ = station;
        FlexPtt ptt = station.CreatePtt();

        ptt.Key();
        await WaitForAsync(() => InterlockState(station) == "TRANSMITTING");
        InterlockState(station).Should().Be("TRANSMITTING");

        ptt.Unkey();
        await WaitForAsync(() => InterlockState(station) == "RECEIVE");
        InterlockState(station).Should().Be("RECEIVE");
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

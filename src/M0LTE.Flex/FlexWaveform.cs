using System.Diagnostics;
using System.Globalization;

namespace M0LTE.Flex;

/// <summary>Options for bringing up a FlexRadio <see cref="FlexWaveform"/> (IQ TX via the Waveform
/// API). Defaults target the wideband complex path proven on M0LTE's 6500 (docs/flex-integration.md
/// §9.5): <c>underlying_mode=RAW</c> with a ±12 kHz TX filter.</summary>
public sealed record FlexWaveformOptions
{
    /// <summary>The waveform's friendly name (≤20 chars). Default "PdnWfm".</summary>
    public string Name { get; init; } = "PdnWfm";

    /// <summary>The 1–4 char mode designator the slice is switched to (becomes the slice
    /// <c>mode</c>). Default "PDN".</summary>
    public string Mode { get; init; } = "PDN";

    /// <summary>The underlying radio mode the waveform is layered on. <c>RAW</c> passes true
    /// wideband complex IQ→RF (both sidebands); <c>USB</c>/<c>IQ</c> are SSB-limited (§9.5). Default
    /// "RAW".</summary>
    public string UnderlyingMode { get; init; } = "RAW";

    /// <summary>Slice frequency (MHz, six-decimal Flex form). Default "14.100000".</summary>
    public string Frequency { get; init; } = "14.100000";

    /// <summary>RX/TX antenna port. Default "ANT1".</summary>
    public string Antenna { get; init; } = "ANT1";

    /// <summary>TX filter low cut (Hz, may be negative for a symmetric wideband passband). Default
    /// −12000.</summary>
    public int TxFilterLowHz { get; init; } = -12000;

    /// <summary>TX filter high cut (Hz). Default 12000 (the ±12 kHz limit of the 24 kHz waveform
    /// rate).</summary>
    public int TxFilterHighHz { get; init; } = 12000;

    /// <summary>Optional TX RF power (0–100). Null leaves the radio's current setting.</summary>
    public int? RfPower { get; init; }

    /// <summary>How long to wait for a slice object / frequency to appear (default 5 s).</summary>
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Brings up a FlexRadio <b>waveform</b> for wideband IQ transmit over a connected
/// <see cref="FlexClient"/>, and hands back the keying + IQ-output pair. Registers a custom mode
/// (<c>waveform create/set</c>), owns a headless slice switched into that mode, and applies the
/// band-persistence tune fix — the transmit counterpart to <see cref="FlexStation"/> (which does DAX
/// audio). Proven against M0LTE's FLEX-6500 (docs/flex-integration.md §9.2/§9.5).
/// </summary>
/// <remarks>
/// The slice create/find/tune steps mirror <see cref="FlexStation"/>'s headless path (that shared
/// logic is a candidate for extraction). The waveform-specific parts — register the mode, switch the
/// slice to it, reflect TX buffers (<see cref="FlexWaveformIqOutput"/>) — are this class's own.
/// Firmware note (V1.4.0.0): <c>{rx,tx}_filter depth=</c> is rejected, so only <c>low_cut</c>/
/// <c>high_cut</c> are set.
/// </remarks>
public sealed class FlexWaveform : IAsyncDisposable
{
    private const double FrequencyToleranceMhz = 0.000002;

    private readonly FlexClient _client;
    private readonly FlexWaveformOptions _options;

    private FlexWaveform(FlexClient client, FlexWaveformOptions options)
    {
        _client = client;
        _options = options;
    }

    /// <summary>The shared session.</summary>
    public FlexClient Client => _client;

    /// <summary>The registered waveform's friendly name.</summary>
    public string WaveformName => _options.Name;

    /// <summary>The numeric slice index the waveform runs on (e.g. "0").</summary>
    public string SliceIndex { get; private set; } = "";

    /// <summary>A human-readable warning if the created slice could not be verified on the requested
    /// frequency after the tune (band persistence still on?); null when it verified. See
    /// <see cref="FlexStation.TuneWarning"/>.</summary>
    public string? TuneWarning { get; private set; }

    /// <summary>Headless bring-up (no SmartSDR): init UDP, register as a GUI client, register the
    /// waveform, create + tune a slice, switch it into the waveform mode. Leaves the radio ready to
    /// key and transmit IQ.</summary>
    public static async Task<FlexWaveform> SetUpHeadlessAsync(
        FlexClient client, FlexWaveformOptions options, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var waveform = new FlexWaveform(client, options);
        await client.InitUdpAsync(cancellation).ConfigureAwait(false);
        await client.SendCommandExpectOkAsync("client gui", cancellation).ConfigureAwait(false);

        // 1. Register the waveform mode. Filter params are one-per-command; depth= is unsupported.
        await client.SendCommandExpectOkAsync(
            $"waveform create name={options.Name} mode={options.Mode} underlying_mode={options.UnderlyingMode}",
            cancellation).ConfigureAwait(false);
        await client.SendCommandExpectOkAsync($"waveform set {options.Name} tx=1", cancellation).ConfigureAwait(false);
        await client.SendCommandExpectOkAsync(
            $"waveform set {options.Name} tx_filter low_cut={options.TxFilterLowHz}", cancellation).ConfigureAwait(false);
        await client.SendCommandExpectOkAsync(
            $"waveform set {options.Name} tx_filter high_cut={options.TxFilterHighHz}", cancellation).ConfigureAwait(false);
        await client.SendCommandExpectOkAsync(
            $"waveform set {options.Name} udpport={client.LocalUdpPort}", cancellation).ConfigureAwait(false);

        // 2. Own a slice, tune it (band-persistence fix), then switch it into the waveform mode.
        await waveform.CreateSliceAsync(cancellation).ConfigureAwait(false);
        await waveform.EnsureTunedAsync(cancellation).ConfigureAwait(false);
        await client.SendCommandExpectOkAsync(
            $"slice set {waveform.SliceIndex} mode={options.Mode}", cancellation).ConfigureAwait(false);

        if (options.RfPower is int power)
        {
            await client.SendCommandExpectOkAsync($"transmit set rfpower={power}", cancellation).ConfigureAwait(false);
        }

        return waveform;
    }

    /// <summary>Creates the slice PTT (keying).</summary>
    public FlexPtt CreatePtt(bool confirmInterlock = false) => new(_client, SliceIndex, confirmInterlock);

    /// <summary>Creates the wideband IQ transmit sink.</summary>
    public FlexWaveformIqOutput CreateIqOutput(double bufferSeconds = 3.0) => new(_client, bufferSeconds);

    private async Task CreateSliceAsync(CancellationToken cancellation)
    {
        _client.SendCommandNoWait("sub slice all");
        await _client.SendCommandExpectOkAsync(
            $"slice create freq={_options.Frequency} ant={_options.Antenna} mode=DIGU rxant={_options.Antenna}",
            cancellation).ConfigureAwait(false);

        string sliceObject = await WaitForObjectAsync(
            "slice ",
            state => HandleMatches(state.GetValueOrDefault("client_handle", ""), _client.Handle),
            "our created slice", _options.SetupTimeout, cancellation).ConfigureAwait(false);
        SliceIndex = sliceObject["slice ".Length..];
    }

    // Band-persistence tune fix, mirroring FlexStation.EnsureTunedAsync (docs/flex-integration.md §8):
    // disable persistence, activate, explicit `slice t`, then verify RF_frequency converged.
    private async Task EnsureTunedAsync(CancellationToken cancellation)
    {
        if (!double.TryParse(_options.Frequency, NumberStyles.Float, CultureInfo.InvariantCulture, out double wantMhz))
        {
            TuneWarning = $"headless tune skipped: '{_options.Frequency}' is not a numeric MHz value";
            return;
        }

        await TryBestEffortAsync("radio set band_persistence_enabled=0", cancellation).ConfigureAwait(false);
        await TryBestEffortAsync($"slice set {SliceIndex} active=1", cancellation).ConfigureAwait(false);

        string tuneFreq = wantMhz.ToString("F6", CultureInfo.InvariantCulture);
        await _client.SendCommandExpectOkAsync($"slice t {SliceIndex} {tuneFreq}", cancellation).ConfigureAwait(false);

        string? got = await WaitForSliceFrequencyAsync(wantMhz, _options.SetupTimeout, cancellation).ConfigureAwait(false);
        if (got is null)
        {
            TuneWarning = $"headless tune unverified: slice {SliceIndex} reported no RF_frequency after 'slice t {tuneFreq}'";
        }
        else if (!double.TryParse(got, NumberStyles.Float, CultureInfo.InvariantCulture, out double gotMhz)
            || Math.Abs(gotMhz - wantMhz) > FrequencyToleranceMhz)
        {
            TuneWarning = $"headless tune mismatch: slice {SliceIndex} is on {got} MHz, requested {tuneFreq} MHz";
        }

        if (TuneWarning is not null)
        {
            Debug.WriteLine($"flex: {TuneWarning}");
        }
    }

    private async Task TryBestEffortAsync(string command, CancellationToken cancellation)
    {
        try
        {
            FlexResult result = await _client.SendCommandAsync(command, cancellation).ConfigureAwait(false);
            if (!result.IsOk)
            {
                Debug.WriteLine($"flex: best-effort '{command}' returned 0x{result.Error:X8}; continuing");
            }
        }
        catch (FlexProtocolException ex)
        {
            Debug.WriteLine($"flex: best-effort '{command}' faulted ({ex.Message}); continuing");
        }
    }

    private async Task<string?> WaitForSliceFrequencyAsync(double wantMhz, TimeSpan timeout, CancellationToken cancellation)
    {
        string sliceObject = "slice " + SliceIndex;
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        string? last = null;
        while (true)
        {
            if (_client.TryGetObject(sliceObject, out IReadOnlyDictionary<string, string> state)
                && state.TryGetValue("RF_frequency", out string? rf))
            {
                last = rf;
                if (double.TryParse(rf, NumberStyles.Float, CultureInfo.InvariantCulture, out double gotMhz)
                    && Math.Abs(gotMhz - wantMhz) <= FrequencyToleranceMhz)
                {
                    return rf;
                }
            }

            if (Environment.TickCount64 > deadline)
            {
                return last;
            }

            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }
    }

    private async Task<string> WaitForObjectAsync(
        string prefix, Func<IReadOnlyDictionary<string, string>, bool> predicate, string what,
        TimeSpan timeout, CancellationToken cancellation)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            if (_client.TryFindObject(prefix, predicate, out string objectName))
            {
                return objectName;
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new FlexProtocolException($"timed out waiting for {what}");
            }

            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }
    }

    private static bool HandleMatches(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        return NormalizeHandle(a).Equals(NormalizeHandle(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHandle(string handle) =>
        handle.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? handle[2..] : handle;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Best-effort teardown: unkey, remove the waveform + slice. The client may already be gone.
        try
        {
            await _client.SendCommandAsync("xmit 0", CancellationToken.None).ConfigureAwait(false);
            await _client.SendCommandAsync($"waveform remove {_options.Name}", CancellationToken.None).ConfigureAwait(false);
            if (SliceIndex.Length > 0)
            {
                await _client.SendCommandAsync($"slice remove {SliceIndex}", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // teardown is best-effort
        }

        await _client.DisposeAsync().ConfigureAwait(false);
    }
}

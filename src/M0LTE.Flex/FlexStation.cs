using System.Diagnostics;
using System.Globalization;

namespace M0LTE.Flex;

/// <summary>Which bring-up path <see cref="FlexStation"/> uses.</summary>
public enum FlexSetupMode
{
    /// <summary>The daemon owns the radio: register as a GUI client, create our own slice,
    /// then enable DAX. Needs no SmartSDR running (the "pdn at the radio" deployment — a Pi
    /// at the rig, no Windows). This is the default for <c>--device flex:</c>.</summary>
    Headless,

    /// <summary>Coexist with a running SmartSDR: attach to a slice the operator has already
    /// configured (found by station name + slice letter), then enable DAX. Opt in with a
    /// <c>@station</c> suffix on the device string.</summary>
    Attach,
}

/// <summary>Options for setting up a <see cref="FlexStation"/>.</summary>
/// <remarks>
/// Two paths share these options (docs/flex-integration.md §4/§8):
/// <list type="bullet">
/// <item><b>Headless</b> (default) creates its own slice — <see cref="Frequency"/>,
/// <see cref="Antenna"/> and <see cref="SliceMode"/> configure it. <see cref="Station"/> is
/// unused.</item>
/// <item><b>Attach</b> binds to an existing SmartSDR slice — <see cref="Station"/> and
/// <see cref="SliceLetter"/> select it; the slice-creation params are unused.</item>
/// </list>
/// </remarks>
public sealed record FlexStationOptions
{
    /// <summary>Attach mode only: the SmartSDR station name whose slice we bind DAX to
    /// (nDAX default "Flex"). Ignored in headless mode.</summary>
    public string Station { get; init; } = "Flex";

    /// <summary>The slice letter (headless: the letter to expect on the created slice;
    /// attach: the letter to find). Default "A".</summary>
    public string SliceLetter { get; init; } = "A";

    /// <summary>The DAX channel number to claim (default "1").</summary>
    public string DaxChannel { get; init; } = "1";

    /// <summary>RX audio gain 0–100 (default 50).</summary>
    public int Gain { get; init; } = 50;

    /// <summary>Enable keepalive + ping so a wedged radio is detected (default true).</summary>
    public bool Keepalive { get; init; } = true;

    /// <summary>How long to wait for a client/slice object to appear (default 5 s).</summary>
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Headless mode only: the frequency (MHz, six-decimal Flex form) for the slice
    /// we create (default "14.100000"). Ignored in attach mode.</summary>
    public string Frequency { get; init; } = "14.100000";

    /// <summary>Headless mode only: the antenna (RX and TX) for the slice we create
    /// (default "ANT1"). Ignored in attach mode.</summary>
    public string Antenna { get; init; } = "ANT1";

    /// <summary>Headless mode only: the demod mode for the slice we create (default "DIGU" —
    /// a data mode; the radio may report it back as "USB", which is equivalent for the DAX
    /// data path). Ignored in attach mode.</summary>
    public string SliceMode { get; init; } = "DIGU";
}

/// <summary>
/// Drives the DAX enable sequence on a connected <see cref="FlexClient"/> and hands back
/// the RX/TX/PTT triplet, all sharing that one session. Two bring-up paths
/// (docs/flex-integration.md §4/§8):
/// <list type="bullet">
/// <item><see cref="SetUpHeadlessAsync"/> — no SmartSDR: register as a GUI client, create our
/// own slice, then enable DAX. The default for <c>--device flex:</c>.</item>
/// <item><see cref="SetUpAsync"/> — attach to a slice a running SmartSDR already owns (found
/// by station name + slice letter), then enable DAX.</item>
/// </list>
/// </summary>
/// <remarks>
/// The attach path (bind → find-slice → enable-DAX) is a port of nDAX <c>bindClient</c>,
/// <c>findSlice</c> and <c>enableDax</c> (© Andrew Rodland KC2G, MIT). The <b>headless</b>
/// path (GUI-register + create-slice, tolerate the redundant self-bind) is pdn's own — nDAX
/// is attach-only — proven against M0LTE's FLEX-6500 (docs/flex-integration.md §8). The
/// eight-step DAX enable (<see cref="EnableDaxAsync"/>) is shared, unchanged, between them.
/// </remarks>
public sealed class FlexStation : IAsyncDisposable
{
    private readonly FlexClient _client;
    private readonly DaxStreamFormat _format;

    private FlexStation(FlexClient client, DaxStreamFormat format)
    {
        _client = client;
        _format = format;
    }

    /// <summary>The shared session.</summary>
    public FlexClient Client => _client;

    /// <summary>The DAX transport in use.</summary>
    public DaxStreamFormat Format => _format;

    /// <summary>The numeric slice index that was attached (e.g. "0").</summary>
    public string SliceIndex { get; private set; } = "";

    /// <summary>The DAX-RX stream id.</summary>
    public uint RxStreamId { get; private set; }

    /// <summary>The DAX-TX stream id.</summary>
    public uint TxStreamId { get; private set; }

    /// <summary>The result of the best-effort headless <c>client bind</c>, if attempted. On a
    /// headless radio we are already the owning GUI client, so the explicit re-bind is
    /// redundant and the radio rejects it (error 0x5000003E) — DAX works regardless, so this
    /// is surfaced for observability but never fails setup. Null in attach mode.</summary>
    public FlexResult? HeadlessBindResult { get; private set; }

    /// <summary>Headless mode only: a human-readable warning when the created slice could not be
    /// verified on the requested frequency after the explicit tune (<see cref="EnsureTunedAsync"/>).
    /// A real 6500 with <c>band_persistence_enabled=1</c> (the default) ignores <c>slice create</c>'s
    /// <c>freq</c> and snaps the slice to the last-used band; the headless path disables persistence
    /// and re-tunes with <c>slice t</c>, then re-reads <c>RF_frequency</c>. If it still doesn't match
    /// <see cref="FlexStationOptions.Frequency"/> within tolerance, that mismatch is surfaced here
    /// (setup does NOT throw — the <c>slice t</c> command itself returned err=0). Null when the tune
    /// verified OK, and null in attach mode (SmartSDR owns tuning there).</summary>
    public string? TuneWarning { get; private set; }

    /// <summary>Attach path: connects the session (init UDP), binds to the running SmartSDR
    /// station's client, finds its slice by letter, and runs the DAX enable sequence. Use
    /// this only to coexist with a running SmartSDR; the default deployment is
    /// <see cref="SetUpHeadlessAsync"/>.</summary>
    public static async Task<FlexStation> SetUpAsync(
        FlexClient client, DaxStreamFormat format, FlexStationOptions options,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(options);

        var station = new FlexStation(client, format);
        await client.InitUdpAsync(cancellation).ConfigureAwait(false);

        string clientHandle = await station.BindClientAsync(options, cancellation).ConfigureAwait(false);
        await station.FindSliceAsync(options, clientHandle, cancellation).ConfigureAwait(false);
        await station.EnableDaxAsync(options, cancellation).ConfigureAwait(false);

        await station.FinishAsync(options, cancellation).ConfigureAwait(false);
        return station;
    }

    /// <summary>Headless path: connects the session (init UDP), registers as a GUI client,
    /// creates our own slice (from <see cref="FlexStationOptions.Frequency"/>/
    /// <see cref="FlexStationOptions.Antenna"/>/<see cref="FlexStationOptions.SliceMode"/>),
    /// finds it by our client handle, best-effort re-binds (tolerating the redundant-bind
    /// error), and runs the DAX enable sequence. Needs no SmartSDR running — the default for
    /// <c>--device flex:</c>. Proven against a real FLEX-6500 (docs/flex-integration.md §8).</summary>
    public static async Task<FlexStation> SetUpHeadlessAsync(
        FlexClient client, DaxStreamFormat format, FlexStationOptions options,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(options);

        var station = new FlexStation(client, format);
        await client.InitUdpAsync(cancellation).ConfigureAwait(false);

        // 1. Register as a GUI client — this makes us able to own a slice and returns our
        //    client_id (uuid) in the result message.
        FlexResult gui = await client.SendCommandExpectOkAsync("client gui", cancellation)
            .ConfigureAwait(false);
        string clientId = gui.Message.Trim();

        // 2. Create our own slice and find it by our client handle (we own it, so its
        //    client_handle == this session's handle — not a station name).
        await station.CreateSliceAsync(options, cancellation).ConfigureAwait(false);

        // 3. Force the slice onto the requested frequency. band_persistence (default-on on a real
        //    6500) makes `slice create freq=…` snap the slice back to the last-used band, so the
        //    create alone leaves us on the wrong QRG — DAX then streams silence / the wrong band.
        //    Proven live on M0LTE's 6500 (docs/flex-integration.md §8). Attach mode skips this
        //    (SmartSDR owns tuning).
        await station.EnsureTunedAsync(options, cancellation).ConfigureAwait(false);

        // 4. Best-effort re-bind. We are already the owning GUI client, so the radio rejects
        //    this (0x5000003E) and DAX works regardless — never fail setup on it.
        await station.TryBindSelfAsync(clientId, cancellation).ConfigureAwait(false);

        // 5. The eight-step DAX enable, shared unchanged with the attach path.
        await station.EnableDaxAsync(options, cancellation).ConfigureAwait(false);

        await station.FinishAsync(options, cancellation).ConfigureAwait(false);
        return station;
    }

    /// <summary>Creates the DAX-RX audio source.</summary>
    public FlexAudioInput CreateAudioInput(int packetBuffer = 3) =>
        new(_client, RxStreamId, _format, packetBuffer);

    /// <summary>Creates the DAX-TX audio sink.</summary>
    public FlexAudioOutput CreateAudioOutput(bool paceRealTime = true) =>
        new(_client, TxStreamId, _format, paceRealTime);

    /// <summary>Creates the slice PTT.</summary>
    public FlexPtt CreatePtt(bool confirmInterlock = false) =>
        new(_client, SliceIndex, confirmInterlock);

    private async Task FinishAsync(FlexStationOptions options, CancellationToken cancellation)
    {
        if (options.Keepalive)
        {
            await _client.EnableKeepaliveAsync(TimeSpan.FromSeconds(1), cancellation).ConfigureAwait(false);
        }
    }

    private async Task<string> BindClientAsync(FlexStationOptions options, CancellationToken cancellation)
    {
        _client.SendCommandNoWait("sub client all");
        string clientObject = await WaitForObjectAsync(
            "client ",
            state => state.GetValueOrDefault("station") == options.Station
                && state.ContainsKey("client_id"),
            $"client for station '{options.Station}'",
            options.SetupTimeout,
            cancellation).ConfigureAwait(false);

        _client.TryGetObject(clientObject, out IReadOnlyDictionary<string, string> client);
        string clientId = client["client_id"];
        string clientHandle = clientObject["client ".Length..];

        await _client.SendCommandExpectOkAsync($"client bind client_id={clientId}", cancellation)
            .ConfigureAwait(false);
        return clientHandle;
    }

    private async Task FindSliceAsync(
        FlexStationOptions options, string clientHandle, CancellationToken cancellation)
    {
        _client.SendCommandNoWait("sub slice all");
        string sliceObject = await WaitForObjectAsync(
            "slice ",
            state => state.GetValueOrDefault("index_letter") == options.SliceLetter
                && (!state.TryGetValue("client_handle", out string? handle) || handle == clientHandle),
            $"slice '{options.SliceLetter}'",
            options.SetupTimeout,
            cancellation).ConfigureAwait(false);
        SliceIndex = sliceObject["slice ".Length..];
    }

    private async Task CreateSliceAsync(FlexStationOptions options, CancellationToken cancellation)
    {
        // Subscribe so the created slice's status object (carrying our client_handle) reaches
        // us, then create the slice on the working frequency/antenna/mode.
        _client.SendCommandNoWait("sub slice all");
        await _client.SendCommandExpectOkAsync(
            $"slice create freq={options.Frequency} ant={options.Antenna} "
            + $"mode={options.SliceMode} rxant={options.Antenna}",
            cancellation).ConfigureAwait(false);

        // Find OUR slice by client_handle == this session's handle (we created it, so we own
        // it). Handles are compared with any "0x" prefix normalised away — the prologue H
        // line has none, slice status carries the "0x" form.
        string sliceObject = await WaitForObjectAsync(
            "slice ",
            state => HandleMatches(state.GetValueOrDefault("client_handle", ""), _client.Handle),
            "our created slice",
            options.SetupTimeout,
            cancellation).ConfigureAwait(false);
        SliceIndex = sliceObject["slice ".Length..];
    }

    /// <summary>~2 Hz. A slice tunes in 1 Hz steps, so the reported <c>RF_frequency</c> should
    /// equal the requested MHz to the six-decimal place; allow a couple of Hz for rounding.</summary>
    private const double FrequencyToleranceMhz = 0.000002;

    private async Task EnsureTunedAsync(FlexStationOptions options, CancellationToken cancellation)
    {
        // The requested frequency is a six-decimal MHz string; parse it so we can format the
        // `slice t` argument (%.6f, the flexclient SliceTune form) and verify convergence.
        if (!double.TryParse(
                options.Frequency, NumberStyles.Float, CultureInfo.InvariantCulture, out double wantMhz))
        {
            // Not a numeric frequency — we can neither tune nor verify. Leave the created slice
            // as-is rather than send a malformed `slice t`.
            TuneWarning = $"headless tune skipped: '{options.Frequency}' is not a numeric MHz value";
            return;
        }

        // 1. Disable band persistence — the cause. Best-effort: some firmwares may not expose the
        //    setting, and DAX still works if it's already off, so never fail setup on its result.
        await TryBestEffortAsync("radio set band_persistence_enabled=0", cancellation).ConfigureAwait(false);

        // 2. Activate the slice so the tune takes. Best-effort for the same reason.
        await TryBestEffortAsync($"slice set {SliceIndex} active=1", cancellation).ConfigureAwait(false);

        // 3. The explicit tune — this is the actual fix; the radio returns err=0. Format %.6f so
        //    the argument matches the six-decimal MHz form the radio expects.
        string tuneFreq = wantMhz.ToString("F6", CultureInfo.InvariantCulture);
        await _client.SendCommandExpectOkAsync($"slice t {SliceIndex} {tuneFreq}", cancellation)
            .ConfigureAwait(false);

        // 4. Verify. `slice t` re-emits the slice's RF_frequency status; poll (bounded — never
        //    hang) until it converges on the request. If it never does, surface a warning rather
        //    than throw: the tune command succeeded, so a mismatch is an observability signal
        //    (band persistence still on? unexpected firmware?), not a fatal setup error.
        string? got = await WaitForSliceFrequencyAsync(
            wantMhz, options.SetupTimeout, cancellation).ConfigureAwait(false);
        if (got is null)
        {
            TuneWarning =
                $"headless tune unverified: slice {SliceIndex} reported no RF_frequency after 'slice t {tuneFreq}'";
        }
        else if (!double.TryParse(got, NumberStyles.Float, CultureInfo.InvariantCulture, out double gotMhz)
            || Math.Abs(gotMhz - wantMhz) > FrequencyToleranceMhz)
        {
            TuneWarning =
                $"headless tune mismatch: slice {SliceIndex} is on {got} MHz, requested {tuneFreq} MHz "
                + "(band persistence still enabled?)";
        }

        if (TuneWarning is not null)
        {
            Debug.WriteLine($"flex: {TuneWarning}");
        }
    }

    /// <summary>Sends a command and swallows any non-fatal failure (a non-zero radio error or a
    /// protocol fault) — for the best-effort steps that must never fail headless setup.
    /// Cancellation propagates.</summary>
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

    /// <summary>Polls the created slice's <c>RF_frequency</c> until it lands within tolerance of
    /// <paramref name="wantMhz"/> or the timeout elapses. Returns the last <c>RF_frequency</c>
    /// seen (the converged value on success; a mismatching value, or null if the slice never
    /// reported one, on timeout). Never throws on timeout — the caller decides how to surface a
    /// miss.</summary>
    private async Task<string?> WaitForSliceFrequencyAsync(
        double wantMhz, TimeSpan timeout, CancellationToken cancellation)
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

    private async Task TryBindSelfAsync(string clientId, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return;
        }

        FlexResult bind = await _client.SendCommandAsync($"client bind client_id={clientId}", cancellation)
            .ConfigureAwait(false);
        HeadlessBindResult = bind;
        if (!bind.IsOk)
        {
            // Expected: we are already the owning GUI client, so the explicit re-bind is
            // redundant. DAX works regardless — swallow it (debug-log only, never throw).
            Debug.WriteLine(
                $"flex: headless client bind redundant (0x{bind.Error:X8}); continuing — already GUI owner");
        }
    }

    private async Task EnableDaxAsync(FlexStationOptions options, CancellationToken cancellation)
    {
        if (_format.IsReducedBandwidth)
        {
            await _client.SendCommandExpectOkAsync("client set send_reduced_bw_dax=true", cancellation)
                .ConfigureAwait(false);
        }

        await _client.SendCommandExpectOkAsync(
            $"slice set {SliceIndex} dax={options.DaxChannel}", cancellation).ConfigureAwait(false);
        await _client.SendCommandExpectOkAsync(
            $"dax audio set {options.DaxChannel} slice={SliceIndex} tx=1", cancellation).ConfigureAwait(false);

        FlexResult rx = await _client.SendCommandExpectOkAsync(
            $"stream create type=dax_rx dax_channel={options.DaxChannel}", cancellation).ConfigureAwait(false);
        RxStreamId = ParseStreamId(rx.Message);

        await _client.SendCommandExpectOkAsync(
            $"audio stream 0x{RxStreamId:X8} slice {SliceIndex} gain {options.Gain}", cancellation)
            .ConfigureAwait(false);

        FlexResult tx = await _client.SendCommandExpectOkAsync("stream create type=dax_tx", cancellation)
            .ConfigureAwait(false);
        TxStreamId = ParseStreamId(tx.Message);
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

    /// <summary>Compares two Flex handles, normalising away any "0x" prefix (the prologue H
    /// line carries none; status objects reference handles in the "0x…" form).</summary>
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

    private static uint ParseStreamId(string message)
    {
        string hex = message.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
        {
            throw new FlexProtocolException($"expected a stream id, got '{message}'");
        }

        return id;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

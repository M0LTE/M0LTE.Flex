using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;

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

    /// <summary>
    /// Point the transmitter at DAX as its audio source (<c>transmit set dax=1</c>). Default true.
    /// </summary>
    /// <remarks>
    /// <para><b>Without this, DAX transmit puts nothing on air.</b> Creating the DAX streams and
    /// pushing packets into them is not enough: the transmitter has its own audio-source selection,
    /// which defaults to the mic input, and every command in the DAX enable returns <c>err=0</c>
    /// whether or not it is set. Measured on a FLEX-6500 (fw 4.1.5, 2026-07-26) — a 1 kHz tone
    /// produced no modulation whatsoever until <c>transmit set dax=1</c> was sent.</para>
    /// <para>It is a <b>global</b> radio setting: it persists after teardown and affects what the
    /// radio transmits from thereafter. Set false to leave the radio's own selection alone — for a
    /// receive-only session, or where something else owns the transmitter.</para>
    /// </remarks>
    public bool SelectDaxAsTransmitSource { get; init; } = true;

    /// <summary>
    /// High cut of the radio's transmit filter (Hz), applied with <c>transmit set filter_high=</c>.
    /// Null (the default) leaves the radio's own setting alone.
    /// </summary>
    /// <remarks>
    /// <para><b>This, not the slice, is what limits transmitted DAX audio bandwidth.</b> Measured on
    /// a FLEX-6500 (fw 4.1.5, 2026-07-26): an audio sweep transmitted through DAX was cut at exactly
    /// 10 kHz with the filter at 10000, and at exactly 3 kHz with it at 3000. DAX audio is therefore
    /// not the ~3 kHz path it is often assumed to be — it carries whatever this filter allows, up to
    /// the radio's 10 kHz clamp, the same ceiling the waveform path has.</para>
    /// <para>It is a <b>global</b> radio setting that persists after teardown, which is why the
    /// default is to leave it alone: a station that quietly widened it would change what every other
    /// client transmits. <see cref="FlexStation.TransmitFilter"/> reports what is actually in force,
    /// so a stale value is visible rather than silently shaping your signal.</para>
    /// </remarks>
    public int? TransmitFilterHighHz { get; init; }

    /// <summary>
    /// Low and high cut of the <b>slice's receive</b> filter (Hz), applied with
    /// <c>slice set &lt;n&gt; filter_lo= filter_hi=</c>. Null (the default) leaves the slice's own
    /// setting alone; either may be set without the other. Headless only — in attach mode the
    /// slice belongs to SmartSDR.
    /// </summary>
    /// <remarks>
    /// <para>The receive counterpart of <see cref="TransmitFilterHighHz"/>, and the other half of
    /// making DAX carry more than ~3 kHz: transmitted bandwidth is capped by the global transmit
    /// filter, but what reaches DAX-RX is capped here, per slice. A slice left on an ordinary
    /// 3 kHz data filter hands you nothing above 3 kHz however wide the signal is.</para>
    /// <para>Unlike the transmit filter this is <b>slice</b> state, so it goes away with the slice
    /// rather than persisting on the radio — a headless station that creates its own slice is
    /// setting its own filter, not changing what other clients hear.</para>
    /// <para><b>The radio's ceiling here is not measured.</b> The transmit filter clamps at 10 kHz;
    /// whether a slice's receive filter clamps at the same width, somewhere else, or not at all is
    /// unknown, so nothing is assumed: the value is read back and
    /// <see cref="FlexStation.ReceiveFilterWarning"/> says so if the radio did not land where it
    /// was asked to.</para>
    /// </remarks>
    public int? ReceiveFilterLowHz { get; init; }

    /// <inheritdoc cref="ReceiveFilterLowHz" />
    public int? ReceiveFilterHighHz { get; init; }

    /// <summary>
    /// Transmit RF power, 0–100, applied with <c>transmit set rfpower=</c>. Null (the default)
    /// leaves the radio's own setting alone.
    /// </summary>
    /// <remarks>
    /// <para>On the 6000 series the PA is 100 W (<c>slice N max_internal_pa_power</c>), so the
    /// number is very close to watts.</para>
    /// <para><b>Only a station client with a slice can set this.</b> Measured on a FLEX-6500
    /// (fw 4.2.20, 2026-08-02): an unbound command client's <c>transmit set rfpower=N</c> returns
    /// <c>err=0</c> and is silently discarded — the value never moves — because RF power is held
    /// per station, and a client with no station context has nowhere to put it. Registering as a
    /// GUI client makes <c>transmit rfpower</c> report that station's own value. The headless
    /// path here registers as a GUI client and creates a slice, so it is one of the few things
    /// that <i>can</i> set it.</para>
    /// <para>The radio rejects a value above <c>transmit max_power_level</c> outright
    /// (<c>0x50000048</c>) rather than clamping or rescaling — so asking for more than the
    /// operator's ceiling gets an error, not a quiet reduction. <see cref="FlexStation.RfPowerApplied"/>
    /// reports what actually took effect.</para>
    /// <para>It is a <b>global</b> radio setting in the same sense as the transmit filter: it
    /// persists after teardown and affects what this station transmits thereafter.</para>
    /// </remarks>
    public int? RfPower { get; init; }

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

    /// <summary>True once <see cref="CreateSliceAsync"/> has run — i.e. this is the headless path
    /// and the slice at <see cref="SliceIndex"/> is one WE created and therefore own. The attach
    /// path binds to a slice SmartSDR already owns and never sets this, so teardown knows not to
    /// remove it.</summary>
    private bool _createdSlice;

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
        await station.ApplyRfPowerAsync(options, cancellation).ConfigureAwait(false);

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

        // 6. Point the transmitter at DAX. Without this the streams exist, the packets flow and the
        //    radio keys — with nothing modulated onto it, because the transmitter is still
        //    listening to the mic.
        await station.SelectDaxTransmitSourceAsync(options, cancellation).ConfigureAwait(false);

        // 7. Read back (and optionally set) the filter that governs transmitted audio bandwidth.
        await station.ApplyTransmitFilterAsync(options, cancellation).ConfigureAwait(false);

        // 7b. The same question on the receive side, which is per-slice rather than global: a
        //     slice on an ordinary 3 kHz data filter delivers nothing wider to DAX-RX, whatever
        //     the transmit filter allows out.
        await station.ApplyReceiveFilterAsync(options, cancellation).ConfigureAwait(false);

        // 8. Transmit power. After the slice exists, because the radio holds power per station
        //    and there is nothing to hold it against until then.
        await station.ApplyRfPowerAsync(options, cancellation).ConfigureAwait(false);

        await station.FinishAsync(options, cancellation).ConfigureAwait(false);
        return station;
    }

    /// <summary>Whether the radio reports DAX as the transmitter's audio source after setup. Null if
    /// it never reported, or if <see cref="FlexStationOptions.SelectDaxAsTransmitSource"/> was
    /// false.</summary>
    public bool? TransmitSourceIsDax { get; private set; }

    /// <summary>A human-readable warning if the transmitter could not be pointed at DAX, else null.
    /// Setup does not throw: receive still works, and the caller may only want receive.</summary>
    public string? TransmitSourceWarning { get; private set; }

    /// <summary>
    /// Selects DAX as the transmitter's audio source and reads back whether it took.
    /// </summary>
    /// <remarks>
    /// The read-back is the point. This is the step whose absence produced a fully successful-looking
    /// transmission with no modulation on it, so confirming the radio agrees is worth a round trip.
    /// </remarks>
    private async Task SelectDaxTransmitSourceAsync(FlexStationOptions options, CancellationToken cancellation)
    {
        if (!options.SelectDaxAsTransmitSource)
        {
            return;
        }

        // The transmit object is only readable once subscribed.
        await TryBestEffortAsync("sub tx all", cancellation).ConfigureAwait(false);
        await TryBestEffortAsync("transmit set dax=1", cancellation).ConfigureAwait(false);

        bool? applied = await WaitForTransmitDaxAsync(options.SetupTimeout, cancellation).ConfigureAwait(false);
        TransmitSourceIsDax = applied;

        if (applied != true)
        {
            TransmitSourceWarning =
                "the transmitter did not report DAX as its audio source (transmit dax="
                + (applied is null ? "unreported" : "0")
                + "); transmitted audio will be silent even though DAX packets are being sent";
            Debug.WriteLine($"flex: {TransmitSourceWarning}");
        }
    }

    private async Task<bool?> WaitForTransmitDaxAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        bool? last = null;
        while (true)
        {
            if (_client.TryFindObject("transmit", _ => true, out string name)
                && _client.TryGetObject(name, out IReadOnlyDictionary<string, string> transmit)
                && transmit.TryGetValue("dax", out string? dax))
            {
                last = dax is "1" or "true" or "True";
                if (last == true)
                {
                    return true;
                }
            }

            if (Environment.TickCount64 > deadline)
            {
                return last;
            }

            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>The transmit passband the radio reports, as (low, high) in Hz — what actually limits
    /// transmitted audio bandwidth. Null if the radio never reported it.</summary>
    public (int Low, int High)? TransmitFilter { get; private set; }

    /// <summary>The slice's receive passband as the radio reports it, (low, high) in Hz — what limits
    /// the bandwidth reaching DAX-RX. Null if the radio never reported it.</summary>
    public (int Low, int High)? ReceiveFilter { get; private set; }

    /// <summary>A human-readable warning if the slice's receive filter did not end up where
    /// <see cref="FlexStationOptions.ReceiveFilterLowHz"/>/<see cref="FlexStationOptions.ReceiveFilterHighHz"/>
    /// asked, else null. Setup does not throw over it: a narrower passband than asked for still
    /// receives, just less of the band.</summary>
    /// <remarks>
    /// This exists because the radio's limit on receive-filter width is not measured, unlike the
    /// transmit filter's 10 kHz clamp. Rather than encode a guess, ask and report what came back.
    /// </remarks>
    public string? ReceiveFilterWarning { get; private set; }

    /// <summary>The transmit RF power (0–100) the radio reports for this station after setup, whether
    /// or not <see cref="FlexStationOptions.RfPower"/> asked for one. Null if it never reported.</summary>
    public int? RfPowerApplied { get; private set; }

    /// <summary>The operator's power ceiling the radio reports (<c>max_power_level</c>). A request
    /// above this is rejected by the radio, not clamped. Null if it never reported.</summary>
    public int? MaxPowerLevel { get; private set; }

    /// <summary>
    /// Sets transmit RF power if asked to, then reads back what is actually in force.
    /// </summary>
    /// <remarks>
    /// Throws rather than shrugging when the radio rejects the value: a station that asked for 30 W,
    /// was refused, and then transmitted at whatever was already set is worse than one that will not
    /// start. The read-back happens either way, so an unset power is still reported rather than
    /// inherited invisibly from whatever last touched the radio.
    /// </remarks>
    private async Task ApplyRfPowerAsync(FlexStationOptions options, CancellationToken cancellation)
    {
        await TryBestEffortAsync("sub tx all", cancellation).ConfigureAwait(false);
        ReadPowerStatus();

        if (options.RfPower is not int want)
        {
            return;
        }

        if (MaxPowerLevel is int ceiling && want > ceiling)
        {
            throw new FlexProtocolException(
                $"transmit power {want} is above this radio's Max Power Level of {ceiling}. "
                + "The radio rejects a power above that ceiling rather than reducing it, so raise "
                + "the limit at the rig (Settings → Transmit → Max Power Level) or ask for less.");
        }

        await _client.SendCommandExpectOkAsync($"transmit set rfpower={want}", cancellation)
            .ConfigureAwait(false);

        long deadline = Environment.TickCount64 + (long)options.SetupTimeout.TotalMilliseconds;
        while (Environment.TickCount64 <= deadline)
        {
            ReadPowerStatus();
            if (RfPowerApplied == want)
            {
                return;
            }

            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }

        // The command is accepted and discarded for a client the radio does not consider a station
        // — measured, and impossible to tell from success without reading the value back.
        throw new FlexProtocolException(
            $"asked for transmit power {want} but the radio still reports "
            + $"{(RfPowerApplied is int got ? got.ToString(CultureInfo.InvariantCulture) : "nothing")}. "
            + "RF power is held per station; a client the radio does not treat as one has its "
            + "request accepted and discarded.");
    }

    private void ReadPowerStatus()
    {
        if (!_client.TryFindObject("transmit", _ => true, out string name)
            || !_client.TryGetObject(name, out IReadOnlyDictionary<string, string> transmit))
        {
            return;
        }

        if (transmit.TryGetValue("rfpower", out string? power)
            && int.TryParse(power, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rf))
        {
            RfPowerApplied = rf;
        }

        if (transmit.TryGetValue("max_power_level", out string? max)
            && int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ceiling))
        {
            MaxPowerLevel = ceiling;
        }
    }

    /// <summary>
    /// Sets the transmit filter if asked to, then reads back whatever is in force.
    /// </summary>
    /// <remarks>
    /// The read-back happens either way. Leaving the filter alone is the right default, but it means
    /// the transmitted bandwidth is inherited from whatever last touched the radio — so reporting it
    /// is what stops a stale value silently shaping the signal.
    /// </remarks>
    private async Task ApplyTransmitFilterAsync(FlexStationOptions options, CancellationToken cancellation)
    {
        await TryBestEffortAsync("sub tx all", cancellation).ConfigureAwait(false);

        if (options.TransmitFilterHighHz is int high)
        {
            await TryBestEffortAsync($"transmit set filter_high={high}", cancellation).ConfigureAwait(false);
        }

        long deadline = Environment.TickCount64 + (long)options.SetupTimeout.TotalMilliseconds;
        while (true)
        {
            if (_client.TryFindObject("transmit", _ => true, out string name)
                && _client.TryGetObject(name, out IReadOnlyDictionary<string, string> transmit)
                && transmit.TryGetValue("lo", out string? lo)
                && transmit.TryGetValue("hi", out string? hi)
                && int.TryParse(lo, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lowHz)
                && int.TryParse(hi, NumberStyles.Integer, CultureInfo.InvariantCulture, out int highHz))
            {
                TransmitFilter = (lowHz, highHz);
                if (options.TransmitFilterHighHz is not int want || highHz == Math.Min(want, 10000))
                {
                    return;
                }
            }

            if (Environment.TickCount64 > deadline)
            {
                return;
            }

            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets the slice's receive filter if asked to, then reads back whatever is in force.
    /// </summary>
    /// <remarks>
    /// The receive half of <see cref="ApplyTransmitFilterAsync"/>, and read back for the same
    /// reason: a slice on an ordinary 3 kHz data filter delivers nothing above 3 kHz to DAX-RX
    /// however wide the signal on the air is. Where the transmit path knows its ceiling (10 kHz,
    /// measured), this one does not, so a read-back that disagrees with the request becomes
    /// <see cref="ReceiveFilterWarning"/> rather than an exception or a silent success.
    /// </remarks>
    private async Task ApplyReceiveFilterAsync(FlexStationOptions options, CancellationToken cancellation)
    {
        if (options.ReceiveFilterLowHz is int lowWanted && options.ReceiveFilterHighHz is int highWanted
            && lowWanted >= highWanted)
        {
            ReceiveFilterWarning =
                $"receive filter low cut ({lowWanted} Hz) is not below the high cut ({highWanted} Hz); "
                + "the slice's own filter was left alone";
            return;
        }

        var parts = new List<string>(2);
        if (options.ReceiveFilterLowHz is int low)
        {
            parts.Add($"filter_lo={low}");
        }

        if (options.ReceiveFilterHighHz is int high)
        {
            parts.Add($"filter_hi={high}");
        }

        if (parts.Count > 0)
        {
            await TryBestEffortAsync(
                $"slice set {SliceIndex} {string.Join(' ', parts)}", cancellation).ConfigureAwait(false);
        }

        long deadline = Environment.TickCount64 + (long)options.SetupTimeout.TotalMilliseconds;
        while (true)
        {
            if (_client.TryFindObject($"slice {SliceIndex}", _ => true, out string name)
                && _client.TryGetObject(name, out IReadOnlyDictionary<string, string> slice)
                && slice.TryGetValue("filter_lo", out string? lo)
                && slice.TryGetValue("filter_hi", out string? hi)
                && int.TryParse(lo, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lowHz)
                && int.TryParse(hi, NumberStyles.Integer, CultureInfo.InvariantCulture, out int highHz))
            {
                ReceiveFilter = (lowHz, highHz);
                bool lowAsAsked = options.ReceiveFilterLowHz is not int wantLow || lowHz == wantLow;
                bool highAsAsked = options.ReceiveFilterHighHz is not int wantHigh || highHz == wantHigh;
                if (lowAsAsked && highAsAsked)
                {
                    return;
                }
            }

            if (Environment.TickCount64 > deadline)
            {
                break;
            }

            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }

        if (parts.Count == 0)
        {
            return;
        }

        string asked = string.Join(
            ", ",
            new[]
            {
                options.ReceiveFilterLowHz is int lw ? $"low {lw} Hz" : null,
                options.ReceiveFilterHighHz is int hw ? $"high {hw} Hz" : null,
            }.Where(part => part is not null));
        ReceiveFilterWarning = ReceiveFilter is (int gotLow, int gotHigh)
            ? $"asked the slice for a receive filter of {asked} and it reports {gotLow}-{gotHigh} Hz — "
              + "the radio would not go that wide, so anything outside what it reports is not being heard"
            : $"asked the slice for a receive filter of {asked} but it never reported one back";
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

    /// <summary>
    /// Subscribes to radio-level status, which is what carries the frequency-reference objects
    /// behind <see cref="FlexClient.Reference"/>. Without it those objects are never sent and
    /// the property reads Unknown forever — a typed surface nothing ever fills.
    /// </summary>
    private void SubscribeRadioStatus() => _client.SendCommandNoWait("sub radio all");

    private async Task FindSliceAsync(
        FlexStationOptions options, string clientHandle, CancellationToken cancellation)
    {
        SubscribeRadioStatus();
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
        SubscribeRadioStatus();
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
        _createdSlice = true;
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
    public async ValueTask DisposeAsync()
    {
        // Remove the slice WE created before dropping the client. A real FLEX-6500 does NOT
        // auto-remove a headless client's slice when that client disconnects (measured downstream,
        // 2026-07): the slice persists — tuned to our TX frequency, with a now-dead client handle —
        // and headless sessions leak one each until the radio hits its four-slice limit, after which
        // every `slice create` fails with 0x50000003 and stalls all callers. The attach path binds
        // to a slice SmartSDR already owns, so it must never remove it; _createdSlice (set only by
        // the headless CreateSliceAsync) is what tells the two apart. Best-effort and while the
        // client is still alive — we are tearing down, so swallow a dead radio/socket.
        if (_createdSlice && SliceIndex.Length > 0)
        {
            try
            {
                await _client.SendCommandAsync($"slice remove {SliceIndex}", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is FlexProtocolException or IOException or SocketException
                   or ObjectDisposedException or OperationCanceledException or TimeoutException)
            {
                Debug.WriteLine(
                    $"flex: best-effort 'slice remove {SliceIndex}' during dispose faulted "
                    + $"({ex.Message}); continuing");
            }
        }

        await _client.DisposeAsync().ConfigureAwait(false);
    }
}

using System.Diagnostics;
using System.Globalization;

namespace M0LTE.Flex;

/// <summary>
/// Options for bringing up a FlexRadio <see cref="FlexWaveform"/> (IQ TX via the Waveform API).
/// </summary>
/// <remarks>
/// Measured on M0LTE's FLEX-6500 (fw 4.1.5) on 2026-07-26: the waveform transmit path is
/// <b>single-sideband</b> — only the <b>negative</b> half of the baseband is transmitted, in every
/// <c>underlying_mode</c> — and the occupied bandwidth is capped by the radio's <b>transmit filter</b>
/// (<see cref="TransmitFilterHighHz"/>), which defaults to 3 kHz and clamps at
/// <see cref="MaxTransmitFilterHighHz"/>. Only the <b>negative</b> half of the baseband is
/// transmitted, in every mode, so place the whole signal below DC and leave the transmit filter
/// wide — otherwise the band is silently truncated or lost. Setting
/// <see cref="Band"/> hands all of that to the library.
/// </remarks>
public sealed record FlexWaveformOptions
{
    /// <summary>The waveform's friendly name (≤20 chars). Default "PdnWfm".</summary>
    public string Name { get; init; } = "PdnWfm";

    /// <summary>The 1–4 char mode designator the slice is switched to (becomes the slice
    /// <c>mode</c>). Default "PDN".</summary>
    public string Mode { get; init; } = "PDN";

    /// <summary>
    /// The underlying radio mode the waveform is layered on. Default "RAW".
    /// </summary>
    /// <remarks>
    /// <para>Every mode measured is single-sideband, and <b>only the negative half of the baseband
    /// reaches the air</b> — a positive-frequency component is transmitted by none of them. The mode
    /// chooses which side of the carrier the surviving half lands on, and hence whether the caller's
    /// spectrum arrives upright:</para>
    /// <list type="bullet">
    ///   <item><c>RAW</c>, <c>LSB</c>, <c>DIGL</c> — a component at <c>−f</c> is transmitted at
    ///   <c>slice − f</c>. Spectrum <b>upright</b>. Of these,
    ///   <see cref="Band">band placement</see> uses <c>RAW</c> only: the other two are
    ///   full audio modes carrying processing that a tone check would not reveal.</item>
    ///   <item><c>IQ</c>, <c>USB</c>, <c>DIGU</c> — a component at <c>−f</c> is transmitted at
    ///   <c>slice + f</c>. Spectrum <b>mirrored</b>, so a modulated signal goes out inverted.</item>
    ///   <item><c>AM</c>, <c>FM</c> — Q is discarded and the I channel alone is modulated.</item>
    /// </list>
    /// </remarks>
    public string UnderlyingMode { get; init; } = "RAW";

    /// <summary>
    /// Tune the slice here (MHz) and transmit the caller's samples <b>untouched</b>. Mutually
    /// exclusive with <see cref="Band"/>; exactly one must be set.
    /// </summary>
    /// <remarks>
    /// The low-level path: the caller owns where its signal lands relative to this frequency, and
    /// with it the consequences — only content below DC is transmitted, and the transmit filter
    /// truncates whatever is left. Use it to replay a capture verbatim, or to probe the radio's own
    /// behaviour. For ordinary use, <see cref="Band"/> handles all of that.
    /// </remarks>
    public double? SliceFrequencyMhz { get; init; }

    /// <summary>
    /// Place a signal of a declared width at a declared frequency. Mutually exclusive with
    /// <see cref="SliceFrequencyMhz"/>; exactly one must be set.
    /// </summary>
    /// <remarks>
    /// <para>Say where the signal goes and how wide it is, and the library derives the slice
    /// frequency, frequency-shifts the samples into the half the radio actually transmits — without
    /// mirroring them — opens the transmit filter far enough, and throws rather than putting a
    /// truncated signal on air.</para>
    /// <para>Note the slice ends up on a <i>different</i> frequency from the one named here, since
    /// the signal sits below it. Read <see cref="FlexWaveform.SliceFrequencyMhz"/> for where it
    /// actually went.</para>
    /// </remarks>
    public IqBand? Band { get; init; }

    /// <summary>Antenna port used for both receive and transmit. Default "ANT1".</summary>
    public string Antenna { get; init; } = "ANT1";

    /// <summary>The waveform's own TX filter low cut (Hz), sent as
    /// <c>waveform set &lt;name&gt; tx_filter low_cut=</c>. Default −12000.</summary>
    /// <remarks>
    /// <b>Inert on firmware 4.1.5</b>: the command returns <c>err=0</c> and has no measurable effect
    /// on the transmitted signal. <see cref="TransmitFilterHighHz"/> is the setting that actually
    /// governs occupied bandwidth. Kept because it is the documented waveform parameter and may
    /// matter on other firmware.
    /// </remarks>
    public int TxFilterLowHz { get; init; } = -12000;

    /// <inheritdoc cref="TxFilterLowHz" />
    public int TxFilterHighHz { get; init; } = 12000;

    /// <summary>
    /// Low cut of the radio's transmit filter (Hz from the carrier), applied with
    /// <c>transmit set filter_low=</c>. Null leaves the radio's current setting. Default 0.
    /// </summary>
    public int? TransmitFilterLowHz { get; init; } = 0;

    /// <summary>
    /// High cut of the radio's transmit filter (Hz from the carrier), applied with
    /// <c>transmit set filter_high=</c>. Null leaves the radio's current setting. Default
    /// <see cref="MaxTransmitFilterHighHz"/>. Ignored when <see cref="Band"/> is set, which sizes the
    /// filter from the declared bandwidth instead.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the setting that caps occupied bandwidth.</b> The radio's factory value is a
    /// 3 kHz SSB passband, so a waveform client that does not raise it transmits 3 kHz however wide
    /// its IQ is — silently, with every command returning <c>err=0</c>.</para>
    /// <para>Note the read/write asymmetry: the value is <i>reported</i> on the <c>transmit</c>
    /// object as <c>lo</c>/<c>hi</c>, but <c>transmit set hi=</c> is rejected (<c>0x5000002D</c>) —
    /// it must be set with <c>filter_low=</c>/<c>filter_high=</c>. Values above
    /// <see cref="MaxTransmitFilterHighHz"/> are silently clamped.</para>
    /// <para>It is a <b>global</b> radio setting: it persists after teardown and affects ordinary SSB
    /// transmit. The factory value is 0–3000 Hz.</para>
    /// </remarks>
    public int? TransmitFilterHighHz { get; init; } = MaxTransmitFilterHighHz;

    /// <summary>The radio's hard ceiling on the transmit filter's high cut (Hz). Requests above this
    /// are silently clamped. Measured on a FLEX-6500, firmware 4.1.5.</summary>
    public const int MaxTransmitFilterHighHz = 10000;

    /// <summary>Optional TX RF power (0–100). Null leaves the radio's current setting.</summary>
    public int? RfPower { get; init; }

    /// <summary>How long to wait for a slice object / frequency to appear (default 5 s).</summary>
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Brings up a FlexRadio <b>waveform</b> for complex IQ transmit over a connected
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

    /// <summary>
    /// How an underlying mode maps our complex baseband onto RF. Measured on a FLEX-6500, fw 4.1.5.
    /// </summary>
    /// <remarks>
    /// <b>Every</b> mode passes only the <b>negative</b> half of the baseband — positive-frequency
    /// content never reaches the air. What differs is which side of the carrier the surviving half is
    /// placed on, and therefore whether the caller's spectrum arrives upright or mirrored. This is
    /// ordinary SSB behaviour: the radio treats the IQ as an analytic signal and either
    /// conjugates-then-upconverts (lower) or upconverts directly (upper).
    /// </remarks>
    private enum SidebandMapping
    {
        /// <summary>A baseband component at <c>−f</c> is transmitted at <c>slice − f</c>, so
        /// <c>RF = slice + baseband</c> and the spectrum arrives <b>upright</b>. RAW, LSB, DIGL.</summary>
        LowerUpright,

        /// <summary>A baseband component at <c>−f</c> is transmitted at <c>slice + f</c>, so
        /// <c>RF = slice − baseband</c> and the spectrum arrives <b>mirrored</b>. IQ, USB, DIGU.</summary>
        UpperMirrored,

        /// <summary>Discards Q entirely (AM, FM) or is not characterised.</summary>
        Unsupported,
    }

    private static SidebandMapping MappingFor(string mode) => mode.ToUpperInvariant() switch
    {
        "RAW" or "LSB" or "DIGL" => SidebandMapping.LowerUpright,
        "IQ" or "USB" or "DIGU" => SidebandMapping.UpperMirrored,
        _ => SidebandMapping.Unsupported,
    };

    /// <summary>
    /// The one underlying mode band placement will use. <c>LSB</c> and <c>DIGL</c> place a band on
    /// exactly the same frequencies, but they are full audio modes: the transmitter's compander and
    /// speech processing sit in that chain and are not known to be bypassed, and neither would show
    /// up in a tone or waterfall check while quietly mangling a modulated signal. <c>RAW</c> is
    /// documented by FlexRadio as "I/Q samples with minimal processing" and is the only mode verified
    /// end to end for arbitrary IQ, so placement insists on it rather than offering a choice between
    /// one known-good option and two unverified ones.
    /// </summary>
    private const string PlacementMode = "RAW";

    /// <summary>
    /// Works out where to put the slice and how far to shift the caller's samples so the signal lands
    /// on the requested span, in the sideband this mode actually passes.
    /// </summary>
    /// <remarks>
    /// The samples are <b>frequency-shifted, never conjugated</b>. Mirroring would map a one-sided
    /// baseband onto the other sideband just as neatly and cost nothing — and would spectrally invert
    /// the signal, so a QPSK or OFDM waveform would go out looking perfect on a spectrum analyser and
    /// decode nowhere.
    /// </remarks>
    private static (double SliceMhz, double ShiftHz, double LowMhz, double HighMhz) PlaceBand(
        FlexWaveformOptions options, IqBand band)
    {
        double referenceMhz = band.FrequencyMhz;
        int bandwidthHz = band.BandwidthHz;
        SidebandMapping mapping = MappingFor(options.UnderlyingMode);

        if (mapping == SidebandMapping.Unsupported)
        {
            throw new FlexProtocolException(
                $"underlying_mode={options.UnderlyingMode} does not carry complex IQ (AM and FM discard "
                + "the Q channel), so a bandwidth cannot be placed on it; use RAW, LSB or DIGL");
        }

        if (!options.UnderlyingMode.Equals(PlacementMode, StringComparison.OrdinalIgnoreCase)
            && mapping == SidebandMapping.LowerUpright)
        {
            throw new FlexProtocolException(
                $"band placement uses underlying_mode={PlacementMode}; {options.UnderlyingMode} places "
                + "the same band on the same frequencies but routes through a full audio mode, whose "
                + "compander and speech processing are not known to be bypassed and would not be "
                + "visible on a spectrum display. Set UnderlyingMode=RAW, or place the IQ yourself by "
                + "leaving OccupiedBandwidthHz unset");
        }

        if (mapping == SidebandMapping.UpperMirrored)
        {
            // Reachable, but only by conjugating the caller's samples to cancel the radio's flip —
            // and it buys nothing, since the lower-side modes place the same band the same width with
            // no mirror step to get wrong. Refuse rather than ship a spectral inversion.
            throw new FlexProtocolException(
                $"underlying_mode={options.UnderlyingMode} transmits the spectrum mirrored (a baseband "
                + "component at −f is emitted at slice+f), so a band placed on it would go out "
                + "inverted; use RAW, LSB or DIGL, which are upright and reach the same frequencies");
        }

        double widthMhz = bandwidthHz / 1e6;
        (double lowMhz, double highMhz) = band.Reference == IqBandReference.Centre
            ? (referenceMhz - (widthMhz / 2), referenceMhz + (widthMhz / 2))
            : (referenceMhz, referenceMhz + widthMhz);

        // Where the caller's samples start, relative to their own DC.
        double callerLowHz = band.Reference == IqBandReference.Centre ? -bandwidthHz / 2.0 : 0;

        // Only negative baseband reaches the air, and here RF = slice + baseband. Anchor the slice at
        // the TOP of the band (baseband zero) and shift the caller's span down below it.
        return (highMhz, -bandwidthHz - callerLowHz, lowMhz, highMhz);
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
        waveform.ResolvePlacement();
        await waveform.CreateSliceAsync(cancellation).ConfigureAwait(false);
        await waveform.EnsureTunedAsync(cancellation).ConfigureAwait(false);
        await client.SendCommandExpectOkAsync(
            $"slice set {waveform.SliceIndex} mode={options.Mode}", cancellation).ConfigureAwait(false);

        if (options.RfPower is int power)
        {
            await client.SendCommandExpectOkAsync($"transmit set rfpower={power}", cancellation).ConfigureAwait(false);
        }

        // 3. Widen the radio's transmit filter. This, not the waveform's tx_filter, is what caps
        //    occupied bandwidth — leaving it at the 3 kHz factory value silently truncates the band.
        await waveform.ApplyTransmitFilterAsync(cancellation).ConfigureAwait(false);

        return waveform;
    }

    /// <summary>The slice (dial) frequency in MHz the waveform actually runs on. With band placement
    /// this is derived from the requested band and sideband, so it is normally <i>not</i> the
    /// frequency that was asked for.</summary>
    public double SliceFrequencyMhz { get; private set; }

    /// <summary>The RF span the signal will occupy, in MHz, once placed — null without band
    /// placement.</summary>
    public (double LowMhz, double HighMhz)? OccupiedBand { get; private set; }

    /// <summary>The frequency shift, in Hz, the IQ output applies to move the caller's samples below
    /// DC, which is the only half the radio transmits. Zero when band placement is not in use.</summary>
    public double BasebandShiftHz { get; private set; }

    /// <summary>The transmit passband the radio reports after setup, as (low, high) in Hz from the
    /// carrier, or null if it never reported one.</summary>
    public (int Low, int High)? TransmitFilter { get; private set; }

    /// <summary>A human-readable warning when the transmit filter ended up narrower than requested
    /// (the radio clamps it), else null. Setup does not throw — the signal still transmits, just
    /// truncated, and the caller may not care.</summary>
    public string? TransmitFilterWarning { get; private set; }

    /// <summary>
    /// Applies <see cref="FlexWaveformOptions.TransmitFilterLowHz"/>/<see cref="FlexWaveformOptions.TransmitFilterHighHz"/>
    /// and reads back what the radio actually took.
    /// </summary>
    /// <remarks>
    /// Best-effort throughout: a radio that will not widen its transmit filter still transmits, so a
    /// narrow result is surfaced on <see cref="TransmitFilterWarning"/> rather than thrown. The
    /// read-back matters because the high cut is clamped silently — the command returns err=0 either
    /// way.
    /// </remarks>
    private async Task ApplyTransmitFilterAsync(CancellationToken cancellation)
    {
        // Band placement knows exactly how far the signal reaches, so it sets the filter itself.
        int? wantHighHz = _options.Band?.BandwidthHz ?? _options.TransmitFilterHighHz;

        if (_options.TransmitFilterLowHz is null && wantHighHz is null)
        {
            return;
        }

        // The transmit object is only readable once subscribed.
        await TryBestEffortAsync("sub tx all", cancellation).ConfigureAwait(false);

        if (_options.TransmitFilterLowHz is int low)
        {
            await TryBestEffortAsync($"transmit set filter_low={low}", cancellation).ConfigureAwait(false);
        }

        if (wantHighHz is int high)
        {
            await TryBestEffortAsync($"transmit set filter_high={high}", cancellation).ConfigureAwait(false);
        }

        // Wait for the value the radio settled on, not merely the first one it reports: status is
        // asynchronous, so the pre-change value is usually still cached when the setter returns.
        int? expectedHigh = wantHighHz is int wanted
            ? Math.Min(wanted, FlexWaveformOptions.MaxTransmitFilterHighHz)
            : null;

        (int Low, int High)? applied = await WaitForTransmitFilterAsync(
            expectedHigh, _options.SetupTimeout, cancellation).ConfigureAwait(false);
        TransmitFilter = applied;

        // A band the radio cannot pass is a hard failure: transmitting it anyway would put a
        // silently truncated signal on air, which looks exactly like success.
        if (_options.Band?.BandwidthHz is int needed
            && applied is (int _, int achievable)
            && achievable < needed)
        {
            throw new FlexProtocolException(
                $"the radio's transmit filter reached only {achievable} Hz, so a {needed} Hz signal "
                + $"would be truncated by {needed - achievable} Hz on air; request a narrower "
                + "OccupiedBandwidthHz");
        }

        if (applied is (int gotLow, int gotHigh)
            && wantHighHz is int wantHigh
            && gotHigh < wantHigh)
        {
            TransmitFilterWarning =
                $"transmit filter is {gotLow}–{gotHigh} Hz, narrower than the requested high cut of "
                + $"{wantHigh} Hz (the radio clamps it); anything beyond {gotHigh} Hz from the carrier "
                + "will not reach the air";
            Debug.WriteLine($"flex: {TransmitFilterWarning}");
        }
    }

    private async Task<(int Low, int High)?> WaitForTransmitFilterAsync(
        int? expectedHigh, TimeSpan timeout, CancellationToken cancellation)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        (int Low, int High)? last = null;
        while (true)
        {
            last = TryReadTransmitFilter() ?? last;

            // Settle on the expected value when there is one; a radio whose ceiling differs from
            // ours simply costs the timeout, and the value returned is still the truth.
            if (last is (int _, int high) && (expectedHigh is null || high == expectedHigh))
            {
                return last;
            }

            if (Environment.TickCount64 > deadline)
            {
                return last;
            }

            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }
    }

    private (int Low, int High)? TryReadTransmitFilter()
    {
        if (_client.TryFindObject("transmit", _ => true, out string objectName)
            && _client.TryGetObject(objectName, out IReadOnlyDictionary<string, string> transmit)
            && transmit.TryGetValue("lo", out string? lo)
            && transmit.TryGetValue("hi", out string? hi)
            && int.TryParse(lo, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lowHz)
            && int.TryParse(hi, NumberStyles.Integer, CultureInfo.InvariantCulture, out int highHz))
        {
            return (lowHz, highHz);
        }

        return null;
    }

    /// <summary>Creates the slice PTT (keying).</summary>
    public FlexPtt CreatePtt(bool confirmInterlock = false) => new(_client, SliceIndex, confirmInterlock);

    /// <summary>Creates the IQ transmit sink, pre-configured with any shift band placement needs.</summary>
    public FlexWaveformIqOutput CreateIqOutput(double bufferSeconds = 3.0) =>
        new(_client, bufferSeconds, BasebandShiftHz);

    /// <summary>Turns the requested band into a slice frequency and a baseband shift, or leaves the
    /// low-level behaviour alone when no bandwidth was requested.</summary>
    private void ResolvePlacement()
    {
        // Exactly one of the two. Checked here rather than left to a default, because "which mode am
        // I in" being implied by whether some other property happens to be set is the confusion this
        // API shape exists to remove.
        if (_options.Band is null && _options.SliceFrequencyMhz is null)
        {
            throw new FlexProtocolException(
                "set either SliceFrequencyMhz (tune here, samples sent untouched) or Band (place a "
                + "signal of a declared width at a declared frequency)");
        }

        if (_options.Band is not null && _options.SliceFrequencyMhz is not null)
        {
            throw new FlexProtocolException(
                "SliceFrequencyMhz and Band are mutually exclusive — Band derives the slice frequency "
                + "itself, so setting both cannot be honoured");
        }

        if (_options.Band is not IqBand band)
        {
            SliceFrequencyMhz = _options.SliceFrequencyMhz!.Value;
            return;
        }

        if (band.BandwidthHz <= 0)
        {
            throw new FlexProtocolException($"IqBand.BandwidthHz must be positive (got {band.BandwidthHz})");
        }

        (double sliceMhz, double shiftHz, double lowMhz, double highMhz) =
            PlaceBand(_options, band);

        SliceFrequencyMhz = sliceMhz;
        BasebandShiftHz = shiftHz;
        OccupiedBand = (lowMhz, highMhz);
    }

    /// <summary>The frequency the slice is actually tuned to — the placed one when band placement is
    /// in use, else the requested one.</summary>
    private string SliceTuneFrequency =>
        SliceFrequencyMhz.ToString("F6", CultureInfo.InvariantCulture);

    private async Task CreateSliceAsync(CancellationToken cancellation)
    {
        _client.SendCommandNoWait("sub slice all");
        await _client.SendCommandExpectOkAsync(
            $"slice create freq={SliceTuneFrequency} ant={_options.Antenna} mode=DIGU rxant={_options.Antenna}",
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
        double wantMhz = SliceFrequencyMhz;

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

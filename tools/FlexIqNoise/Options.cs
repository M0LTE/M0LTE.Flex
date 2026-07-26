using System.Globalization;

namespace M0LTE.Flex.Tools.IqNoise;

/// <summary>The parsed command line for the noise rig.</summary>
internal sealed record Options
{
    /// <summary>Radio selector: an IP/hostname, or a discovery spec ("discover", "serial=…", "name=…").</summary>
    public string Radio { get; init; } = "discover";

    /// <summary>Centre frequency in MHz — the RF frequency the noise band is centred on.</summary>
    public double CentreMhz { get; init; } = 14.100000;

    /// <summary>Noise bandwidth in Hz. The band occupies centre ± <see cref="BandwidthHz"/>/2.</summary>
    public double BandwidthHz { get; init; } = 2000;

    /// <summary>Offset of the noise band from the slice centre, in Hz (normally 0).</summary>
    public double OffsetHz { get; init; }

    /// <summary>True once <see cref="OffsetHz"/> has been set explicitly, so the default placement
    /// is not applied over the top of it.</summary>
    private bool OffsetSpecified { get; init; }

    /// <summary>On-air duration in seconds (sample-accurate, not wall clock).</summary>
    public double Seconds { get; init; } = 10;

    /// <summary>Target per-component RMS of the transmitted IQ, where ±1.0 is full scale.</summary>
    public double Rms { get; init; } = 0.15;

    /// <summary>TX power in watts (the radio's <c>rfpower</c> setting, 0–100 on a 6500). This is the
    /// PA ceiling at full-scale drive — the actual average power is this scaled by the IQ drive
    /// level, which for Gaussian noise is deliberately well backed off. See
    /// <see cref="AveragePowerWatts"/>.</summary>
    public double PowerWatts { get; init; } = 5;

    /// <summary>Antenna port for both RX and TX.</summary>
    public string Antenna { get; init; } = "ANT1";

    /// <summary>Waveform underlying mode. Only the negative half of the baseband is transmitted in
    /// every mode; RAW/LSB/DIGL place it below the carrier (upright), IQ/USB/DIGU above it
    /// (mirrored), AM/FM discard Q. RAW is the one verified for arbitrary IQ.</summary>
    public string UnderlyingMode { get; init; } = "RAW";

    /// <summary>Waveform TX filter cuts (Hz). Deliberately wide by default so the radio's filter does
    /// not confound the measurement — the noise band alone should define the occupied bandwidth.</summary>
    public int TxFilterLowHz { get; init; } = -12000;

    /// <inheritdoc cref="TxFilterLowHz" />
    public int TxFilterHighHz { get; init; } = 12000;

    /// <summary>PRNG seed, for a reproducible burst.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>When set, transmit complex tones at these offsets instead of noise — the asymmetric
    /// probe that tells a lost sideband from an inverted spectrum. Empty means noise.</summary>
    public double[] ToneOffsetsHz { get; init; } = [];

    /// <summary>True when the burst is tones rather than noise.</summary>
    public bool IsTone => ToneOffsetsHz.Length > 0;

    /// <summary>Explore mode: key once and transmit a single tone that the arrow keys retune live,
    /// for walking the radio's passband by ear/eye. Null means an ordinary run.</summary>
    public int? ExploreStartHz { get; init; }

    /// <summary>True when this run is an interactive passband exploration.</summary>
    public bool IsExplore => ExploreStartHz is not null;

    /// <summary>Drive the library's band-placement API instead of placing IQ by hand: declare where
    /// the signal goes and how wide it is, and let M0LTE.Flex derive the slice, sideband and shift.
    /// This is the mode that validates the placement contract end to end.</summary>
    public bool Placed { get; init; }

    /// <summary>Which baseband convention to declare when <see cref="Placed"/>.</summary>
    public IqBandReference BandReference { get; init; } = IqBandReference.Centre;

    /// <summary>The RF span the signal will occupy under band placement, per the declared
    /// convention.</summary>
    public (double LowMhz, double HighMhz) PlacedBandMhz => BandReference == IqBandReference.LowerEdge
        ? (CentreMhz, CentreMhz + (BandwidthHz / 1e6))
        : (CentreMhz - (BandwidthHz / 2e6), CentreMhz + (BandwidthHz / 2e6));

    /// <summary>How much of the requested signal sits above DC, and so will not be transmitted at
    /// all. Zero when the whole thing is safely below DC.</summary>
    public double UntransmittableHighHz => IsTone
        ? Math.Max(0, MaxToneOffsetHz)
        : Math.Max(0, HighEdgeHz);

    /// <summary>True when nothing in the request is below DC, so the radio will emit only carrier
    /// leakage.</summary>
    public bool IsEntirelyAboveDc => IsTone
        ? MinToneOffsetHz > 0
        : LowEdgeHz >= 0;

    private double MaxToneOffsetHz
    {
        get
        {
            double max = double.NegativeInfinity;
            foreach (double offset in ToneOffsetsHz)
            {
                max = Math.Max(max, offset);
            }

            return max;
        }
    }

    private double MinToneOffsetHz
    {
        get
        {
            double min = double.PositiveInfinity;
            foreach (double offset in ToneOffsetsHz)
            {
                min = Math.Min(min, offset);
            }

            return min;
        }
    }

    /// <summary>The baseband span the caller works in under band placement, per the declared
    /// convention: <c>0…bw</c> for a lower-edge reference, <c>−bw/2…+bw/2</c> for a centred one.</summary>
    public (double Low, double High) PlacedBasebandRange => BandReference == IqBandReference.LowerEdge
        ? (0, BandwidthHz)
        : (-BandwidthHz / 2, BandwidthHz / 2);

    /// <summary>Underlying modes to walk in turn, transmitting the same probe under each. Empty
    /// means a single ordinary run.</summary>
    public string[] SweepModes { get; init; } = [];

    /// <summary>True when this run sweeps underlying modes.</summary>
    public bool IsSweep => SweepModes.Length > 0;

    /// <summary>The modes a bare <c>--sweep</c> walks: every value the 6500's firmware accepts for
    /// <c>underlying_mode</c>, so the whole table is re-derived rather than assumed.</summary>
    public static readonly string[] DefaultSweepModes =
        ["RAW", "IQ", "USB", "LSB", "DIGU", "DIGL", "AM", "FM"];

    /// <summary>Generate and analyse the burst but never key the radio.</summary>
    public bool DryRun { get; init; }

    /// <summary>Dump the radio's full slice status after setup.</summary>
    public bool Verbose { get; init; }

    /// <summary>Set up the waveform, run the probes and read status back, but never key. Lets the
    /// radio's configuration be explored with no RF at all.</summary>
    public bool NoTx { get; init; }

    /// <summary>Extra raw commands to send after setup and before keying, each with its error code
    /// printed. The escape hatch for probing firmware behaviour without a rebuild.</summary>
    public string[] PostCommands { get; init; } = [];

    /// <summary>When set, apply a slice passband with <c>filt &lt;idx&gt; &lt;lo&gt; &lt;hi&gt;</c>
    /// after the mode switch — the slice-level filter, as distinct from the waveform's own.</summary>
    public (int Lo, int Hi)? SliceFilter { get; init; }

    /// <summary>When set, widen the radio's global SSB <b>transmit</b> filter with
    /// <c>transmit set filter_low=/filter_high=</c>. On the 6500 the measured ~3 kHz transmit
    /// passband matches this filter, not the slice's RX filter and not the waveform's tx_filter.</summary>
    public (int Lo, int Hi)? TransmitFilter { get; init; }

    /// <summary>Optional path for a 2-channel float32 WAV of the generated IQ (ch1 = I, ch2 = Q).</summary>
    public string? WavPath { get; init; }

    /// <summary>Optional path for a CSV of the measured power spectrum (dry-run analysis).</summary>
    public string? CsvPath { get; init; }

    /// <summary>The waveform's complex sample rate, and hence the widest band we can synthesise.</summary>
    public const int SampleRate = FlexWaveformIqOutput.SampleRate;

    public double NyquistHz => SampleRate / 2.0;

    /// <summary>True when the requested band fills the whole waveform rate, so no filtering applies.</summary>
    public bool IsFullBand => BandwidthHz >= SampleRate;

    public double LowEdgeHz => OffsetHz - BandwidthHz / 2;

    public double HighEdgeHz => OffsetHz + BandwidthHz / 2;

    /// <summary>The radio's hard ceiling on the transmit filter's high cut, measured on the 6500:
    /// values above this are silently clamped to it.</summary>
    public const int MaxTransmitFilterHighHz = 10000;

    /// <summary>How wide the radio's transmit filter must be for the requested signal to reach the
    /// air intact. The transmit passband is an <i>audio</i> filter mapped into the active sideband,
    /// so what matters is the furthest offset from the carrier the signal reaches.</summary>
    public int RequiredTxFilterHighHz
    {
        get
        {
            // Under placement the library sets the filter from the declared bandwidth, so report
            // that rather than a span derived from --bw/--offset, which do not apply.
            if (Placed)
            {
                return (int)Math.Ceiling(BandwidthHz);
            }

            if (IsExplore)
            {
                return MaxTransmitFilterHighHz;
            }

            double needed = 0;
            if (IsTone)
            {
                foreach (double offset in ToneOffsetsHz)
                {
                    needed = Math.Max(needed, Math.Abs(offset));
                }
            }
            else
            {
                needed = Math.Max(Math.Abs(LowEdgeHz), Math.Abs(HighEdgeHz));
            }

            return (int)Math.Ceiling(needed);
        }
    }

    /// <summary>Mean power of the complex IQ relative to full scale — 2σ² for per-component RMS σ.</summary>
    public double DriveFraction => 2 * Rms * Rms;

    /// <summary>Backoff of the drive level below full scale, in dB.</summary>
    public double DriveBackoffDb => 10 * Math.Log10(DriveFraction);

    /// <summary>Estimated average radiated power: the PA ceiling scaled by the drive level. Gaussian
    /// noise runs ~10 dB backed off, so this is well below <see cref="PowerWatts"/>.</summary>
    public double AveragePowerWatts => PowerWatts * DriveFraction;

    public static Options Parse(string[] args)
    {
        var options = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"unexpected argument '{arg}' (options start with --)");
            }

            string key = arg[2..];
            string? inlineValue = null;
            int equals = key.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                inlineValue = key[(equals + 1)..];
                key = key[..equals];
            }

            string Value()
            {
                if (inlineValue is not null)
                {
                    return inlineValue;
                }

                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"--{key} needs a value");
                }

                return args[++i];
            }

            switch (key.ToLowerInvariant())
            {
                case "radio": options = options with { Radio = Value() }; break;
                case "freq": options = options with { CentreMhz = ParseFrequencyMhz(Value()) }; break;
                case "bw": options = options with { BandwidthHz = ParseHz(Value()) }; break;
                case "offset": options = options with { OffsetHz = ParseHz(Value()), OffsetSpecified = true }; break;
                case "seconds": options = options with { Seconds = ParseDouble(Value(), "--seconds") }; break;
                case "rms": options = options with { Rms = ParseDouble(Value(), "--rms") }; break;
                case "power":
                case "rfpower":
                    options = options with { PowerWatts = ParseDouble(Value(), "--power") };
                    break;
                case "ant": options = options with { Antenna = Value() }; break;
                case "underlying": options = options with { UnderlyingMode = Value().ToUpperInvariant() }; break;
                case "txfilter-low": options = options with { TxFilterLowHz = (int)ParseHz(Value()) }; break;
                case "txfilter-high": options = options with { TxFilterHighHz = (int)ParseHz(Value()) }; break;
                case "seed": options = options with { Seed = (int)ParseDouble(Value(), "--seed") }; break;
                case "tone": options = options with { ToneOffsetsHz = ParseOffsets(Value()) }; break;
                case "explore":
                    options = options with
                    {
                        ExploreStartHz = inlineValue is null
                            && (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                                ? -500                       // negative: the only half that transmits
                                : (int)ParseHz(Value()),
                    };
                    break;
                case "sweep":
                    options = options with
                    {
                        SweepModes = inlineValue is null && (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                            ? DefaultSweepModes
                            : Value().ToUpperInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    };
                    break;
                case "dry-run": options = options with { DryRun = true }; break;
                case "verbose": options = options with { Verbose = true }; break;
                case "placed": options = options with { Placed = true }; break;
                case "reference":
                    options = options with
                    {
                        BandReference = Value().ToLowerInvariant() switch
                        {
                            "centre" or "center" => IqBandReference.Centre,
                            "loweredge" or "lower-edge" or "edge" => IqBandReference.LowerEdge,
                            var other => throw new ArgumentException(
                                $"--reference must be centre or loweredge (got '{other}')"),
                        },
                    };
                    break;
                case "no-tx": options = options with { NoTx = true }; break;
                case "post": options = options with { PostCommands = [.. options.PostCommands, Value()] }; break;
                case "transmit-filter":
                {
                    string[] cuts = Value().Split(',', StringSplitOptions.TrimEntries);
                    if (cuts.Length != 2)
                    {
                        throw new ArgumentException("--transmit-filter takes <lo>,<hi> in Hz");
                    }

                    options = options with { TransmitFilter = ((int)ParseHz(cuts[0]), (int)ParseHz(cuts[1])) };
                    break;
                }

                case "slice-filter":
                {
                    string[] cuts = Value().Split(',', StringSplitOptions.TrimEntries);
                    if (cuts.Length != 2)
                    {
                        throw new ArgumentException("--slice-filter takes <lo>,<hi> in Hz");
                    }

                    options = options with { SliceFilter = ((int)ParseHz(cuts[0]), (int)ParseHz(cuts[1])) };
                    break;
                }
                case "wav": options = options with { WavPath = Value() }; break;
                case "csv": options = options with { CsvPath = Value() }; break;
                default: throw new ArgumentException($"unknown option --{key}");
            }
        }

        options = options.ApplyDefaultPlacement();

        if (options.Placed && !options.IsExplore)
        {
            // Under placement the caller must supply IQ in the convention it declared, or the
            // library's shift lands half the band outside what was asked for. --offset does not apply.
            options = options with
            {
                OffsetHz = options.BandReference == IqBandReference.LowerEdge
                    ? options.BandwidthHz / 2
                    : 0,
                OffsetSpecified = true,
            };
        }

        if (options.IsSweep && !options.IsTone)
        {
            // A SINGLE tone below DC, deliberately asymmetric. A symmetric probe cannot tell "the +f
            // tone passed" from "the −f tone passed and the mode inverts" — it contains both — and
            // reading one off a symmetric probe is what produced two wrong conclusions about this
            // radio. With one tone at −3 kHz, which side of the carrier it lands on IS the answer.
            options = options with { ToneOffsetsHz = [-3000] };
        }

        options.Validate();
        return options;
    }

    /// <summary>
    /// Places the band below DC unless an explicit <c>--offset</c> says otherwise.
    /// </summary>
    /// <remarks>
    /// Only content below DC is transmitted, in any mode, so this is the only placement that puts the
    /// whole requested width on air. There is deliberately no option to select the other side: it
    /// would be a named choice between one correct value and two that quietly lose signal.
    /// <c>--offset</c> remains for putting energy somewhere specific on purpose — including above DC
    /// to confirm it does not transmit — and warns when it does.
    /// </remarks>
    private Options ApplyDefaultPlacement() =>
        OffsetSpecified ? this : this with { OffsetHz = -BandwidthHz / 2 };

    private void Validate()
    {
        if (CentreMhz is <= 0 or > 100)
        {
            throw new ArgumentException($"--freq {CentreMhz} MHz is outside the 6000-series range");
        }

        if (BandwidthHz <= 0 || BandwidthHz > SampleRate)
        {
            throw new ArgumentException(
                $"--bw must be >0 and <= {SampleRate} Hz (the waveform's complex sample rate)");
        }

        // Nothing outside ±12 kHz can be synthesised at a 24 kHz complex rate; anything asked for
        // beyond it would alias back in-band and quietly corrupt the measurement.
        if (LowEdgeHz < -NyquistHz || HighEdgeHz > NyquistHz)
        {
            throw new ArgumentException(
                $"the requested band ({LowEdgeHz:F0}..{HighEdgeHz:F0} Hz from centre) falls outside "
                + $"±{NyquistHz:F0} Hz, the 24 kHz waveform rate's synthesis limit — reduce --bw or "
                + $"--offset. Note the radio only transmits about {MaxTransmitFilterHighHz} Hz on one "
                + "side of the carrier anyway");
        }

        if (Seconds <= 0)
        {
            throw new ArgumentException("--seconds must be > 0");
        }

        if (Rms is <= 0 or > 1)
        {
            throw new ArgumentException("--rms must be in (0, 1]");
        }

        if (PowerWatts is < 0 or > 100)
        {
            throw new ArgumentException($"--power {PowerWatts} W is outside the 0..100 W range");
        }

        if (TxFilterHighHz <= TxFilterLowHz)
        {
            throw new ArgumentException("--txfilter-high must exceed --txfilter-low");
        }

        if (ExploreStartHz is int start && Math.Abs(start) > NyquistHz)
        {
            throw new ArgumentException($"--explore {start} Hz falls outside ±{NyquistHz:F0} Hz");
        }

        foreach (double offset in ToneOffsetsHz)
        {
            if (Math.Abs(offset) > NyquistHz)
            {
                throw new ArgumentException(
                    $"--tone {offset:F0} Hz falls outside ±{NyquistHz:F0} Hz and would alias");
            }
        }
    }

    /// <summary>Parses a comma-separated list of Hz offsets, e.g. <c>3k</c> or <c>3k,-3k,7k,-7k</c>.</summary>
    private static double[] ParseOffsets(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("--tone needs at least one offset, e.g. --tone 3k");
        }

        return Array.ConvertAll(parts, ParseHz);
    }

    /// <summary>Parses a frequency. A bare number is MHz (the Flex convention); an explicit
    /// <c>k</c>/<c>M</c>/<c>Hz</c> suffix wins, so "14200k", "14.2M" and "14200000Hz" all work.</summary>
    internal static double ParseFrequencyMhz(string text) => ParseHz(text, bareUnitHz: 1e6) / 1e6;

    /// <summary>Parses a value in Hz, honouring a <c>k</c>/<c>M</c> suffix. A bare number is Hz.</summary>
    internal static double ParseHz(string text) => ParseHz(text, bareUnitHz: 1);

    private static double ParseHz(string text, double bareUnitHz)
    {
        string trimmed = text.Trim();

        // A trailing "Hz" states the unit outright, so it must beat the bare-number default: plain
        // "14200000Hz" is 14.2 MHz, not 14200000 MHz.
        bool unitStated = false;
        if (trimmed.EndsWith("hz", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2].Trim();
            unitStated = true;
        }

        double multiplier = unitStated ? 1 : bareUnitHz;
        if (trimmed.EndsWith('k') || trimmed.EndsWith('K'))
        {
            multiplier = 1e3;
            trimmed = trimmed[..^1];
        }
        else if (trimmed.EndsWith('M') || trimmed.EndsWith('m'))
        {
            multiplier = 1e6;
            trimmed = trimmed[..^1];
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw new ArgumentException($"'{text}' is not a number");
        }

        return value * multiplier;
    }

    private static double ParseDouble(string text, string what) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new ArgumentException($"{what}: '{text}' is not a number");

    public const string Usage = """
        flex-iq-noise — transmit band-limited Gaussian white noise through a FlexRadio 6000
                        via the Waveform API, to characterise its IQ transmit bandwidth.

        USAGE
          flex-iq-noise --freq <f> --bw <hz> [--seconds <n>] [options]

        THE SIGNAL
          Complex (I/Q) Gaussian noise, flat across the requested band and sharply band-limited,
          synthesised at the waveform's 24 kHz complex rate and centred on --freq. So
            --freq 14.200 --bw 2k
          puts noise from 14.199 to 14.201 MHz and nothing outside it. Whatever the receiver
          shows beyond those edges is the radio, not the rig.

        OPTIONS
          --radio <spec>      radio IP/hostname, discovery spec, or "mock" for an in-process
                              fake radio that proves the whole path with no RF (default: discover)
          --freq <f>          centre frequency; bare = MHz, or suffix k/M (default: 14.100000)
          --bw <hz>           noise bandwidth; bare = Hz, or suffix k (default: 2000)
          --offset <hz>       put the band's centre at this offset from the carrier instead of the
                              default placement. The band normally occupies -bw..0, because only
                              content BELOW the carrier is transmitted; use this to place energy
                              deliberately elsewhere — including above it, to confirm that nothing
                              comes out. Anything above DC is warned about before keying.
          --seconds <n>       on-air duration, sample-accurate (default: 10)
          --power <watts>     TX power, the radio's rfpower setting (default: 5)
          --rms <0..1>        per-component IQ drive, ±1.0 = full scale (default: 0.15). Gaussian
                              noise must run backed off or its peaks clip and splatter; the default
                              sits ~13 dB down, so average power ≈ --power × 0.045.
          --ant <port>        antenna (default: ANT1)
          --underlying <m>    waveform underlying mode. Only NEGATIVE baseband is transmitted, in
                              every mode; the mode picks which side of the carrier it lands on:
                                RAW, LSB, DIGL   below the carrier, spectrum upright
                                IQ, USB, DIGU    above the carrier, spectrum MIRRORED
                                AM, FM           Q discarded entirely
                              (default: RAW — the only one verified for arbitrary IQ)
          --txfilter-low <hz> waveform TX filter cuts. Default ±12000 — deliberately wide, so the
          --txfilter-high <hz>  radio's own filter does not confound the measurement.
          --seed <n>          PRNG seed for a reproducible burst (default: 1)
          --tone <hz[,hz…]>   transmit complex tones instead of noise — the probe that tells a
                              lost sideband from an inverted spectrum (see DIAGNOSING below)
          --sweep [m1,m2,…]   transmit the probe under each underlying_mode in turn (bare --sweep
                              walks RAW,IQ,USB,LSB,DIGU,DIGL,AM,FM). Defaults to a SINGLE tone at
                              -3 kHz, so which side of the carrier it lands on identifies the
                              mode outright. A symmetric probe cannot: it contains both tones,
                              so "passed +f" and "passed -f, inverted" look identical
          --explore [hz]      INTERACTIVE. Key once and transmit a single tone, retuned live from
                              the keyboard, to walk the radio's passband (default start: -500 Hz,
                              negative because only that half of the baseband is transmitted).
                              The tone is moved by regenerating the IQ — the dial never moves —
                              so the passband stays put while the signal sweeps through it.
                                up / down     ±100 Hz        left / right  ±10 Hz
                                PgUp / PgDn   ±1000 Hz       0             back to 0 Hz
                                q or Esc      unkey and quit
          --placed            drive the library's band-placement API: declare the band with --freq
                              and --bw and let M0LTE.Flex derive the slice, sideband and shift,
                              instead of this tool placing IQ by hand. Validates the contract.
          --reference <r>     baseband convention to declare with --placed: "centre" (you supply
                              DC-centred IQ, --freq is the band centre) or "loweredge" (you supply
                              0..bw, --freq is the lower edge). Default: centre
          --dry-run           generate and analyse the burst offline; never contact the radio
          --no-tx             connect, set up the waveform, run the probes and read status back,
                              but never key — explore the radio's config with no RF
          --verbose           dump the radio's full slice status after setup
          --transmit-filter <l,h>
                              widen the radio's global SSB TRANSMIT filter ("transmit set
                              filter_low=/filter_high="). On the 6500 the ~3 kHz transmit
                              passband tracks THIS filter — try it first
          --slice-filter <l,h> also set the SLICE passband ("filt <idx> <lo> <hi>"). Note this is
                              the slice's RECEIVE filter, so it is unlikely to be the lever
          --post "<cmd>"      send a raw command after setup, before keying, printing its error
                              code. Repeatable. The escape hatch for probing firmware:
                                --post "filt 0 -12000 12000"
                                --post "waveform set IqNoise tx_filter high_cut=12000"
          --wav <path>        also write the IQ as a 2-ch float32 WAV (ch1 = I, ch2 = Q)
          --csv <path>        write the measured spectrum as CSV (with --dry-run)
          --help              this text

        EXAMPLES
          # Prove the rig without transmitting: is the noise flat and sharply bounded?
          flex-iq-noise --freq 14.200 --bw 3k --dry-run --csv band.csv

          # 10 s of 3 kHz-wide noise occupying 14.197 - 14.200
          flex-iq-noise --radio 10.45.0.76 --freq 14.200 --bw 3k --seconds 10

          # The widest the radio will pass: 10 kHz, one-sided
          flex-iq-noise --radio 10.45.0.76 --freq 14.200 --bw 10k --seconds 20

          # Let the library place the band instead: same spectrum, none of the mechanics
          flex-iq-noise --radio 10.45.0.76 --freq 14.197 --bw 3k --placed --reference loweredge

          # Walk the passband by hand and find its edges
          flex-iq-noise --radio 10.45.0.76 --freq 14.200 --explore -500

          # Re-derive the whole underlying_mode table with an unambiguous probe
          flex-iq-noise --radio 10.45.0.76 --freq 14.200 --sweep --seconds 10

        DIAGNOSING A WRONG-LOOKING BAND
          Noise centred on the carrier is symmetric, and so is a two-tone probe at +/-f. Neither
          can tell you *why* a band looks wrong, because both contain the tone that would explain
          either answer. Only a SINGLE tone, off to one side, decides it. Send one at -3 kHz:

            flex-iq-noise --radio <ip> --freq 14.200 --tone -3k --seconds 10

          and note where it lands:

            14.197 (below the carrier)   this mode is UPRIGHT — usable for a modulated signal
            14.203 (above the carrier)   this mode MIRRORS — a modulated signal goes out inverted
            nothing at all               the transmit filter is cutting it, or the mode discards Q

          Then confirm with the opposite sign:

            flex-iq-noise --radio <ip> --freq 14.200 --tone 3k --seconds 10

          which should be SILENT in every mode. Only the negative half of the baseband is ever
          transmitted; positive-frequency content reaches the air in no mode at all. If you see
          the +3 kHz tone anywhere, the model in this tool is wrong — say so.

          Measured on a FLEX-6500 (fw 4.1.5): RAW, LSB and DIGL are upright; IQ, USB and DIGU
          mirror; AM and FM discard Q and modulate the I channel alone (carrier plus both
          sidebands). --sweep re-derives that whole table in one run.

        WHY A BAND COMES OUT NARROWER THAN ASKED FOR
          Two separate limits, both silent:
            - Half of it was above DC, so was never transmitted. Use.
            - The radio's transmit filter truncated the rest. It defaults to 3 kHz and clamps at
              10 kHz; this tool sets it from --bw, and warns when the clamp bites.
          Together those cap the usable width at about 10 kHz, one-sided.
        """;
}

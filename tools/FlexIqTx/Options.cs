using System.Globalization;
using M0LTE.Flex;

namespace M0LTE.Flex.Tools.IqTx;

/// <summary>The parsed command line for the stdin IQ transmitter.</summary>
internal sealed record Options
{
    public string Radio { get; init; } = "discover";

    /// <summary>Where the signal goes, in MHz — its centre or lower edge per <see cref="Reference"/>.</summary>
    public double FreqMhz { get; init; } = 14.100000;

    /// <summary>How wide the caller's signal is, in Hz. Sets both the placement and the radio's
    /// transmit filter, so it must be declared rather than guessed.</summary>
    public double BandwidthHz { get; init; } = 3000;

    public IqBandReference Reference { get; init; } = IqBandReference.Centre;

    public IqFormat Format { get; init; } = IqFormat.Cf32;

    public double PowerWatts { get; init; } = 5;

    public string Antenna { get; init; } = "ANT1";

    /// <summary>Linear scale applied to every incoming sample. The escape hatch for a stream whose
    /// level is not already right for the ±1.0 full-scale convention.</summary>
    public double Gain { get; init; } = 1.0;

    public double? MaxSeconds { get; init; }

    /// <summary>
    /// Transmit the samples exactly as supplied, at <see cref="FreqMhz"/>, with no placement or
    /// frequency shift.
    /// </summary>
    /// <remarks>
    /// <para>For a stream already positioned for the radio: a capture being replayed verbatim, or a
    /// probe that deliberately tests the radio's sideband behaviour rather than using it. The caller
    /// then owns the consequences — content above DC is not transmitted, and the tool says so rather
    /// than dropping it silently.</para>
    /// <para>Named <c>direct</c> rather than <c>raw</c> because <c>RAW</c> is also an
    /// <c>underlying_mode</c>, and the two have nothing to do with each other.</para>
    /// </remarks>
    public bool Direct { get; init; }

    /// <summary>Read from this file instead of stdin. A SigMF <c>.sigmf-meta</c> sidecar beside it is
    /// picked up automatically, and its datatype and sample rate are believed over the flags.</summary>
    public string? InputPath { get; init; }

    /// <summary>True once <c>--format</c> was given explicitly, so a sidecar disagreeing with it can
    /// be reported rather than silently overriding.</summary>
    public bool FormatSpecified { get; init; }

    public bool DryRun { get; init; }

    public bool Verbose { get; init; }

    public double LowMhz => Reference == IqBandReference.LowerEdge
        ? FreqMhz
        : FreqMhz - (BandwidthHz / 2e6);

    public double HighMhz => LowMhz + (BandwidthHz / 1e6);

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string Value()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{key} needs a value");
                }

                return args[++i];
            }

            switch (key)
            {
                case "--radio": options = options with { Radio = Value() }; break;
                case "--freq": options = options with { FreqMhz = ParseHz(Value(), 1e6) / 1e6 }; break;
                case "--bw": options = options with { BandwidthHz = ParseHz(Value(), 1) }; break;
                case "--format": options = options with { Format = IqReader.Parse(Value()), FormatSpecified = true }; break;
                case "--in": options = options with { InputPath = Value() }; break;
                case "--power": options = options with { PowerWatts = ParseDouble(Value()) }; break;
                case "--ant": options = options with { Antenna = Value() }; break;
                case "--gain": options = options with { Gain = ParseDouble(Value()) }; break;
                case "--max-seconds": options = options with { MaxSeconds = ParseDouble(Value()) }; break;
                case "--direct": options = options with { Direct = true }; break;

                // Renamed, and worth saying so: RAW is also an underlying_mode, so the old name
                // read as though it selected one.
                case "--raw":
                    throw new ArgumentException("--raw is now --direct (RAW is also an underlying_mode, "
                        + "and the two are unrelated)");
                case "--dry-run": options = options with { DryRun = true }; break;
                case "--verbose": options = options with { Verbose = true }; break;
                case "--reference":
                    options = options with
                    {
                        Reference = Value().ToLowerInvariant() switch
                        {
                            "centre" or "center" => IqBandReference.Centre,
                            "loweredge" or "lower-edge" or "edge" => IqBandReference.LowerEdge,
                            var other => throw new ArgumentException(
                                $"--reference must be centre or loweredge (got '{other}')"),
                        },
                    };
                    break;
                default: throw new ArgumentException($"unknown option {key}");
            }
        }

        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (FreqMhz is <= 0 or > 100)
        {
            throw new ArgumentException($"--freq {FreqMhz} MHz is outside the 6000-series range");
        }

        if (BandwidthHz <= 0)
        {
            throw new ArgumentException("--bw must be positive — it declares how wide your signal is");
        }

        // The radio's own ceiling; the library re-checks against what the filter actually reached.
        if (BandwidthHz > FlexWaveformOptions.MaxTransmitFilterHighHz)
        {
            throw new ArgumentException(
                $"--bw {BandwidthHz:F0} Hz exceeds the radio's {FlexWaveformOptions.MaxTransmitFilterHighHz} Hz "
                + "transmit filter ceiling; the band would be truncated on air");
        }

        if (PowerWatts is < 0 or > 100)
        {
            throw new ArgumentException($"--power {PowerWatts} W is outside 0..100");
        }

        if (Gain <= 0)
        {
            throw new ArgumentException("--gain must be positive");
        }

        if (MaxSeconds is <= 0)
        {
            throw new ArgumentException("--max-seconds must be positive");
        }
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new ArgumentException($"'{text}' is not a number");

    /// <summary>Parses a frequency, honouring a k/M/Hz suffix. A bare number takes
    /// <paramref name="bareUnitHz"/> — MHz for --freq, Hz for --bw.</summary>
    private static double ParseHz(string text, double bareUnitHz)
    {
        string trimmed = text.Trim();
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

        return ParseDouble(trimmed) * multiplier;
    }
}

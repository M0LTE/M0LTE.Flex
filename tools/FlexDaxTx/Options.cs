using System.Globalization;

namespace M0LTE.Flex.Tools.DaxTx;

/// <summary>The parsed command line for the DAX audio transmitter.</summary>
internal sealed record Options
{
    public string Radio { get; init; } = "discover";

    /// <summary>The slice's dial frequency in MHz. Audio lands relative to this per the slice mode —
    /// above it for USB/DIGU, below for LSB/DIGL.</summary>
    public double FreqMhz { get; init; } = 14.100000;

    /// <summary>Slice mode. DIGU is the usual choice for data: upper sideband, no speech processing
    /// implied by the name.</summary>
    public string Mode { get; init; } = "DIGU";

    public string Antenna { get; init; } = "ANT1";

    /// <summary>The DAX channel to claim. A running SmartSDR takes channel 1.</summary>
    public string DaxChannel { get; init; } = "1";

    /// <summary>Wire rate: 24000 (reduced-bandwidth s16) or 48000 (full-bandwidth float32). The
    /// stream on stdin must already be at this rate.</summary>
    public int RateHz { get; init; } = 48000;

    public AudioFormat Format { get; init; } = AudioFormat.F32;

    public double PowerWatts { get; init; } = 5;

    public double Gain { get; init; } = 1.0;

    /// <summary>When set, widen or narrow the radio's transmit filter to this many Hz. That filter,
    /// not the slice, is what limits transmitted audio bandwidth. Null leaves it as found.</summary>
    public int? TransmitFilterHighHz { get; init; }

    public double? MaxSeconds { get; init; }

    public string? InputPath { get; init; }

    /// <summary>Set up and probe, but never key — explore the radio's state with no RF.</summary>
    public bool NoTx { get; init; }

    /// <summary>Raw commands sent after setup, with their error codes printed.</summary>
    public string[] PostCommands { get; init; } = [];

    public bool DryRun { get; init; }

    public bool Verbose { get; init; }

    /// <summary>The DAX transport implied by <see cref="RateHz"/>.</summary>
    public DaxStreamFormat StreamFormat =>
        RateHz == 24000 ? DaxStreamFormat.ReducedBandwidth : DaxStreamFormat.FullBandwidth;

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
                case "--freq": options = options with { FreqMhz = ParseFrequencyMhz(Value()) }; break;
                case "--mode": options = options with { Mode = Value().ToUpperInvariant() }; break;
                case "--ant": options = options with { Antenna = Value() }; break;
                case "--dax-channel": options = options with { DaxChannel = Value() }; break;
                case "--rate": options = options with { RateHz = (int)ParseDouble(Value()) }; break;
                case "--format": options = options with { Format = AudioReader.Parse(Value()) }; break;
                case "--power": options = options with { PowerWatts = ParseDouble(Value()) }; break;
                case "--gain": options = options with { Gain = ParseDouble(Value()) }; break;
                case "--bw": options = options with { TransmitFilterHighHz = (int)ParseDouble(Value()) }; break;
                case "--max-seconds": options = options with { MaxSeconds = ParseDouble(Value()) }; break;
                case "--in": options = options with { InputPath = Value() }; break;
                case "--no-tx": options = options with { NoTx = true }; break;
                case "--post": options = options with { PostCommands = [.. options.PostCommands, Value()] }; break;
                case "--dry-run": options = options with { DryRun = true }; break;
                case "--verbose": options = options with { Verbose = true }; break;
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

        // These are the only two DAX transports the radio offers; anything else would have to be
        // resampled, and guessing which way would corrupt the audio silently.
        if (RateHz is not (24000 or 48000))
        {
            throw new ArgumentException($"--rate must be 24000 or 48000 (got {RateHz})");
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

    /// <summary>Bare numbers are MHz (the Flex convention); k/M/Hz suffixes win.</summary>
    internal static double ParseFrequencyMhz(string text)
    {
        string trimmed = text.Trim();
        bool unitStated = false;
        if (trimmed.EndsWith("hz", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2].Trim();
            unitStated = true;
        }

        double multiplier = unitStated ? 1 : 1e6;
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

        return ParseDouble(trimmed) * multiplier / 1e6;
    }
}

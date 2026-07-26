using System.Globalization;

namespace M0LTE.Flex.Tools.IqGen;

/// <summary>
/// Emits complex-baseband test signals for a FlexRadio waveform, to stdout or to a file — the
/// generator half of a <c>flex-iq-gen … | flex-iq-tx …</c> pipeline.
/// </summary>
/// <remarks>
/// Everything is produced at 24 kHz complex (the waveform rate) and, unless told otherwise, placed
/// entirely <b>below DC</b>, because that is the only half a Flex waveform transmits.
/// </remarks>
internal static class Program
{
    private const string Usage = """
        flex-iq-gen — complex-baseband test signals for a FlexRadio waveform, on stdout.

        USAGE
          flex-iq-gen <signal> [options] [> file.cf32]
          flex-iq-gen --corpus <dir>

        All signals are 24 kHz complex — the waveform rate — and sit BELOW DC by default, because
        that is the only half a Flex waveform transmits. Pipe straight into the transmitter:

          flex-iq-gen tone | flex-iq-tx --radio 10.45.0.76 --freq 14.200

        SIGNALS
          tone              one complex tone (default -3000 Hz). Asymmetric, so where it lands
                            identifies the radio's sideband and orientation outright
          twotone           two tones at unequal offsets (-2000 and -5000 Hz). Their order
                            reverses under a mirrored path
          noise             flat band-limited noise (default -3000..0 Hz)
          chirp             linear sweep (default -10000 -> 0 Hz). The sweep line stops where the
                            radio's passband ends, mapping an edge in one pass
          staircase         noise in stepped levels across the band — the orientation check: the
                            steps run the other way if the path mirrors
          qpsk              RRC-filtered QPSK, a real modulated signal. Mirroring or companding
                            leaves it looking plausible on a spectrum display but undecodable

        OPTIONS
          --seconds <n>     duration (default: 5)
          --offset <hz>     tone/qpsk centre, or the band's upper edge for noise-like signals
          --bw <hz>         width for noise/staircase/chirp (default: 3000)
          --rms <0..1>      per-component RMS, +/-1.0 full scale (default: 0.15)
          --format <f>      cf32 (interleaved float32 LE, GNU Radio .cfile — default) or cs16
          --seed <n>        PRNG seed, for a byte-identical repeat (default: 1)
          --rate <hz>       sample rate (default: 24000, the waveform rate). Use 48000 for the
                            DAX full-bandwidth audio path — generating at the wrong rate plays
                            back at the wrong speed and pitch, sounding like a signal throughout
          --out <path>      write here instead of stdout
          --corpus <dir>    write the whole corpus, plus a README describing what each one proves
          --real            emit MONO REAL audio (the I channel alone) instead of complex I/Q,
                            for the DAX audio path. A complex tone at +/-f becomes a real tone
                            at |f|, since a real audio path cannot carry the sign
          --sigmf           also write SigMF .sigmf-meta sidecars, so the files carry their own
                            sample rate and type instead of relying on the reader knowing them
                            (needs --out or --corpus; a pipe has nowhere to put a sidecar)
          --help            this text
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || Array.Exists(args, a => a is "--help" or "-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            return Run(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"i/o error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string signal = args[0].StartsWith("--", StringComparison.Ordinal) ? "" : args[0];
        double seconds = 5;
        double bandwidth = 3000;
        double? offset = null;
        double rms = 0.15;
        int seed = 1;
        IqFormat format = IqFormat.Cf32;
        string? outPath = null;
        string? corpusDir = null;
        bool sigmf = false;
        bool real = false;
        int rate = Signals.SampleRate;

        for (int i = signal.Length > 0 ? 1 : 0; i < args.Length; i++)
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
                case "--seconds": seconds = ParseDouble(Value()); break;
                case "--bw": bandwidth = ParseHz(Value()); break;
                case "--offset": offset = ParseHz(Value()); break;
                case "--rms": rms = ParseDouble(Value()); break;
                case "--seed": seed = (int)ParseDouble(Value()); break;
                case "--format": format = IqFormatIo.Parse(Value()); break;
                case "--out": outPath = Value(); break;
                case "--corpus": corpusDir = Value(); break;
                case "--sigmf": sigmf = true; break;
                case "--real": real = true; break;
                case "--rate": rate = (int)ParseDouble(Value()); break;
                default: throw new ArgumentException($"unknown option {key}");
            }
        }

        if (corpusDir is not null)
        {
            return Corpus.Write(corpusDir, format, sigmf);
        }

        if (signal.Length == 0)
        {
            throw new ArgumentException("name a signal (tone, twotone, noise, chirp, staircase, qpsk)");
        }

        float[] iq = Generate(signal, seconds, bandwidth, offset, rms, seed, rate);

        var scratch = new byte[8192];
        using (Stream destination = outPath is null
            ? Console.OpenStandardOutput()
            : new FileStream(outPath, FileMode.Create, FileAccess.Write))
        {
            if (real)
            {
                IqFormatIo.WriteReal(destination, iq, format, scratch);
            }
            else
            {
                IqFormatIo.Write(destination, iq, format, scratch);
            }
        }

        if (sigmf && outPath is not null)
        {
            SigMf.WriteMeta(outPath, format, Signals.SampleRate, $"flex-iq-gen {signal}");
        }

        Console.Error.WriteLine(
            $"{signal}: {iq.Length / 2:N0} {(real ? "mono real" : "complex")} samples, "
            + $"{iq.Length / 2.0 / rate:F2} s at {rate} Hz, "
            + $"{format.ToString().ToLowerInvariant()}{(real ? " (I channel only)" : "")}");
        return 0;
    }

    /// <summary>Builds one named signal. Offsets default below DC — the half that transmits.</summary>
    internal static float[] Generate(
        string signal, double seconds, double bandwidth, double? offset, double rms, int seed,
        int rate = Signals.SampleRate) => signal switch
    {
        "tone" => Signals.Tone(offset ?? -3000, seconds, rms * Math.Sqrt(2), rate),
        "twotone" => Signals.TwoTone(offset ?? -2000, (offset ?? -2000) - 3000, seconds, rms * Math.Sqrt(2), rate),
        "noise" => Signals.Noise((offset ?? 0) - bandwidth, offset ?? 0, seconds, rms, seed, rate),
        "chirp" => Signals.Chirp((offset ?? 0) - bandwidth, offset ?? 0, seconds, rms * Math.Sqrt(2), rate),
        "staircase" => Signals.Staircase((offset ?? 0) - bandwidth, offset ?? 0, 5, 6, seconds, rms, seed, rate),
        "qpsk" => Signals.Qpsk(2400, 0.35, offset ?? -2000, seconds, rms, seed, rate),
        _ => throw new ArgumentException(
            $"unknown signal '{signal}' (tone, twotone, noise, chirp, staircase, qpsk)"),
    };

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new ArgumentException($"'{text}' is not a number");

    internal static double ParseHz(string text)
    {
        string trimmed = text.Trim();
        double multiplier = 1;
        if (trimmed.EndsWith('k') || trimmed.EndsWith('K'))
        {
            multiplier = 1000;
            trimmed = trimmed[..^1];
        }

        return ParseDouble(trimmed) * multiplier;
    }
}

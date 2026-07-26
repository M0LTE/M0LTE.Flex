using System.Buffers.Binary;
using System.Globalization;

namespace M0LTE.Flex.Tools.IqPaint;

/// <summary>
/// Paints a picture onto a waterfall, as mono audio for the DAX path or complex IQ for the waveform
/// path.
/// </summary>
internal static class Program
{
    private const string Usage = """
        flex-iq-paint — turn a PNG into a signal that draws it on a waterfall.

        USAGE
          flex-iq-paint <image.png> --out <file> [--iq] [options]

        Image columns become frequencies and rows become time. Two flavours:

          # Mono audio for the DAX path (flex-dax-tx)
          flex-iq-paint logo.png --out logo.f32 --rate 48000 --lo 300 --hi 2700
          flex-dax-tx --radio <ip> --freq 14.100 --rate 48000 --in logo.f32

          # Complex IQ for the waveform path (flex-iq-tx), which has room for more detail
          flex-iq-paint logo.png --out logo.cf32 --iq --rate 24000 --lo 300 --hi 9500 --bins 384
          flex-iq-tx --radio <ip> --freq 14.1905 --bw 9500 --reference loweredge --in logo.cf32

        The picture is ordinary complex baseband at the frequencies you ask for, running upward
        from --lo. Which half of the spectrum the radio actually transmits is the library's
        problem, not the picture's: flex-iq-tx places the band for you. Note there is no --raw
        above — that is the escape hatch for replaying a capture verbatim, not the normal path.

        OPTIONS
          --out <path>      where to write the samples (required)
          --iq              emit interleaved complex I/Q (cf32) instead of mono real audio,
                            for the waveform path via flex-iq-tx --reference loweredge
          --lo <hz>         lowest frequency in the picture (default: 300)
          --hi <hz>         highest (default: 2700 — raise to 9500 with a 10 kHz transmit filter)
          --bins <n>        frequency bins, i.e. image width after resize (default: 192)
          --lines <n>       time steps, i.e. image height after resize (default: 96)
          --line-ms <ms>    duration of each time step (default: 80). Must be at least 1/spacing
                            or the waterfall cannot resolve adjacent bins
          --rate <hz>       sample rate: 48000 for DAX full-bandwidth, 24000 for the waveform
                            path or DAX reduced-bandwidth (default: 48000)
          --peak <0..1>     peak amplitude (default: 0.85). Limited by peak, not RMS: hundreds of
                            tones have a ~20 dB crest factor and one clipped sample streaks the
                            whole picture
          --no-invert       treat bright pixels as strong tones (default is dark ink = strong,
                            which suits a logo on white)
          --newest-at-bottom  transmit top row first, if your waterfall scrolls upward
          --seed <n>        phase seed, for a byte-identical repeat (default: 1)
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
        string image = args[0];
        string? output = null;
        bool iq = false;
        int bins = 192;
        int lines = 96;
        var options = new PaintOptions();

        for (int i = 1; i < args.Length; i++)
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
                case "--out": output = Value(); break;
                case "--iq": iq = true; break;
                case "--bins": bins = (int)Number(Value()); break;
                case "--lines": lines = (int)Number(Value()); break;
                case "--lo": options = options with { LowHz = Number(Value()) }; break;
                case "--hi": options = options with { HighHz = Number(Value()) }; break;
                case "--line-ms": options = options with { LineMs = Number(Value()) }; break;
                case "--rate": options = options with { RateHz = (int)Number(Value()) }; break;
                case "--peak": options = options with { Peak = Number(Value()) }; break;
                case "--seed": options = options with { Seed = (int)Number(Value()) }; break;
                case "--no-invert": options = options with { Invert = false }; break;
                case "--newest-at-bottom": options = options with { NewestAtTop = false }; break;
                default: throw new ArgumentException($"unknown option {key}");
            }
        }

        if (output is null)
        {
            throw new ArgumentException("--out is required");
        }

        if (bins < 2 || lines < 2)
        {
            throw new ArgumentException("--bins and --lines must be at least 2");
        }

        if (options.HighHz <= options.LowHz)
        {
            throw new ArgumentException("--hi must exceed --lo");
        }

        double spacing = (options.HighHz - options.LowHz) / (bins - 1);
        double needMs = 1000 / spacing;
        if (options.LineMs < needMs)
        {
            // Not fatal — the picture still transmits, it just smears. Say so rather than let it
            // look like the radio blurring it.
            Console.Error.WriteLine(
                $"warning: {spacing:F1} Hz bin spacing needs at least {needMs:F0} ms per line to be "
                + $"resolvable; --line-ms is {options.LineMs:F0}, so the picture will smear");
        }

        double[,] source = Png.ReadGreyscale(image);
        double[,] resized = Resize(source, bins, lines);
        float[] samples = Painter.Render(resized, options, iq);

        WriteFloats(output, samples);

        int count = iq ? samples.Length / 2 : samples.Length;
        Console.Error.WriteLine($"  {image} ({source.GetLength(1)}x{source.GetLength(0)}) -> {output}");
        Console.Error.WriteLine(
            $"  {bins} bins over {options.LowHz:F0}..{options.HighHz:F0} Hz "
            + $"({spacing:F1} Hz apart), {lines} lines x {options.LineMs:F0} ms");
        Console.Error.WriteLine(
            $"  {count:N0} {(iq ? "complex" : "mono")} samples, {count / (double)options.RateHz:F1} s "
            + $"at {options.RateHz} Hz, peak {options.Peak:F2}");
        return 0;
    }

    /// <summary>Box-samples the image down to the requested grid.</summary>
    private static double[,] Resize(double[,] source, int width, int height)
    {
        int sh = source.GetLength(0);
        int sw = source.GetLength(1);
        var output = new double[height, width];

        for (int y = 0; y < height; y++)
        {
            int y0 = y * sh / height;
            int y1 = Math.Max(y0 + 1, (y + 1) * sh / height);
            for (int x = 0; x < width; x++)
            {
                int x0 = x * sw / width;
                int x1 = Math.Max(x0 + 1, (x + 1) * sw / width);

                double sum = 0;
                int n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    for (int sx = x0; sx < x1; sx++)
                    {
                        sum += source[sy, sx];
                        n++;
                    }
                }

                output[y, x] = sum / Math.Max(n, 1);
            }
        }

        return output;
    }

    private static void WriteFloats(string path, float[] samples)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        var scratch = new byte[8192];
        int at = 0;
        while (at < samples.Length)
        {
            int take = Math.Min(scratch.Length / 4, samples.Length - at);
            for (int i = 0; i < take; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(scratch.AsSpan(i * 4, 4), samples[at + i]);
            }

            file.Write(scratch, 0, take * 4);
            at += take;
        }
    }

    private static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new ArgumentException($"'{text}' is not a number");
}

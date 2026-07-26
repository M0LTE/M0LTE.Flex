using M0LTE.Flex.Tools.IqPaint;

namespace M0LTE.Flex.Tools.Tests;

/// <summary>
/// The waterfall painter and its PNG reader. The picture is the test oracle here: if the synthesis
/// drifts, the image stops being legible, and nothing else would notice.
/// </summary>
public sealed class PaintTests
{
    private static double[,] Checkerboard(int width, int height)
    {
        var image = new double[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[y, x] = (x + y) % 2 == 0 ? 0 : 1;     // 0 = ink, inverted to a strong tone
            }
        }

        return image;
    }

    private static double PowerAt(float[] samples, double hz, int rate, bool complex)
    {
        double re = 0;
        double im = 0;
        int count = complex ? samples.Length / 2 : samples.Length;
        for (int n = 0; n < count; n++)
        {
            (double sin, double cos) = Math.SinCos(-2 * Math.PI * hz * n / rate);
            double i = complex ? samples[2 * n] : samples[n];
            double q = complex ? samples[(2 * n) + 1] : 0;
            re += (i * cos) - (q * sin);
            im += (i * sin) + (q * cos);
        }

        return Math.Sqrt((re * re) + (im * im)) / count;
    }

    [Fact]
    public void A_real_png_decodes_to_the_right_shape_and_range()
    {
        // A palette PNG built here rather than read from disk, so the test is self-contained.
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
        WriteGreyPng(path, 8, 4);

        double[,] image = Png.ReadGreyscale(path);
        image.GetLength(0).Should().Be(4);
        image.GetLength(1).Should().Be(8);

        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (double v in image)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            v.Should().BeInRange(0, 1);
        }

        min.Should().BeLessThan(0.2, "the gradient starts dark");
        max.Should().BeGreaterThan(0.8, "and ends light");
    }

    /// <summary>Writes a minimal 8-bit greyscale PNG with a left-to-right gradient.</summary>
    private static void WriteGreyPng(string path, int width, int height)
    {
        var raw = new byte[height * (width + 1)];
        for (int y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = 0;                        // filter: none
            for (int x = 0; x < width; x++)
            {
                raw[(y * (width + 1)) + 1 + x] = (byte)(x * 255 / (width - 1));
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(
            compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(file, "IHDR", [
            .. System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(width).ToBytes(),
            .. System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(height).ToBytes(),
            8, 0, 0, 0, 0]);
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        stream.Write(stackalloc byte[4]);                    // CRC is not checked by the reader
    }

    [Fact]
    public void A_mono_render_is_one_sample_per_step_and_never_clips()
    {
        var options = new PaintOptions { RateHz = 24000, LineMs = 50, LowHz = 500, HighHz = 2500 };
        float[] mono = Painter.Render(Checkerboard(16, 8), options, complex: false);

        mono.Length.Should().Be(8 * (int)(24000 * 0.050));
        mono.Max(Math.Abs).Should().BeApproximately(0.85f, 0.001f, "peak-limited, so it cannot clip");
    }

    [Fact]
    public void An_iq_render_puts_the_whole_picture_below_dc()
    {
        // The half above DC is transmitted by no underlying_mode, so a picture placed there keys the
        // radio and paints nothing. This is the one that would waste a bench session.
        var options = new PaintOptions { RateHz = 24000, LineMs = 50, LowHz = 1000, HighHz = 4000 };
        float[] iq = Painter.Render(Checkerboard(16, 8), options, complex: true);

        iq.Length.Should().Be(2 * 8 * (int)(24000 * 0.050));

        double below = 0;
        double above = 0;
        for (double hz = 500; hz <= 5000; hz += 250)
        {
            below += PowerAt(iq, -hz, options.RateHz, complex: true);
            above += PowerAt(iq, hz, options.RateHz, complex: true);
        }

        above.Should().BeLessThan(below * 0.02, "the picture must sit below DC");
    }

    [Fact]
    public void The_picture_lands_between_the_requested_frequencies()
    {
        var options = new PaintOptions { RateHz = 24000, LineMs = 60, LowHz = 1000, HighHz = 3000 };
        var solid = new double[8, 16];                       // all ink after inversion
        float[] mono = Painter.Render(solid, options, complex: false);

        // Summed across the band rather than probed at one frequency: the bins land wherever the
        // requested span divides, so a single probe frequency usually falls between two of them.
        double inBand = BandPower(mono, 1000, 3000, options.RateHz);
        double above = BandPower(mono, 3500, 6000, options.RateHz);
        double below = BandPower(mono, 100, 800, options.RateHz);

        inBand.Should().BeGreaterThan(above * 50);
        inBand.Should().BeGreaterThan(below * 50);
    }

    private static double BandPower(float[] samples, double lowHz, double highHz, int rate)
    {
        double total = 0;
        for (double hz = lowHz; hz <= highHz; hz += 25)
        {
            double p = PowerAt(samples, hz, rate, complex: false);
            total += p * p;
        }

        return total;
    }

    [Fact]
    public void Inversion_decides_which_pixels_become_tones()
    {
        var options = new PaintOptions { RateHz = 24000, LineMs = 40, LowHz = 1000, HighHz = 2000 };
        var white = new double[4, 8];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                white[y, x] = 1;                             // blank paper
            }
        }

        // Inverted (the default, for dark ink on white), blank paper should be near-silent — the
        // renderer normalises to a peak, so "silent" means it never rose above the noise of the
        // normalisation, not that the buffer is zero.
        float[] inverted = Painter.Render(white, options with { Invert = true }, complex: false);
        float[] asIs = Painter.Render(white, options with { Invert = false }, complex: false);

        inverted.Max(Math.Abs).Should().BeLessThan(1e-6f, "white paper carries no ink");
        asIs.Max(Math.Abs).Should().BeApproximately(0.85f, 0.001f);
    }

    [Fact]
    public void The_same_seed_paints_byte_identically()
    {
        var options = new PaintOptions { RateHz = 24000, LineMs = 40 };
        double[,] image = Checkerboard(8, 4);

        Painter.Render(image, options, complex: false)
            .Should().Equal(Painter.Render(image, options, complex: false));
        Painter.Render(image, options with { Seed = 2 }, complex: false)
            .Should().NotEqual(Painter.Render(image, options, complex: false));
    }
}

internal static class IntExtensions
{
    public static byte[] ToBytes(this int value) => BitConverter.GetBytes(value);
}

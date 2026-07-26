using System.Buffers.Binary;
using System.IO.Compression;

namespace M0LTE.Flex.Tools.IqPaint;

/// <summary>
/// A minimal PNG reader, producing greyscale in 0..1.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from a package: the only thing needed here is "turn an image into
/// a brightness grid", and the alternatives are either a licence question in an AGPL repo
/// (ImageSharp's newer split licence) or a platform one (System.Drawing on Linux). PNG's own
/// compression is plain zlib, which the framework already has.
/// Supports 8-bit greyscale/RGB/RGBA/grey+alpha and 1/2/4/8-bit palette, non-interlaced — which
/// covers anything a logo or screenshot will be saved as.
/// </remarks>
internal static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static double[,] ReadGreyscale(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new ArgumentException($"{path} is not a PNG");
        }

        int width = 0, height = 0, bitDepth = 0, colourType = 0;
        byte[]? palette = null;
        using var idat = new MemoryStream();

        int at = 8;
        while (at + 8 <= file.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at, 4));
            string type = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
            int dataAt = at + 8;

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(dataAt, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(dataAt + 4, 4));
                    bitDepth = file[dataAt + 8];
                    colourType = file[dataAt + 9];
                    if (file[dataAt + 12] != 0)
                    {
                        throw new ArgumentException($"{path}: interlaced PNG is not supported");
                    }

                    break;

                case "PLTE":
                    palette = file[dataAt..(dataAt + length)];
                    break;

                case "IDAT":
                    idat.Write(file, dataAt, length);
                    break;
            }

            if (type == "IEND")
            {
                break;
            }

            at = dataAt + length + 4;                      // skip the CRC
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"{path}: no usable IHDR");
        }

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);
        byte[] bytes = raw.ToArray();

        int channels = colourType switch
        {
            0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4,
            _ => throw new ArgumentException($"{path}: colour type {colourType} is not supported"),
        };

        if (bitDepth != 8 && !(colourType == 3 && bitDepth is 1 or 2 or 4))
        {
            throw new ArgumentException($"{path}: bit depth {bitDepth} is not supported");
        }

        int bitsPerPixel = channels * bitDepth;
        int stride = ((width * bitsPerPixel) + 7) / 8;
        int filterStep = Math.Max(1, bitsPerPixel / 8);

        var image = new double[height, width];
        var previous = new byte[stride];
        var current = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            int rowAt = y * (stride + 1);
            if (rowAt + stride >= bytes.Length + 1 && rowAt + 1 + stride > bytes.Length)
            {
                throw new ArgumentException($"{path}: truncated image data");
            }

            byte filter = bytes[rowAt];
            Array.Copy(bytes, rowAt + 1, current, 0, stride);
            Unfilter(filter, current, previous, filterStep);

            for (int x = 0; x < width; x++)
            {
                image[y, x] = Sample(current, x, colourType, bitDepth, channels, palette);
            }

            (previous, current) = (current, previous);
        }

        return image;
    }

    /// <summary>Reverses a scanline's filter, in place. PNG filters are defined against the
    /// already-reconstructed bytes to their left and above.</summary>
    private static void Unfilter(byte filter, byte[] line, byte[] previous, int step)
    {
        for (int i = 0; i < line.Length; i++)
        {
            int a = i >= step ? line[i - step] : 0;        // left
            int b = previous[i];                            // above
            int c = i >= step ? previous[i - step] : 0;     // above-left

            line[i] = filter switch
            {
                0 => line[i],
                1 => (byte)(line[i] + a),
                2 => (byte)(line[i] + b),
                3 => (byte)(line[i] + ((a + b) / 2)),
                4 => (byte)(line[i] + Paeth(a, b, c)),
                _ => throw new ArgumentException($"unknown PNG filter {filter}"),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>One pixel as luminance in 0..1.</summary>
    private static double Sample(byte[] line, int x, int colourType, int bitDepth, int channels, byte[]? palette)
    {
        if (colourType == 3)
        {
            int index = ReadIndex(line, x, bitDepth);
            if (palette is null || (index * 3) + 2 >= palette.Length)
            {
                return 0;
            }

            return Luminance(palette[index * 3], palette[(index * 3) + 1], palette[(index * 3) + 2]);
        }

        int at = x * channels;
        return colourType switch
        {
            0 or 4 => line[at] / 255.0,                     // grey, alpha ignored
            _ => Luminance(line[at], line[at + 1], line[at + 2]),
        };
    }

    private static int ReadIndex(byte[] line, int x, int bitDepth)
    {
        if (bitDepth == 8)
        {
            return line[x];
        }

        int perByte = 8 / bitDepth;
        int shift = 8 - (bitDepth * ((x % perByte) + 1));
        return (line[x / perByte] >> shift) & ((1 << bitDepth) - 1);
    }

    // Rec. 601 luma: a waterfall shows power, and the eye's sense of "how dark is this ink" tracks
    // luma far better than a flat channel average.
    private static double Luminance(byte r, byte g, byte b) =>
        ((0.299 * r) + (0.587 * g) + (0.114 * b)) / 255.0;
}

using System.Buffers.Binary;

namespace M0LTE.Flex.Tools.IqGen;

/// <summary>On-the-wire sample formats for an IQ stream, shared with <c>flex-iq-tx</c>.</summary>
internal enum IqFormat
{
    /// <summary>Interleaved little-endian <c>float32</c> I/Q — GNU Radio's <c>complex64</c>, the
    /// format of a <c>.cfile</c>, and what almost every SDR tool reads and writes. The default.</summary>
    Cf32,

    /// <summary>Interleaved little-endian <c>int16</c> I/Q, full scale ±32767 — what most SDR
    /// hardware captures natively.</summary>
    Cs16,
}

internal static class IqFormatIo
{
    public static int BytesPerSample(IqFormat format) => format == IqFormat.Cf32 ? 8 : 4;

    /// <summary>
    /// Drops Q and writes the real channel alone — mono audio, for the DAX path.
    /// </summary>
    /// <remarks>
    /// DAX carries real audio into a slice, not complex baseband. Feeding it interleaved I/Q would
    /// transmit Q as though it were more audio, at double the intended rate, and sound plausible
    /// enough to miss. A complex tone at ±f becomes a real tone at |f|; the sign stops meaning
    /// anything, which is the honest reflection of what a real audio path can carry.
    /// </remarks>
    public static void WriteReal(Stream destination, ReadOnlySpan<float> interleavedIq, IqFormat format, Span<byte> scratch)
    {
        int pairs = interleavedIq.Length / 2;
        var mono = new float[pairs];
        for (int n = 0; n < pairs; n++)
        {
            mono[n] = interleavedIq[2 * n];
        }

        WriteMono(destination, mono, format, scratch);
    }

    /// <summary>Writes mono samples in the scalar form of <paramref name="format"/> — one value per
    /// sample rather than two.</summary>
    public static void WriteMono(Stream destination, ReadOnlySpan<float> mono, IqFormat format, Span<byte> scratch)
    {
        int perSample = format == IqFormat.Cf32 ? 4 : 2;
        int offset = 0;
        while (offset < mono.Length)
        {
            int take = Math.Min(scratch.Length / perSample, mono.Length - offset);
            for (int i = 0; i < take; i++)
            {
                float value = mono[offset + i];
                if (format == IqFormat.Cf32)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(scratch[(i * 4)..], value);
                }
                else
                {
                    int scaled = (int)Math.Round(Math.Clamp(value, -1f, 1f) * 32767f);
                    BinaryPrimitives.WriteInt16LittleEndian(scratch[(i * 2)..], (short)scaled);
                }
            }

            destination.Write(scratch[..(take * perSample)]);
            offset += take;
        }
    }

    /// <summary>Writes interleaved I/Q floats (±1.0 full scale) in <paramref name="format"/>.</summary>
    public static void Write(Stream destination, ReadOnlySpan<float> interleavedIq, IqFormat format, Span<byte> scratch)
    {
        int perComponent = format == IqFormat.Cf32 ? 4 : 2;
        int offset = 0;
        while (offset < interleavedIq.Length)
        {
            int take = Math.Min(scratch.Length / perComponent, interleavedIq.Length - offset);
            for (int i = 0; i < take; i++)
            {
                float value = interleavedIq[offset + i];
                if (format == IqFormat.Cf32)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(scratch[(i * 4)..], value);
                }
                else
                {
                    // Round rather than truncate, and clamp so a full-scale sample cannot wrap.
                    int scaled = (int)Math.Round(Math.Clamp(value, -1f, 1f) * 32767f);
                    BinaryPrimitives.WriteInt16LittleEndian(scratch[(i * 2)..], (short)scaled);
                }
            }

            destination.Write(scratch[..(take * perComponent)]);
            offset += take;
        }
    }

    public static IqFormat Parse(string text) => text.ToLowerInvariant() switch
    {
        "cf32" or "complex64" or "cfile" or "f32" => IqFormat.Cf32,
        "cs16" or "complex32" or "s16" or "i16" => IqFormat.Cs16,
        _ => throw new ArgumentException($"unknown --format '{text}' (expected cf32 or cs16)"),
    };
}

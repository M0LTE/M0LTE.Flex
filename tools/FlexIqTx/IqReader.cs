using System.Buffers.Binary;

namespace M0LTE.Flex.Tools.IqTx;

/// <summary>Sample formats accepted on stdin.</summary>
internal enum IqFormat
{
    /// <summary>Interleaved little-endian <c>float32</c> — GNU Radio <c>complex64</c> / a
    /// <c>.cfile</c>. The default, and what most SDR tooling emits.</summary>
    Cf32,

    /// <summary>Interleaved little-endian <c>int16</c>, full scale ±32767.</summary>
    Cs16,
}

/// <summary>
/// Streams interleaved I/Q from a byte source, converting to normalised floats (±1.0 full scale).
/// </summary>
/// <remarks>
/// Reads incrementally rather than slurping: an IQ stream is often long or endless (a pipe from a
/// modulator), and buffering it whole would both waste memory and delay keying until the producer
/// finished. Partial samples spanning a read boundary are carried over rather than dropped.
/// </remarks>
internal sealed class IqReader(Stream source, IqFormat format)
{
    private readonly int _bytesPerSample = format == IqFormat.Cf32 ? 8 : 4;
    private readonly byte[] _buffer = new byte[65536];
    private int _held;

    /// <summary>Complex samples read so far.</summary>
    public long SamplesRead { get; private set; }

    /// <summary>
    /// Fills <paramref name="destination"/> with as many whole complex samples as are available,
    /// returning the number of floats written, or 0 at end of stream.
    /// </summary>
    public int Read(Span<float> destination)
    {
        int wantBytes = Math.Min((destination.Length / 2) * _bytesPerSample, _buffer.Length);
        while (_held < _bytesPerSample)
        {
            int got = source.Read(_buffer, _held, Math.Max(wantBytes - _held, _bytesPerSample));
            if (got == 0)
            {
                return 0;                                  // end of stream; any partial sample is dropped
            }

            _held += got;
        }

        int usable = Math.Min(_held, wantBytes);
        int samples = usable / _bytesPerSample;
        for (int n = 0; n < samples; n++)
        {
            int at = n * _bytesPerSample;
            if (format == IqFormat.Cf32)
            {
                destination[2 * n] = BinaryPrimitives.ReadSingleLittleEndian(_buffer.AsSpan(at, 4));
                destination[(2 * n) + 1] = BinaryPrimitives.ReadSingleLittleEndian(_buffer.AsSpan(at + 4, 4));
            }
            else
            {
                destination[2 * n] = BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(at, 2)) / 32768f;
                destination[(2 * n) + 1] = BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(at + 2, 2)) / 32768f;
            }
        }

        // Carry the remainder — a sample split across two reads must not be lost or misaligned.
        int consumed = samples * _bytesPerSample;
        _held -= consumed;
        if (_held > 0)
        {
            Array.Copy(_buffer, consumed, _buffer, 0, _held);
        }

        SamplesRead += samples;
        return samples * 2;
    }

    public static IqFormat Parse(string text) => text.ToLowerInvariant() switch
    {
        "cf32" or "complex64" or "cfile" or "f32" => IqFormat.Cf32,
        "cs16" or "complex32" or "s16" or "i16" => IqFormat.Cs16,
        _ => throw new ArgumentException($"unknown --format '{text}' (expected cf32 or cs16)"),
    };
}

using System.Buffers.Binary;

namespace M0LTE.Flex.Tools.DaxTx;

/// <summary>Sample formats accepted for mono audio on stdin.</summary>
internal enum AudioFormat
{
    /// <summary>Little-endian <c>float32</c>, ±1.0 full scale — what sox writes with
    /// <c>-t raw -e float -b 32</c>, and the default.</summary>
    F32,

    /// <summary>Little-endian <c>int16</c>, ±32767 — ordinary WAV sample data.</summary>
    S16,
}

/// <summary>
/// Streams <b>mono real</b> audio from a byte source as normalised floats.
/// </summary>
/// <remarks>
/// One value per sample, not two: DAX audio is a real audio path, so feeding it interleaved I/Q
/// would transmit the Q channel as though it were more audio — at double the intended rate, and
/// sounding plausible enough not to notice.
/// </remarks>
internal sealed class AudioReader(Stream source, AudioFormat format)
{
    private readonly int _bytesPerSample = format == AudioFormat.F32 ? 4 : 2;
    private readonly byte[] _buffer = new byte[65536];
    private int _held;

    public long SamplesRead { get; private set; }

    /// <summary>Fills <paramref name="destination"/> with whole samples, or returns 0 at end of
    /// stream.</summary>
    public int Read(Span<float> destination)
    {
        int wantBytes = Math.Min(destination.Length * _bytesPerSample, _buffer.Length);
        while (_held < _bytesPerSample)
        {
            int got = source.Read(_buffer, _held, Math.Max(wantBytes - _held, _bytesPerSample));
            if (got == 0)
            {
                return 0;
            }

            _held += got;
        }

        int samples = Math.Min(_held, wantBytes) / _bytesPerSample;
        for (int n = 0; n < samples; n++)
        {
            destination[n] = format == AudioFormat.F32
                ? BinaryPrimitives.ReadSingleLittleEndian(_buffer.AsSpan(n * 4, 4))
                : BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(n * 2, 2)) / 32768f;
        }

        // Carry a sample straddling a read boundary rather than losing it.
        int consumed = samples * _bytesPerSample;
        _held -= consumed;
        if (_held > 0)
        {
            Array.Copy(_buffer, consumed, _buffer, 0, _held);
        }

        SamplesRead += samples;
        return samples;
    }

    public static AudioFormat Parse(string text) => text.ToLowerInvariant() switch
    {
        "f32" or "float32" or "float" => AudioFormat.F32,
        "s16" or "int16" or "i16" or "pcm" => AudioFormat.S16,
        _ => throw new ArgumentException($"unknown --format '{text}' (expected f32 or s16)"),
    };
}

using System.Buffers.Binary;

namespace M0LTE.Flex.Tools.IqNoise;

/// <summary>
/// Streams interleaved <c>I, Q</c> floats to a 2-channel 32-bit-float WAV (channel 1 = I,
/// channel 2 = Q), so exactly what was handed to the radio can be re-examined in Audacity,
/// inspectrum, GNU Radio or SoX. Sizes are patched into the header on dispose.
/// </summary>
internal sealed class IqWavWriter : IDisposable
{
    private const int HeaderBytes = 56;
    private const int Channels = 2;
    private const int BitsPerSample = 32;

    private readonly FileStream _stream;
    private readonly byte[] _scratch = new byte[8192];
    private long _dataBytes;

    public IqWavWriter(string path, int sampleRate)
    {
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        WriteHeader(sampleRate);
    }

    public void Write(ReadOnlySpan<float> interleavedIq)
    {
        int offset = 0;
        while (offset < interleavedIq.Length)
        {
            int take = Math.Min(_scratch.Length / 4, interleavedIq.Length - offset);
            for (int i = 0; i < take; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(_scratch.AsSpan(i * 4, 4), interleavedIq[offset + i]);
            }

            _stream.Write(_scratch, 0, take * 4);
            _dataBytes += take * 4;
            offset += take;
        }
    }

    private void WriteHeader(int sampleRate)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        header.Clear();

        "RIFF"u8.CopyTo(header[..4]);
        // riffSize at [4..8) is patched on dispose.
        "WAVE"u8.CopyTo(header.Slice(8, 4));

        "fmt "u8.CopyTo(header.Slice(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20, 2), 3);          // IEEE float
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22, 2), Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.Slice(28, 4), (uint)(sampleRate * Channels * (BitsPerSample / 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32, 2), Channels * (BitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(34, 2), BitsPerSample);

        "fact"u8.CopyTo(header.Slice(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40, 4), 4);
        // sample-frame count at [44..48) is patched on dispose.

        "data"u8.CopyTo(header.Slice(48, 4));
        // dataSize at [52..56) is patched on dispose.

        _stream.Write(header);
    }

    public void Dispose()
    {
        if (_stream.CanSeek)
        {
            Span<byte> value = stackalloc byte[4];

            _stream.Position = 4;
            BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)(HeaderBytes - 8 + _dataBytes));
            _stream.Write(value);

            _stream.Position = 44;
            BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)(_dataBytes / (Channels * (BitsPerSample / 8))));
            _stream.Write(value);

            _stream.Position = 52;
            BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)_dataBytes);
            _stream.Write(value);
        }

        _stream.Dispose();
    }
}

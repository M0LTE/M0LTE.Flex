using System.Buffers.Binary;

namespace M0LTE.Flex;

/// <summary>
/// Minimal RIFF/WAVE writer for 16-bit mono PCM — backs
/// <see cref="MockFlexRadio.WriteCapturedTxWav"/> so a test can dump the audio the mock
/// received. Not a general WAV library.
/// </summary>
internal static class WavFile
{
    /// <summary>Writes mono 16-bit PCM. Samples are clipped to −1..1.</summary>
    public static void WriteMono(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        int dataBytes = samples.Length * 2;
        var buffer = new byte[44 + dataBytes];
        var span = buffer.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);  // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);  // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);  // block align
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16); // bits per sample
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);

        for (int i = 0; i < samples.Length; i++)
        {
            float clipped = Math.Clamp(samples[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(
                span[(44 + i * 2)..], (short)MathF.Round(clipped * 32767f));
        }

        File.WriteAllBytes(path, buffer);
    }
}

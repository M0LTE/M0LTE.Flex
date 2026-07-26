using System.Buffers.Binary;
using GenFormat = M0LTE.Flex.Tools.IqGen.IqFormat;
using GenIo = M0LTE.Flex.Tools.IqGen.IqFormatIo;
using TxFormat = M0LTE.Flex.Tools.IqTx.IqFormat;
using TxReader = M0LTE.Flex.Tools.IqTx.IqReader;

namespace M0LTE.Flex.Tools.Tests;

/// <summary>
/// The on-the-wire IQ formats. These are the interop contract — <c>cf32</c> is claimed to be
/// byte-identical to GNU Radio's <c>complex64</c> — and a silent change to layout, endianness or
/// scaling would corrupt every sample without failing anything.
/// </summary>
public sealed class IqFormatTests
{
    private static byte[] Write(float[] iq, GenFormat format)
    {
        using var stream = new MemoryStream();
        GenIo.Write(stream, iq, format, new byte[64]);
        return stream.ToArray();
    }

    [Fact]
    public void Cf32_is_interleaved_little_endian_float32_with_no_header()
    {
        byte[] bytes = Write([0.5f, -0.25f], GenFormat.Cf32);

        bytes.Length.Should().Be(8, "one complex sample is 8 bytes — no header, no padding");
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(0, 4)).Should().Be(0.5f);
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4, 4)).Should().Be(-0.25f);
    }

    [Fact]
    public void Cs16_is_interleaved_little_endian_int16_at_full_scale()
    {
        byte[] bytes = Write([1.0f, -1.0f], GenFormat.Cs16);

        bytes.Length.Should().Be(4);
        BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(0, 2)).Should().Be(32767);
        BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2, 2)).Should().Be(-32767);
    }

    [Fact]
    public void Cs16_clamps_rather_than_wrapping_an_over_range_sample()
    {
        // Wrapping would turn a positive overshoot into a large negative sample — a click on air,
        // and one that looks like data rather than an error.
        byte[] bytes = Write([2.0f, -2.0f], GenFormat.Cs16);

        BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(0, 2)).Should().Be(32767);
        BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2, 2)).Should().Be(-32767);
    }

    [Fact]
    public void What_the_generator_writes_the_transmitter_reads_back_as_cf32() =>
        RoundTrips(GenFormat.Cf32, TxFormat.Cf32);

    [Fact]
    public void What_the_generator_writes_the_transmitter_reads_back_as_cs16() =>
        RoundTrips(GenFormat.Cs16, TxFormat.Cs16);

    private static void RoundTrips(GenFormat write, TxFormat read)
    {
        var original = new float[512];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (float)Math.Sin(i * 0.05) * 0.5f;
        }

        using var stream = new MemoryStream(Write(original, write));
        var reader = new TxReader(stream, read);
        var recovered = new float[original.Length];
        int filled = 0;
        int got;
        while (filled < recovered.Length && (got = reader.Read(recovered.AsSpan(filled))) > 0)
        {
            filled += got;
        }

        filled.Should().Be(original.Length);
        for (int i = 0; i < original.Length; i++)
        {
            // cs16 quantises to ~3e-5; cf32 is exact.
            recovered[i].Should().BeApproximately(original[i], 1e-4f);
        }
    }

    [Fact]
    public void A_sample_split_across_two_reads_is_carried_over_not_lost()
    {
        // The reader pulls from a pipe, so a complex sample routinely straddles a read boundary.
        // Dropping or misaligning one there would shift I and Q against each other for the rest of
        // the stream — which conjugates the signal, and looks like a working transmission.
        var original = new float[256];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = i / 256f;
        }

        using var stream = new DribblingStream(Write(original, GenFormat.Cf32), chunk: 3);
        var reader = new TxReader(stream, TxFormat.Cf32);
        var recovered = new float[original.Length];
        int filled = 0;
        int got;
        while (filled < recovered.Length && (got = reader.Read(recovered.AsSpan(filled))) > 0)
        {
            filled += got;
        }

        filled.Should().Be(original.Length);
        recovered.Should().Equal(original);
    }

    /// <summary>A stream that returns awkward short reads, like a real pipe does.</summary>
    private sealed class DribblingStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(Math.Min(chunk, count), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

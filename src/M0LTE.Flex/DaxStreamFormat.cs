using System.Buffers.Binary;

namespace M0LTE.Flex;

/// <summary>
/// The two DAX-audio transports a 6000-series radio offers, and the sample↔VITA payload
/// conversions for each. Reduced-bandwidth is the native 24 kHz s16 big-endian mode (128
/// mono samples/packet); full-bandwidth is 48 kHz float32 big-endian (256 mono
/// samples/packet). DAX audio is mono-per-channel into a slice — nDAX creates its RX/TX
/// pulse pipes with a single channel and writes the raw payload straight through, so a
/// packet's payload is <see cref="SamplesPerPacket"/> mono samples, not interleaved
/// stereo (that is only <c>remote_audio_rx</c>). See docs/flex-integration.md §2.4.
/// </summary>
/// <remarks>
/// Constants from nDAX <c>main.go</c> (© Andrew Rodland KC2G, MIT): the two
/// <c>audioCfg</c> branches (sampleRate/samplesPerPacket/bytesPerSample/format/streamClass).
/// </remarks>
public sealed record DaxStreamFormat
{
    private DaxStreamFormat(
        int sampleRate, int samplesPerPacket, bool isFloat, ushort packetClassCode, ulong streamClass)
    {
        SampleRate = sampleRate;
        SamplesPerPacket = samplesPerPacket;
        IsFloat = isFloat;
        PacketClassCode = packetClassCode;
        StreamClass = streamClass;
    }

    /// <summary>Reduced-bandwidth DAX audio: 24 kHz, s16 big-endian, 128 samples/packet.
    /// The radio's native mode; requires <c>client set send_reduced_bw_dax=true</c>.</summary>
    public static DaxStreamFormat ReducedBandwidth { get; } =
        new(24000, 128, isFloat: false, Vita49.ReducedDaxAudioClass, Vita49.ReducedDaxStreamClass);

    /// <summary>Full-bandwidth DAX audio: 48 kHz, float32 big-endian, 256 samples/packet.</summary>
    public static DaxStreamFormat FullBandwidth { get; } =
        new(48000, 256, isFloat: true, Vita49.IfNarrowClass, Vita49.FullDaxStreamClass);

    /// <summary>Wire sample rate (24000 or 48000 Hz).</summary>
    public int SampleRate { get; }

    /// <summary>Mono samples carried in one VITA-49 DAX packet.</summary>
    public int SamplesPerPacket { get; }

    /// <summary>True for float32 samples; false for s16.</summary>
    public bool IsFloat { get; }

    /// <summary>Bytes per sample (2 for s16, 4 for float32).</summary>
    public int BytesPerSample => IsFloat ? 4 : 2;

    /// <summary>VITA-49 packet-class code identifying this stream on the wire.</summary>
    public ushort PacketClassCode { get; }

    /// <summary>The 64-bit stream class written into the DAX packet header.</summary>
    public ulong StreamClass { get; }

    /// <summary>Payload byte length of a full DAX packet for this format.</summary>
    public int PayloadBytesPerPacket => SamplesPerPacket * BytesPerSample;

    /// <summary>Whether <c>client set send_reduced_bw_dax=true</c> must precede stream setup.</summary>
    public bool IsReducedBandwidth => !IsFloat;

    /// <summary>
    /// Auto-picks the DAX transport from the modem's DSP rate (design §4, settled default):
    /// 12 kHz audio-band modes bridge to reduced-bandwidth 24 kHz s16 (÷2/×2); 48 kHz
    /// (9600-family and freedv) modes bridge to full-bandwidth 48 kHz float32 (1:1).
    /// </summary>
    public static DaxStreamFormat ForDspRate(int dspRate) => dspRate switch
    {
        48000 => FullBandwidth,
        _ when 24000 % dspRate == 0 => ReducedBandwidth,
        _ => throw new ArgumentException(
            $"no DAX transport bridges DSP rate {dspRate} Hz with an integer ratio", nameof(dspRate)),
    };

    /// <summary>Converts big-endian wire samples to normalised floats (−1..1). Returns the
    /// number of samples written; <paramref name="destination"/> must hold them all.</summary>
    public int Depacketize(ReadOnlySpan<byte> payload, Span<float> destination)
    {
        int count = payload.Length / BytesPerSample;
        if (destination.Length < count)
        {
            throw new ArgumentException("destination too small for the payload", nameof(destination));
        }

        if (IsFloat)
        {
            for (int i = 0; i < count; i++)
            {
                uint bits = BinaryPrimitives.ReadUInt32BigEndian(payload[(i * 4)..]);
                destination[i] = BitConverter.UInt32BitsToSingle(bits);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                short value = BinaryPrimitives.ReadInt16BigEndian(payload[(i * 2)..]);
                destination[i] = value / 32768f;
            }
        }

        return count;
    }

    /// <summary>Converts normalised floats to big-endian wire samples. s16 rounds and
    /// clamps to −1..1 (matching a soundcard 16-bit output path); float32 is
    /// written verbatim so a loopback round-trips exactly.</summary>
    public void WriteSamples(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        if (destination.Length < samples.Length * BytesPerSample)
        {
            throw new ArgumentException("destination too small for the samples", nameof(destination));
        }

        if (IsFloat)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(
                    destination[(i * 4)..], BitConverter.SingleToUInt32Bits(samples[i]));
            }
        }
        else
        {
            for (int i = 0; i < samples.Length; i++)
            {
                short value = (short)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
                BinaryPrimitives.WriteInt16BigEndian(destination[(i * 2)..], value);
            }
        }
    }

    /// <summary>Builds a complete DAX-audio VITA-49 packet from mono float samples (used by
    /// the transmit path and the mock radio).</summary>
    public byte[] BuildPacket(uint streamId, int packetCount, ReadOnlySpan<float> samples)
    {
        Span<byte> payload = samples.Length * BytesPerSample <= 4096
            ? stackalloc byte[samples.Length * BytesPerSample]
            : new byte[samples.Length * BytesPerSample];
        WriteSamples(samples, payload);
        return Vita49.BuildDaxAudioPacket(StreamClass, streamId, packetCount, payload);
    }
}

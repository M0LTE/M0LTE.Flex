using System.Buffers.Binary;

namespace M0LTE.Flex;

/// <summary>VITA-49 packet type (the top nibble of the first header word).</summary>
public enum VitaPacketType
{
    /// <summary>IF data, no stream identifier.</summary>
    IfData = 0,

    /// <summary>IF data with a stream identifier — DAX audio uses this.</summary>
    IfDataWithStream = 1,

    /// <summary>Extension data, no stream identifier.</summary>
    ExtData = 2,

    /// <summary>Extension data with a stream identifier — discovery uses this.</summary>
    ExtDataWithStream = 3,

    /// <summary>IF context.</summary>
    IfContext = 4,

    /// <summary>Extension context.</summary>
    ExtContext = 5,
}

/// <summary>VITA-49 timestamp-integer mode (TSI, 2 bits).</summary>
public enum VitaTsi
{
    /// <summary>No integer timestamp present.</summary>
    None = 0,

    /// <summary>UTC seconds.</summary>
    Utc = 1,

    /// <summary>GPS seconds.</summary>
    Gps = 2,

    /// <summary>Other/free-form — what the Flex DAX stream uses.</summary>
    Other = 3,
}

/// <summary>VITA-49 timestamp-fractional mode (TSF, 2 bits).</summary>
public enum VitaTsf
{
    /// <summary>No fractional timestamp present.</summary>
    None = 0,

    /// <summary>Sample count — what the Flex DAX stream uses.</summary>
    SampleCount = 1,

    /// <summary>Real time (picoseconds).</summary>
    RealTime = 2,

    /// <summary>Free-running count.</summary>
    FreeRunning = 3,
}

/// <summary>The VITA-49 class identifier (present when the header's C bit is set).</summary>
/// <param name="Oui">Organizationally-unique identifier (24 bits); FlexRadio is 0x001C2D.</param>
/// <param name="InformationClassCode">The information-class code (16 bits); Flex streams use 0x534C ("SL").</param>
/// <param name="PacketClassCode">The packet-class code (16 bits) — the stream-type discriminator.</param>
public readonly record struct VitaClassId(uint Oui, ushort InformationClassCode, ushort PacketClassCode);

/// <summary>The parsed fields of a VITA-49 packet preamble (header + optional stream id,
/// class id and timestamps), plus the offset/length of the payload that follows.</summary>
/// <param name="PacketType">The packet type.</param>
/// <param name="HasClassId">The C bit — a class identifier is present.</param>
/// <param name="HasTrailer">The T bit — a 4-byte trailer terminates the packet.</param>
/// <param name="Tsi">Integer-timestamp mode.</param>
/// <param name="Tsf">Fractional-timestamp mode.</param>
/// <param name="PacketCount">The 4-bit modulo-16 packet counter.</param>
/// <param name="PacketSizeWords">The declared packet size in 32-bit words.</param>
/// <param name="StreamId">The 32-bit stream identifier (0 when absent).</param>
/// <param name="ClassId">The class identifier (default when absent).</param>
/// <param name="TimestampInt">The integer timestamp (0 when absent).</param>
/// <param name="TimestampFrac">The fractional timestamp (0 when absent).</param>
/// <param name="PayloadOffset">Byte offset of the payload within the packet.</param>
/// <param name="PayloadLength">Byte length of the payload (trailer excluded).</param>
public readonly record struct VitaPreamble(
    VitaPacketType PacketType,
    bool HasClassId,
    bool HasTrailer,
    VitaTsi Tsi,
    VitaTsf Tsf,
    int PacketCount,
    int PacketSizeWords,
    uint StreamId,
    VitaClassId ClassId,
    uint TimestampInt,
    ulong TimestampFrac,
    int PayloadOffset,
    int PayloadLength);

/// <summary>
/// VITA-49 parse and build for the FlexRadio 6000-series API subset the modem path needs
/// (discovery and DAX audio streams). Big-endian throughout, per the protocol.
/// </summary>
/// <remarks>
/// Ported with provenance from the MIT-licensed Go reference clients (© 2017 Frank
/// Werner-Häcker HB9FXQ; © Andrew Rodland KC2G): the preamble field layout and bit masks
/// from <c>hb9fxq/flexlib-go</c> <c>vita/vitahandler.go</c> <c>ParseVitaPreamble</c> and
/// <c>vita/vitatypes.go</c> (class codes, OUI, MAX_VITA_PACKET_SIZE); the DAX-audio TX
/// packet byte layout from <c>kc2g-flex-tools/nDAX</c> <c>main.go</c>
/// <c>streamFromPulse</c> (the 28-byte header) and its two <c>streamClass</c>/rate
/// branches. The wire protocol itself is publicly documented by FlexRadio
/// (smartsdr-api-docs wiki: TCPIP-dax, TCPIP-stream, Discovery-protocol). See
/// docs/flex-integration.md §2.3/§2.4 and PROVENANCE.md.
/// </remarks>
public static class Vita49
{
    /// <summary>FlexRadio's organizationally-unique identifier.</summary>
    public const uint FlexOui = 0x001C2D;

    /// <summary>Information-class code carried by every Flex stream ("SL").</summary>
    public const ushort FlexInformationClass = 0x534C;

    /// <summary>Maximum VITA-49 packet size in bytes (flexlib-go MAX_VITA_PACKET_SIZE).</summary>
    public const int MaxVitaPacketSize = 16384;

    /// <summary>The radio's UDP source port for its VITA-49 streams.</summary>
    public const int RadioVitaPort = 4991;

    /// <summary>Discovery / command / status UDP+TCP port.</summary>
    public const int DiscoveryPort = 4992;

    /// <summary>Packet-class code: discovery broadcast (flexlib-go SL_VITA_DISCOVERY_CLASS).</summary>
    public const ushort DiscoveryClass = 0xFFFF;

    /// <summary>Packet-class code: meter readings.</summary>
    public const ushort MeterClass = 0x8002;

    /// <summary>Packet-class code: panadapter FFT.</summary>
    public const ushort FftClass = 0x8003;

    /// <summary>Packet-class code: waterfall tiles.</summary>
    public const ushort WaterfallClass = 0x8004;

    /// <summary>Packet-class code: Opus-compressed remote audio.</summary>
    public const ushort OpusClass = 0x8005;

    /// <summary>Packet-class code: full-rate DAX audio / remote_audio (float32, 48 kHz).</summary>
    public const ushort IfNarrowClass = 0x03E3;

    /// <summary>Packet-class code: reduced-bandwidth DAX audio (s16, 24 kHz).</summary>
    public const ushort ReducedDaxAudioClass = 0x0123;

    /// <summary>The 64-bit stream class written into a reduced-bandwidth (24 kHz s16) DAX
    /// audio packet: OUI 0x001C2D, info-class 0x534C, packet-class 0x0123.</summary>
    public const ulong ReducedDaxStreamClass = 0x00001C2D534C0123UL;

    /// <summary>The 64-bit stream class written into a full-bandwidth (48 kHz float32) DAX
    /// audio packet: OUI 0x001C2D, info-class 0x534C, packet-class 0x03E3.</summary>
    public const ulong FullDaxStreamClass = 0x00001C2D534C03E3UL;

    /// <summary>The discovery packet's stream identifier.</summary>
    public const uint DiscoveryStreamId = 0x00000800;

    /// <summary>The number of 32-bit words in a DAX-audio packet header (0x18 marker word,
    /// stream id, two class-id words, and three zeroed timestamp words).</summary>
    public const int DaxHeaderWords = 7;

    /// <summary>Byte length of the DAX-audio packet header.</summary>
    public const int DaxHeaderBytes = DaxHeaderWords * 4;

    /// <summary>
    /// Parses a VITA-49 preamble. Faithful port of flexlib-go
    /// <c>ParseVitaPreamble</c>: the first header word's bit fields, then the optional
    /// stream id (types 1/3), class id (C bit), integer and fractional timestamps, and a
    /// 4-byte trailer (T bit). Returns false for a buffer too short to hold a preamble.
    /// </summary>
    public static bool TryParsePreamble(ReadOnlySpan<byte> data, out VitaPreamble preamble)
    {
        preamble = default;
        if (data.Length < 20)
        {
            return false;
        }

        uint header = BinaryPrimitives.ReadUInt32BigEndian(data);
        int index = 4;

        var packetType = (VitaPacketType)(header >> 28);
        bool hasClassId = (header & 0x08000000) != 0;
        bool hasTrailer = (header & 0x04000000) != 0;
        var tsi = (VitaTsi)((header >> 22) & 0x03);
        var tsf = (VitaTsf)((header >> 20) & 0x03);
        int packetCount = (int)((header >> 16) & 0x0F);
        int packetSizeWords = (int)(header & 0xFFFF);

        uint streamId = 0;
        if (packetType is VitaPacketType.IfDataWithStream or VitaPacketType.ExtDataWithStream)
        {
            if (data.Length < index + 4)
            {
                return false;
            }

            streamId = BinaryPrimitives.ReadUInt32BigEndian(data[index..]);
            index += 4;
        }

        var classId = default(VitaClassId);
        if (hasClassId)
        {
            if (data.Length < index + 8)
            {
                return false;
            }

            uint classHigh = BinaryPrimitives.ReadUInt32BigEndian(data[index..]);
            index += 4;
            uint classLow = BinaryPrimitives.ReadUInt32BigEndian(data[index..]);
            index += 4;
            classId = new VitaClassId(
                classHigh & 0x00FFFFFF,
                (ushort)(classLow >> 16),
                (ushort)classLow);
        }

        uint timestampInt = 0;
        if (tsi != VitaTsi.None)
        {
            if (data.Length < index + 4)
            {
                return false;
            }

            timestampInt = BinaryPrimitives.ReadUInt32BigEndian(data[index..]);
            index += 4;
        }

        ulong timestampFrac = 0;
        if (tsf != VitaTsf.None)
        {
            if (data.Length < index + 8)
            {
                return false;
            }

            timestampFrac = BinaryPrimitives.ReadUInt64BigEndian(data[index..]);
            index += 8;
        }

        int trailerBytes = hasTrailer ? 4 : 0;
        int payloadLength = data.Length - index - trailerBytes;
        if (payloadLength < 0)
        {
            return false;
        }

        preamble = new VitaPreamble(
            packetType, hasClassId, hasTrailer, tsi, tsf, packetCount, packetSizeWords,
            streamId, classId, timestampInt, timestampFrac, index, payloadLength);
        return true;
    }

    /// <summary>
    /// Builds a DAX-audio VITA-49 packet (client→radio TX or radio→client RX — the layout
    /// is identical). Byte-for-byte the nDAX <c>streamFromPulse</c> header
    /// (docs/flex-integration.md §2.4): <c>0x18</c>, <c>0xD0 | (count &amp; 0x0F)</c>,
    /// u16be word count, u32be stream id, u64be stream class, three zeroed u32be timestamp
    /// words, then the big-endian sample payload.
    /// </summary>
    /// <param name="streamClass">The 64-bit stream class (<see cref="ReducedDaxStreamClass"/>
    /// or <see cref="FullDaxStreamClass"/>).</param>
    /// <param name="streamId">The DAX stream identifier returned by <c>stream create</c>.</param>
    /// <param name="packetCount">The modulo-16 packet counter (only the low 4 bits are used).</param>
    /// <param name="payload">The already big-endian sample bytes; length must be a multiple of 4.</param>
    public static byte[] BuildDaxAudioPacket(
        ulong streamClass, uint streamId, int packetCount, ReadOnlySpan<byte> payload)
    {
        if (payload.Length % 4 != 0)
        {
            throw new ArgumentException("payload byte length must be a multiple of 4", nameof(payload));
        }

        var packet = new byte[DaxHeaderBytes + payload.Length];
        var span = packet.AsSpan();

        span[0] = 0x18;
        span[1] = (byte)(0xD0 | (packetCount & 0x0F));
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)(payload.Length / 4 + DaxHeaderWords));
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], streamId);
        BinaryPrimitives.WriteUInt64BigEndian(span[8..], streamClass);
        // Bytes 16..27 are the (unused) integer + fractional timestamp words, left zero.
        payload.CopyTo(span[DaxHeaderBytes..]);
        return packet;
    }

    /// <summary>
    /// Builds a VITA-49 discovery broadcast packet carrying the ASCII <c>key=value</c>
    /// payload — the shape a 6000-series radio emits once per second on UDP :4992, used by
    /// the tests and the mock radio. Extension-data-with-stream type, class OUI 0x001C2D /
    /// info 0x534C / packet 0xFFFF, stream id 0x800, no timestamps or trailer; the payload
    /// is NUL-padded to a 32-bit word boundary.
    /// </summary>
    public static byte[] BuildDiscoveryPacket(string keyValues)
    {
        ArgumentNullException.ThrowIfNull(keyValues);
        int payloadLength = keyValues.Length;
        int padded = (payloadLength + 3) & ~3;
        // header word + stream id + 2 class words = 16 bytes of preamble.
        const int preambleBytes = 16;
        int totalBytes = preambleBytes + padded;

        var packet = new byte[totalBytes];
        var span = packet.AsSpan();

        // Header word: type=ExtDataWithStream (3), C=1, TSI=None, TSF=None, count=0.
        uint header = ((uint)VitaPacketType.ExtDataWithStream << 28)
            | 0x08000000u
            | (uint)(totalBytes / 4);
        BinaryPrimitives.WriteUInt32BigEndian(span, header);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], DiscoveryStreamId);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], FlexOui);
        BinaryPrimitives.WriteUInt32BigEndian(
            span[12..], ((uint)FlexInformationClass << 16) | DiscoveryClass);
        System.Text.Encoding.ASCII.GetBytes(keyValues, span[preambleBytes..]);
        return packet;
    }
}

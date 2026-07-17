using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace M0LTE.Flex;

/// <summary>
/// A radio located by discovery (or described by an explicit host), parsed from the
/// discovery broadcast's <c>key=value</c> payload
/// (<c>model serial version name callsign ip port</c>). Underscores in
/// <see cref="Name"/> are converted back to spaces for display, per the protocol.
/// </summary>
public sealed record FlexRadioInfo
{
    /// <summary>Radio model, e.g. <c>FLEX-6500</c>.</summary>
    public string Model { get; init; } = "";

    /// <summary>Serial number.</summary>
    public string Serial { get; init; } = "";

    /// <summary>Firmware/API version, e.g. <c>3.8.23.35785</c>.</summary>
    public string Version { get; init; } = "";

    /// <summary>Radio "nickname" (underscores decoded back to spaces).</summary>
    public string Name { get; init; } = "";

    /// <summary>Owner callsign.</summary>
    public string Callsign { get; init; } = "";

    /// <summary>Radio IP address.</summary>
    public string Ip { get; init; } = "";

    /// <summary>TCP command/status port (normally 4992).</summary>
    public int Port { get; init; } = Vita49.DiscoveryPort;

    /// <summary>Every raw <c>key=value</c> field from the discovery payload (unmodified).</summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// FlexRadio discovery: parses the VITA-49 UDP :4992 broadcast and listens for a radio
/// matching a spec. Supports an explicit IP to skip discovery (broadcast will not cross
/// subnets). See docs/flex-integration.md §2.1.
/// </summary>
/// <remarks>
/// Ported with provenance from <c>kc2g-flex-tools/flexclient</c> (© Andrew Rodland KC2G,
/// MIT): <c>discovery.go</c> (<c>Discover</c>/<c>discoveryRecv</c>/<c>discoveryMatch</c>
/// — the OUI 0x001C2D + packet-class 0xFFFF gate and the <c>key=value</c> parse) and
/// <c>discovery_unix.go</c> (bind <c>:4992</c> with <c>SO_REUSEPORT</c>).
/// </remarks>
public static class FlexDiscovery
{
    // Linux SOL_SOCKET / SO_REUSEPORT — flexclient sets this so a discovery listener can
    // coexist with SmartSDR's. Not exposed as a named SocketOptionName in .NET.
    private const int SolSocket = 1;
    private const int SoReusePort = 15;

    /// <summary>Parses the discovery payload's <c>key=value</c> fields into a dictionary.
    /// Trailing NULs and spaces are trimmed (the payload is word-padded).</summary>
    public static Dictionary<string, string> ParseFields(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in payload.Trim(' ', '\0').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                fields[part[..eq]] = part[(eq + 1)..];
            }
        }

        return fields;
    }

    /// <summary>Maps parsed discovery fields to a <see cref="FlexRadioInfo"/> (decoding the
    /// underscore↔space rule on <see cref="FlexRadioInfo.Name"/>).</summary>
    public static FlexRadioInfo ToRadioInfo(IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return new FlexRadioInfo
        {
            Model = fields.GetValueOrDefault("model", ""),
            Serial = fields.GetValueOrDefault("serial", ""),
            Version = fields.GetValueOrDefault("version", ""),
            Name = fields.GetValueOrDefault("name", "").Replace('_', ' '),
            Callsign = fields.GetValueOrDefault("callsign", ""),
            Ip = fields.GetValueOrDefault("ip", ""),
            Port = int.TryParse(fields.GetValueOrDefault("port"), out int port) ? port : Vita49.DiscoveryPort,
            Fields = fields,
        };
    }

    /// <summary>Parses a raw VITA-49 discovery packet to a <see cref="FlexRadioInfo"/>, or
    /// returns null if it is not a Flex discovery broadcast.</summary>
    public static FlexRadioInfo? TryParsePacket(ReadOnlySpan<byte> packet)
    {
        if (!Vita49.TryParsePreamble(packet, out VitaPreamble preamble)
            || !preamble.HasClassId
            || preamble.ClassId.Oui != Vita49.FlexOui
            || preamble.ClassId.PacketClassCode != Vita49.DiscoveryClass)
        {
            return null;
        }

        string payload = System.Text.Encoding.ASCII.GetString(
            packet.Slice(preamble.PayloadOffset, preamble.PayloadLength));
        return ToRadioInfo(ParseFields(payload));
    }

    /// <summary>
    /// Listens on UDP :4992 for a discovery broadcast matching <paramref name="spec"/> (a
    /// <c>key=value</c> filter over the raw discovery fields — e.g. <c>serial=1234</c> or
    /// <c>model=FLEX-6500</c>; empty/null matches the first radio seen). Returns the first
    /// match, or throws <see cref="TimeoutException"/> after <paramref name="timeout"/>.
    /// </summary>
    public static async Task<FlexRadioInfo> DiscoverAsync(
        string? spec, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var filter = string.IsNullOrWhiteSpace(spec)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ParseFields(spec);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        TrySetReusePort(socket);
        socket.Bind(new IPEndPoint(IPAddress.Any, Vita49.DiscoveryPort));

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutSource.Token);
        var buffer = new byte[Vita49.MaxVitaPacketSize];

        try
        {
            while (true)
            {
                int received = await socket.ReceiveAsync(buffer, SocketFlags.None, linked.Token)
                    .ConfigureAwait(false);
                FlexRadioInfo? info = TryParsePacket(buffer.AsSpan(0, received));
                if (info is not null && Matches(info.Fields, filter))
                {
                    return info;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"no FlexRadio discovery broadcast matching '{spec}' within {timeout.TotalSeconds:0.#}s");
        }
    }

    private static bool Matches(
        IReadOnlyDictionary<string, string> fields, Dictionary<string, string> filter)
    {
        foreach ((string key, string value) in filter)
        {
            if (!fields.TryGetValue(key, out string? actual) || actual != value)
            {
                return false;
            }
        }

        return true;
    }

    private static void TrySetReusePort(Socket socket)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        try
        {
            socket.SetRawSocketOption(SolSocket, SoReusePort, [1, 0, 0, 0]);
        }
        catch (SocketException)
        {
            // Best-effort: coexistence with another discovery listener is optional.
        }
    }
}

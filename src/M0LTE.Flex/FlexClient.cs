using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace M0LTE.Flex;

/// <summary>The result of a command: the sequence number, the error code (0 = OK) and the
/// message text from the radio's <c>R&lt;seq&gt;|&lt;err&gt;|&lt;msg&gt;</c> reply.</summary>
/// <param name="Serial">The command sequence number.</param>
/// <param name="Error">The error code — 0 is success, non-zero is a documented failure.</param>
/// <param name="Message">The reply message (a stream id hex, a fault string, or empty).</param>
public readonly record struct FlexResult(uint Serial, uint Error, string Message)
{
    /// <summary>True when the command succeeded.</summary>
    public bool IsOk => Error == 0;
}

/// <summary>An asynchronous object-status update from the radio
/// (<c>S&lt;handle&gt;|&lt;object&gt; k=v …</c>).</summary>
/// <param name="SenderHandle">The handle that caused the update.</param>
/// <param name="Object">The object name, e.g. <c>slice 0</c> or <c>interlock</c>.</param>
/// <param name="Updated">The keys changed by this update.</param>
/// <param name="Current">The object's full current state after applying the update.</param>
public readonly record struct FlexStatusUpdate(
    string SenderHandle,
    string Object,
    IReadOnlyDictionary<string, string> Updated,
    IReadOnlyDictionary<string, string> Current);

/// <summary>
/// A managed FlexRadio 6000-series client: the TCP :4992 command/status session plus the
/// UDP VITA-49 stream socket. Speaks the ASCII line protocol (prologue
/// <c>V…</c>/<c>H…</c>; <c>C&lt;seq&gt;|cmd</c> commands awaiting
/// <c>R&lt;seq&gt;|err|msg</c>; <c>S…</c> status; <c>M…</c> messages) and forwards
/// received VITA packets to <see cref="VitaPacketReceived"/>. See
/// docs/flex-integration.md §2.2/§2.3.
/// </summary>
/// <remarks>
/// Ported with provenance from <c>kc2g-flex-tools/flexclient</c> (© Andrew Rodland KC2G,
/// MIT) <c>client.go</c>: the prologue, the <c>C%d|%s\n</c> command format and monotonic
/// sequence, the <c>parseLine</c>/<c>parseGenericState</c> status machine (object-name
/// accumulation, the <c>removed</c>/<c>connected</c>/<c>disconnected</c> cases, the 0x7f→
/// space rule on <c>client station</c>), and <c>InitUDP</c> (<c>client udpport</c>, radio
/// VITA source port 4991). The wire protocol is publicly documented by FlexRadio
/// (smartsdr-api-docs wiki: SmartSDR-TCPIP-API, TCPIP-keepalive).
/// </remarks>
public sealed class FlexClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<FlexResult>> _pending = new();
    private readonly Dictionary<string, Dictionary<string, string>> _state = new(StringComparer.Ordinal);
    private readonly object _stateLock = new();
    private readonly TaskCompletionSource _prologueComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();

    private Socket? _udp;
    private IPEndPoint? _radioVitaEndpoint;
    private Task? _readLoop;
    private Task? _udpLoop;
    private Task? _keepalive;
    private int _disposed;
    private uint _cmdSeq;

    private FlexClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>The radio's reported version (the <c>V…</c> prologue line).</summary>
    public string Version { get; private set; } = "";

    /// <summary>This client's handle (the <c>H…</c> prologue line, a 32-bit hex value).</summary>
    public string Handle { get; private set; } = "";

    /// <summary>The local UDP port the radio streams VITA-49 to (valid after
    /// <see cref="InitUdpAsync"/>).</summary>
    public int LocalUdpPort { get; private set; }

    /// <summary>Raised for every VITA-49 packet received on the UDP stream socket. The
    /// payload array is a per-packet copy; the handler should consume it synchronously.</summary>
    public event Action<VitaPreamble, byte[]>? VitaPacketReceived;

    /// <summary>Raised for every object-status update (<c>S…</c>).</summary>
    public event Action<FlexStatusUpdate>? StatusUpdated;

    /// <summary>Raised for every informational/warning/fault message (<c>M…</c>): the
    /// sender handle and the message text.</summary>
    public event Action<string, string>? MessageReceived;

    /// <summary>Connects to a radio at an explicit host, waits for the prologue, and starts
    /// the read loop.</summary>
    /// <param name="host">Radio IP or hostname.</param>
    /// <param name="port">TCP command port (normally 4992).</param>
    /// <param name="radioVitaPort">The radio's UDP VITA source port (normally 4991; the
    /// mock radio overrides this to an ephemeral port).</param>
    /// <param name="cancellation">Cancels the connect/prologue wait.</param>
    public static async Task<FlexClient> ConnectAsync(
        string host, int port = Vita49.DiscoveryPort, int radioVitaPort = Vita49.RadioVitaPort,
        CancellationToken cancellation = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellation).ConfigureAwait(false);
        var client = new FlexClient(tcp)
        {
            _radioVitaEndpoint = new IPEndPoint(IPAddress.Parse(ResolveIp(host)), radioVitaPort),
        };
        client._readLoop = Task.Run(client.ReadLoopAsync);
        await client._prologueComplete.Task.WaitAsync(cancellation).ConfigureAwait(false);
        return client;
    }

    /// <summary>Discovers a radio matching <paramref name="spec"/> on UDP :4992, then
    /// connects to it.</summary>
    public static async Task<FlexClient> DiscoverAndConnectAsync(
        string? spec, TimeSpan timeout, CancellationToken cancellation = default)
    {
        FlexRadioInfo info = await FlexDiscovery.DiscoverAsync(spec, timeout, cancellation)
            .ConfigureAwait(false);
        return await ConnectAsync(info.Ip, info.Port, Vita49.RadioVitaPort, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>Sends a command and awaits the radio's result.</summary>
    public async Task<FlexResult> SendCommandAsync(string command, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        uint seq = NextSeq();
        var tcs = new TaskCompletionSource<FlexResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;
        try
        {
            await WriteLineAsync($"C{seq}|{command}", cancellation).ConfigureAwait(false);
            using (cancellation.Register(() => tcs.TrySetCanceled(cancellation)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.TryRemove(seq, out _);
        }
    }

    /// <summary>Sends a command and throws if the radio reports a non-zero error.</summary>
    public async Task<FlexResult> SendCommandExpectOkAsync(
        string command, CancellationToken cancellation = default)
    {
        FlexResult result = await SendCommandAsync(command, cancellation).ConfigureAwait(false);
        if (!result.IsOk)
        {
            throw new FlexProtocolException(
                $"command '{command}' failed: error 0x{result.Error:X8} {result.Message}");
        }

        return result;
    }

    /// <summary>Sends a command without awaiting its result (e.g. subscriptions).</summary>
    public void SendCommandNoWait(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        uint seq = NextSeq();
        _ = WriteLineAsync($"C{seq}|{command}", CancellationToken.None);
    }

    /// <summary>Binds a local UDP socket and registers it with the radio
    /// (<c>client udpport</c>), then starts the VITA receive loop.</summary>
    public async Task InitUdpAsync(CancellationToken cancellation = default)
    {
        var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        _udp = udp;
        LocalUdpPort = ((IPEndPoint)udp.LocalEndPoint!).Port;
        _udpLoop = Task.Run(UdpLoopAsync);
        await SendCommandExpectOkAsync($"client udpport {LocalUdpPort}", cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// An optional in-process delivery hook for the VITA send path. When set,
    /// <see cref="SendVita"/> hands the packet to this instead of the UDP socket — a
    /// lossless transport used by the mock radio so an offline modem loop does not depend on
    /// kernel UDP buffering (a full OFDM burst is hundreds of DAX packets; one dropped
    /// loopback datagram breaks byte-exact decode). Real radios leave this null (real UDP).
    /// </summary>
    public Action<byte[]>? VitaSendHook { get; set; }

    /// <summary>Sends a VITA-49 packet to the radio's stream socket (or the in-process
    /// <see cref="VitaSendHook"/> when set).</summary>
    public void SendVita(ReadOnlySpan<byte> packet)
    {
        if (VitaSendHook is Action<byte[]> hook)
        {
            hook(packet.ToArray());
            return;
        }

        if (_udp is null || _radioVitaEndpoint is null)
        {
            throw new InvalidOperationException("InitUdpAsync must be called before SendVita");
        }

        _udp.SendTo(packet, SocketFlags.None, _radioVitaEndpoint);
    }

    /// <summary>Delivers a VITA-49 packet as if received on the stream socket, bypassing
    /// UDP — the receive counterpart of <see cref="VitaSendHook"/> for an in-process
    /// transport. Runs the same depacketize/dispatch as the UDP receive loop, so
    /// <see cref="FlexAudioInput"/>'s reorder ring and event plumbing are still exercised.</summary>
    public void DeliverVitaPacket(byte[] packet) => DispatchVitaPacket(packet);

    /// <summary>Enables the radio keepalive and pings once per <paramref name="interval"/>
    /// so a wedged radio is detected (15 s of silence disconnects). Optional; for a
    /// long-running headless node.</summary>
    public async Task EnableKeepaliveAsync(
        TimeSpan interval, CancellationToken cancellation = default)
    {
        await SendCommandExpectOkAsync("keepalive enable", cancellation).ConfigureAwait(false);
        _keepalive = Task.Run(() => KeepaliveLoopAsync(interval));
    }

    /// <summary>Gets a snapshot of an object's current state, if known.</summary>
    public bool TryGetObject(string objectName, out IReadOnlyDictionary<string, string> state)
    {
        lock (_stateLock)
        {
            if (_state.TryGetValue(objectName, out Dictionary<string, string>? found))
            {
                state = new Dictionary<string, string>(found, StringComparer.Ordinal);
                return true;
            }
        }

        state = new Dictionary<string, string>();
        return false;
    }

    /// <summary>Finds the first known object whose name starts with
    /// <paramref name="prefix"/> and whose state satisfies <paramref name="predicate"/>,
    /// returning the object name.</summary>
    public bool TryFindObject(
        string prefix, Func<IReadOnlyDictionary<string, string>, bool> predicate, out string objectName)
    {
        lock (_stateLock)
        {
            foreach ((string name, Dictionary<string, string> state) in _state)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal) && predicate(state))
                {
                    objectName = name;
                    return true;
                }
            }
        }

        objectName = "";
        return false;
    }

    private uint NextSeq() => Interlocked.Increment(ref _cmdSeq);

    private static string ResolveIp(string host)
    {
        if (IPAddress.TryParse(host, out _))
        {
            return host;
        }

        IPAddress[] addresses = Dns.GetHostAddresses(host, AddressFamily.InterNetwork);
        return addresses.Length > 0 ? addresses[0].ToString() : host;
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellation)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(line + "\n");
        await _writeLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
            await _stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[8192];
        var line = new List<byte>(256);
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    byte b = buffer[i];
                    if (b == (byte)'\n')
                    {
                        DispatchLine(Encoding.ASCII.GetString(line.ToArray()).TrimEnd('\r'));
                        line.Clear();
                    }
                    else
                    {
                        line.Add(b);
                    }
                }
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (IOException)
        {
            // Connection dropped.
        }
        finally
        {
            _prologueComplete.TrySetException(
                new FlexProtocolException("connection closed before prologue completed"));
            FailPending(new FlexProtocolException("connection closed"));
        }
    }

    private void DispatchLine(string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        string body = line[1..];
        switch (line[0])
        {
            case 'V':
                Version = body;
                break;
            case 'H':
                Handle = body;
                _prologueComplete.TrySetResult();
                break;
            case 'M':
                ParseMessage(body);
                break;
            case 'S':
                ParseState(body);
                break;
            case 'R':
                ParseResult(body);
                break;
            default:
                break;
        }
    }

    private void ParseMessage(string body)
    {
        int pipe = body.IndexOf('|', StringComparison.Ordinal);
        if (pipe < 0)
        {
            return;
        }

        MessageReceived?.Invoke(body[..pipe], body[(pipe + 1)..]);
    }

    private void ParseResult(string body)
    {
        string[] parts = body.Split('|', 3);
        if (parts.Length < 3
            || !uint.TryParse(parts[0], out uint serial)
            || !uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out uint error))
        {
            return;
        }

        if (_pending.TryGetValue(serial, out TaskCompletionSource<FlexResult>? tcs))
        {
            tcs.TrySetResult(new FlexResult(serial, error, parts[2]));
        }
    }

    private void ParseState(string body)
    {
        int pipe = body.IndexOf('|', StringComparison.Ordinal);
        if (pipe < 0)
        {
            return;
        }

        string handle = body[..pipe];
        string status = body[(pipe + 1)..];
        string[] parts = status.Split(' ');

        string objectName = "";
        var set = new Dictionary<string, string>(StringComparer.Ordinal);
        bool removed = false;

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }

            int eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                if (i == parts.Length - 1 && part == "removed")
                {
                    removed = true;
                }
                else if (parts[0] == "client" && i == 2 && part == "connected")
                {
                    // Not part of the object name.
                }
                else if (parts[0] == "client" && i == 2 && part == "disconnected")
                {
                    removed = true;
                }
                else
                {
                    objectName = objectName.Length == 0 ? part : objectName + " " + part;
                }
            }
            else
            {
                string key = part[..eq];
                string value = part[(eq + 1)..];
                if (parts[0] == "client" && key == "station")
                {
                    value = value.Replace('\x7f', ' ');
                }

                set[key] = value;
            }
        }

        UpdateState(handle, objectName, set, removed);
    }

    private void UpdateState(string handle, string objectName, Dictionary<string, string> changes, bool removed)
    {
        if (objectName.Length == 0)
        {
            return;
        }

        Dictionary<string, string> current;
        lock (_stateLock)
        {
            if (removed)
            {
                _state.Remove(objectName);
                current = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            else
            {
                if (!_state.TryGetValue(objectName, out Dictionary<string, string>? existing))
                {
                    existing = new Dictionary<string, string>(StringComparer.Ordinal);
                    _state[objectName] = existing;
                }

                foreach ((string key, string value) in changes)
                {
                    existing[key] = value;
                }

                current = new Dictionary<string, string>(existing, StringComparer.Ordinal);
            }
        }

        StatusUpdated?.Invoke(new FlexStatusUpdate(handle, objectName, changes, current));
    }

    private async Task UdpLoopAsync()
    {
        Socket udp = _udp!;
        var buffer = new byte[Vita49.MaxVitaPacketSize];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                int received = await udp.ReceiveAsync(buffer, SocketFlags.None, _lifetime.Token)
                    .ConfigureAwait(false);
                if (received > 0)
                {
                    DispatchVitaPacket(buffer.AsSpan(0, received));
                }
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (SocketException)
        {
            // Socket closed.
        }
    }

    private void DispatchVitaPacket(ReadOnlySpan<byte> packet)
    {
        if (!Vita49.TryParsePreamble(packet, out VitaPreamble preamble))
        {
            return;
        }

        Action<VitaPreamble, byte[]>? handler = VitaPacketReceived;
        if (handler is not null)
        {
            byte[] payload = packet.Slice(preamble.PayloadOffset, preamble.PayloadLength).ToArray();
            handler(preamble, payload);
        }
    }

    private async Task KeepaliveLoopAsync(TimeSpan interval)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(interval, _lifetime.Token).ConfigureAwait(false);
                SendCommandNoWait("ping");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (TaskCompletionSource<FlexResult> tcs in _pending.Values)
        {
            tcs.TrySetException(exception);
        }

        _pending.Clear();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;                                            // idempotent — safe to dispose twice (e.g. via an owning FlexStation/FlexWaveform)
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            _udp?.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        _tcp.Close();

        foreach (Task? task in new[] { _readLoop, _udpLoop, _keepalive })
        {
            if (task is not null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best-effort drain.
                }
            }
        }

        _udp?.Dispose();
        _stream.Dispose();
        _tcp.Dispose();
        _writeLock.Dispose();
        _lifetime.Dispose();
    }
}

/// <summary>Raised when the radio reports a command failure or the session breaks.</summary>
public sealed class FlexProtocolException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public FlexProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    public FlexProtocolException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

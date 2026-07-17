using System.Net;
using System.Net.Sockets;
using System.Text;

namespace M0LTE.Flex;

/// <summary>How the mock radio produces DAX-RX audio.</summary>
public enum MockRxMode
{
    /// <summary>Emit no RX audio automatically; the test drives
    /// <see cref="MockFlexRadio.ReplayRxAsync"/> (a captured buffer or a WAV).</summary>
    Silence,

    /// <summary>Echo every captured DAX-TX packet straight back as a DAX-RX packet — a
    /// hardware-free TX↔RX loop (what <c>--device flex:mock</c> uses).</summary>
    Loopback,
}

/// <summary>Which bring-up path the mock radio models — it changes the pre-existing state a
/// <see cref="FlexStation"/> discovers (docs/flex-integration.md §8).</summary>
public enum MockSetupMode
{
    /// <summary>No SmartSDR: no pre-existing client or slice. <c>client gui</c> registers us
    /// (returns a uuid), <c>slice create</c> makes a slice owned by our handle but — modelling
    /// band persistence — reports it on the PERSISTED band (ignoring the create <c>freq</c>),
    /// <c>slice t</c> re-tunes and re-emits <c>RF_frequency</c>, the redundant <c>client bind</c>
    /// is rejected (0x5000003E), and <c>client set station</c> is rejected (0x50000000) — exactly
    /// as M0LTE's FLEX-6500 behaved. The default.</summary>
    Headless,

    /// <summary>SmartSDR is running: a pre-existing <c>client</c> (with a station name) and a
    /// pre-existing <c>slice</c> appear on subscription, and <c>client bind</c> succeeds — the
    /// coexistence/attach path.</summary>
    Attach,
}

/// <summary>
/// An in-process fake FlexRadio 6000-series radio for offline testing: a real TCP+UDP
/// server on 127.0.0.1 that a <see cref="FlexClient"/> connects to exactly like a real
/// radio. It sends the prologue, answers the DAX enable commands, emits <c>client</c>/
/// <c>slice</c>/<c>interlock</c> status, captures our DAX-TX packets (optionally echoing
/// them back as DAX-RX), and can replay a buffer/WAV as DAX-RX. Lets the whole daemon run
/// <c>--device flex:mock</c> and lets a modem loop through it with no hardware
/// (docs/flex-integration.md §5).
/// </summary>
public sealed class MockFlexRadio : IAsyncDisposable
{
    private const string HandleHex = "1A2B3C4D";
    private const uint RxStreamId = 0x04000000;
    private const uint TxStreamId = 0x08000000;

    private const uint RejectedError = 0x50000000;
    private const uint AlreadyBoundError = 0x5000003E;

    /// <summary>The band a fresh headless slice snaps to under <c>band_persistence_enabled=1</c>
    /// — the "last-used band". <c>slice create</c> reports this regardless of the requested
    /// <c>freq</c>; only an explicit <c>slice t</c> moves the slice off it (the real 6500's
    /// behaviour that <see cref="FlexStation.EnsureTunedAsync"/> works around).</summary>
    private const string PersistedBandFrequency = "14.100000";

    private readonly DaxStreamFormat _format;
    private readonly MockRxMode _mode;
    private readonly MockSetupMode _setupMode;
    private readonly string _station;
    private readonly string _sliceLetter;
    private readonly TcpListener _listener;
    private readonly Socket _udp;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _tcpWrite = new(1, 1);
    private readonly List<float> _capturedTx = [];
    private readonly object _captureLock = new();
    private readonly List<string> _commandLog = [];
    private readonly object _commandLogLock = new();

    private NetworkStream? _tcp;
    private IPEndPoint? _clientVita;
    private Task? _acceptLoop;
    private Task? _udpLoop;
    private int _rxCount;

    // The modelled slice frequency. Starts on the persisted band and, headless, IGNORES the
    // `slice create freq=…` request (band persistence); only `slice t` moves it.
    private string _sliceFrequency = PersistedBandFrequency;

    /// <summary>Creates a mock radio serving <paramref name="format"/>.</summary>
    /// <param name="format">The DAX transport the client will use.</param>
    /// <param name="mode">How RX audio is produced (default loopback).</param>
    /// <param name="setupMode">Which bring-up path to model (default headless — no SmartSDR).</param>
    /// <param name="station">The station name the client binds to (attach mode).</param>
    /// <param name="sliceLetter">The slice letter the client attaches to / the created slice's letter.</param>
    public MockFlexRadio(
        DaxStreamFormat format, MockRxMode mode = MockRxMode.Loopback,
        MockSetupMode setupMode = MockSetupMode.Headless,
        string station = "Flex", string sliceLetter = "A")
    {
        ArgumentNullException.ThrowIfNull(format);
        _format = format;
        _mode = mode;
        _setupMode = setupMode;
        _station = station;
        _sliceLetter = sliceLetter;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    /// <summary>The TCP command/status port to connect to.</summary>
    public int TcpPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The radio's UDP VITA port (pass as the client's <c>radioVitaPort</c>).</summary>
    public int UdpPort => ((IPEndPoint)_udp.LocalEndPoint!).Port;

    /// <summary>Every command (the text after the <c>C&lt;seq&gt;|</c> prefix) the client has
    /// sent so far, in order — for tests that assert the bring-up sequence (e.g. that the
    /// headless tune fix issued <c>band_persistence_enabled=0</c> and <c>slice t</c>).</summary>
    public IReadOnlyList<string> CommandLog
    {
        get
        {
            lock (_commandLogLock)
            {
                return _commandLog.ToArray();
            }
        }
    }

    /// <summary>All DAX-TX samples captured from the client so far (at the DAX rate).</summary>
    public IReadOnlyList<float> CapturedTxSamples
    {
        get
        {
            lock (_captureLock)
            {
                return _capturedTx.ToArray();
            }
        }
    }

    /// <summary>Starts the TCP accept loop and the UDP receive loop.</summary>
    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _udpLoop = Task.Run(UdpLoopAsync);
    }

    /// <summary>Writes the captured DAX-TX audio to a WAV file.</summary>
    public void WriteCapturedTxWav(string path)
    {
        lock (_captureLock)
        {
            WavFile.WriteMono(path, _capturedTx.ToArray(), _format.SampleRate);
        }
    }

    /// <summary>
    /// An optional in-process delivery hook for the DAX-RX path (radio→client). When set
    /// (wired to the client's <see cref="FlexClient.DeliverVitaPacket"/>), RX packets — the
    /// loopback echo and <see cref="ReplayRxAsync"/> — are handed over in-process instead of
    /// sent over UDP, so an offline modem loop is lossless and deterministic regardless of
    /// kernel UDP buffering. Left null, RX goes over real UDP (a real radio's transport).
    /// </summary>
    public Action<byte[]>? RxDelivery { get; set; }

    /// <summary>Delivers a client DAX-TX packet in-process (paired with the client's
    /// <see cref="FlexClient.VitaSendHook"/>), bypassing UDP.</summary>
    public void DeliverTxPacket(byte[] packet) => HandleTxPacket(packet);

    /// <summary>Replays a buffer of samples to the client as DAX-RX packets (the last
    /// packet zero-padded). Used to feed captured TX audio or a WAV back in as RX.</summary>
    public async Task ReplayRxAsync(ReadOnlyMemory<float> samples, CancellationToken cancellation = default)
    {
        if (RxDelivery is null && _clientVita is null)
        {
            throw new InvalidOperationException("client udpport not registered yet");
        }

        int spp = _format.SamplesPerPacket;
        var packetBuffer = new float[spp];
        for (int offset = 0; offset < samples.Length; offset += spp)
        {
            int take = Math.Min(spp, samples.Length - offset);
            samples.Span.Slice(offset, take).CopyTo(packetBuffer);
            if (take < spp)
            {
                Array.Clear(packetBuffer, take, spp - take);
            }

            byte[] packet = _format.BuildPacket(RxStreamId, _rxCount, packetBuffer);
            _rxCount = (_rxCount + 1) & 0x0F;
            DeliverRx(packet);
            await Task.Yield();
            cancellation.ThrowIfCancellationRequested();
        }
    }

    private void DeliverRx(byte[] packet)
    {
        if (RxDelivery is Action<byte[]> sink)
        {
            sink(packet);
        }
        else if (_clientVita is not null)
        {
            _udp.SendTo(packet, SocketFlags.None, _clientVita);
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            using TcpClient conn = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
            _tcp = conn.GetStream();

            await WriteLineAsync("V1.4.0.0").ConfigureAwait(false);
            await WriteLineAsync($"H{HandleHex}").ConfigureAwait(false);

            var buffer = new byte[8192];
            var line = new List<byte>(256);
            while (!_lifetime.IsCancellationRequested)
            {
                int read = await _tcp.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        await HandleCommandAsync(Encoding.ASCII.GetString(line.ToArray()).TrimEnd('\r'))
                            .ConfigureAwait(false);
                        line.Clear();
                    }
                    else
                    {
                        line.Add(buffer[i]);
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
            // Client disconnected.
        }
    }

    private async Task HandleCommandAsync(string commandLine)
    {
        if (commandLine.Length < 2 || commandLine[0] != 'C')
        {
            return;
        }

        int pipe = commandLine.IndexOf('|', StringComparison.Ordinal);
        if (pipe < 0)
        {
            return;
        }

        string seq = commandLine[1..pipe].TrimStart('D');
        string cmd = commandLine[(pipe + 1)..];

        lock (_commandLogLock)
        {
            _commandLog.Add(cmd);
        }

        if (cmd == "client gui")
        {
            // Register us as a GUI client — the headless bring-up's first step. The result
            // message carries our client_id (uuid), just as the real radio returns it.
            await WriteLineAsync($"R{seq}|0|mock-uuid-1").ConfigureAwait(false);
        }
        else if (cmd == "sub client all")
        {
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
            if (_setupMode == MockSetupMode.Attach)
            {
                // Only a running SmartSDR presents a pre-existing named client to attach to.
                await WriteLineAsync(
                    $"S{HandleHex}|client 0x{HandleHex} station={_station} client_id=mock-uuid-1")
                    .ConfigureAwait(false);
            }
        }
        else if (cmd == "sub slice all")
        {
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
            if (_setupMode == MockSetupMode.Attach)
            {
                // Attach mode: SmartSDR has already created the slice. Headless mode has none
                // until `slice create` runs (below).
                await WriteLineAsync(
                    $"S{HandleHex}|slice 0 index_letter={_sliceLetter} client_handle=0x{HandleHex} "
                    + "in_use=1 mode=DIGU RF_frequency=14.100000").ConfigureAwait(false);
            }
        }
        else if (cmd.StartsWith("slice create", StringComparison.Ordinal))
        {
            // Headless bring-up: create our own slice, owned by our handle. The radio assigns
            // index_letter (A here) and reports mode back as USB even for a DIGU request — both
            // are fine for the DAX data path (docs/flex-integration.md §8).
            //
            // Band persistence (default-on on a real 6500) makes the radio IGNORE the create
            // `freq` and snap the new slice to the last-used band. Model that faithfully in
            // headless mode: the slice comes up on the PERSISTED band, not the requested freq —
            // so a headless setup only lands on the requested QRG because EnsureTunedAsync then
            // runs `slice t`. Attach mode has no create in its flow, but honour any freq there.
            _sliceFrequency = _setupMode == MockSetupMode.Headless
                ? PersistedBandFrequency
                : ParseArg(cmd, "freq") ?? _sliceFrequency;
            await WriteLineAsync($"R{seq}|0|0").ConfigureAwait(false);
            await WriteLineAsync(
                $"S{HandleHex}|slice 0 index_letter={_sliceLetter} client_handle=0x{HandleHex} "
                + $"in_use=1 mode=USB RF_frequency={_sliceFrequency}").ConfigureAwait(false);
        }
        else if (cmd.StartsWith("slice t ", StringComparison.Ordinal))
        {
            // Explicit tune (flexclient SliceTune form: `slice t <idx> <freq>`). The real radio
            // honours this even with band persistence on — it's the headless tune fix. Update the
            // modelled slice frequency and re-emit RF_frequency so the client's verify sees it.
            string[] tuneParts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tuneParts.Length >= 4)
            {
                _sliceFrequency = tuneParts[3];
            }

            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
            await WriteLineAsync($"S{HandleHex}|slice 0 RF_frequency={_sliceFrequency}")
                .ConfigureAwait(false);
        }
        else if (cmd.StartsWith("client bind ", StringComparison.Ordinal))
        {
            // Attach binds to another (SmartSDR) client and succeeds; a headless client is
            // already the owning GUI client, so the radio rejects the redundant re-bind.
            uint error = _setupMode == MockSetupMode.Attach ? 0 : AlreadyBoundError;
            await WriteLineAsync($"R{seq}|{error:X8}|").ConfigureAwait(false);
        }
        else if (cmd.StartsWith("client set station", StringComparison.Ordinal))
        {
            // The real radio rejects this in the headless flow; the headless path never sends
            // it (we own our slice, not a named station's), but model the rejection faithfully.
            await WriteLineAsync($"R{seq}|{RejectedError:X8}|").ConfigureAwait(false);
        }
        else if (cmd.StartsWith("client udpport ", StringComparison.Ordinal))
        {
            if (int.TryParse(cmd["client udpport ".Length..], out int port))
            {
                _clientVita = new IPEndPoint(IPAddress.Loopback, port);
            }

            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
        }
        else if (cmd.StartsWith("stream create type=dax_rx", StringComparison.Ordinal))
        {
            await WriteLineAsync($"R{seq}|0|{RxStreamId:X8}").ConfigureAwait(false);
        }
        else if (cmd.StartsWith("stream create type=dax_tx", StringComparison.Ordinal))
        {
            await WriteLineAsync($"R{seq}|0|{TxStreamId:X8}").ConfigureAwait(false);
        }
        else if (cmd.StartsWith("slice set ", StringComparison.Ordinal))
        {
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
            if (cmd.Contains("tx=1", StringComparison.Ordinal))
            {
                await WriteLineAsync($"S{HandleHex}|slice 0 tx=1").ConfigureAwait(false);
            }
        }
        else if (cmd == "xmit 1")
        {
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
            await WriteLineAsync($"S{HandleHex}|interlock state=PTT_REQUESTED").ConfigureAwait(false);
            await WriteLineAsync($"S{HandleHex}|interlock state=TRANSMITTING").ConfigureAwait(false);
        }
        else if (cmd == "xmit 0")
        {
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
            await WriteLineAsync($"S{HandleHex}|interlock state=UNKEY_REQUESTED").ConfigureAwait(false);
            await WriteLineAsync($"S{HandleHex}|interlock state=RECEIVE").ConfigureAwait(false);
        }
        else
        {
            // Permissive: bind, set send_reduced_bw_dax, dax audio set, audio stream gain,
            // keepalive enable, ping — all succeed with an empty message.
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
        }
    }

    private static string? ParseArg(string command, string key)
    {
        foreach (string token in command.Split(' '))
        {
            int eq = token.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && token[..eq] == key)
            {
                return token[(eq + 1)..];
            }
        }

        return null;
    }

    private async Task UdpLoopAsync()
    {
        var buffer = new byte[Vita49.MaxVitaPacketSize];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                int received = await _udp.ReceiveAsync(buffer, SocketFlags.None, _lifetime.Token)
                    .ConfigureAwait(false);
                if (received > 0)
                {
                    HandleTxPacket(buffer.AsSpan(0, received));
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

    private void HandleTxPacket(ReadOnlySpan<byte> packet)
    {
        if (!Vita49.TryParsePreamble(packet, out VitaPreamble preamble)
            || preamble.StreamId != TxStreamId)
        {
            return;
        }

        ReadOnlySpan<byte> payload = packet.Slice(preamble.PayloadOffset, preamble.PayloadLength);
        CaptureTx(payload);

        if (_mode == MockRxMode.Loopback)
        {
            // Byte-exact echo: same payload, RX stream id, rolling RX count.
            byte[] echo = Vita49.BuildDaxAudioPacket(_format.StreamClass, RxStreamId, _rxCount, payload);
            _rxCount = (_rxCount + 1) & 0x0F;
            DeliverRx(echo);
        }
    }

    private void CaptureTx(ReadOnlySpan<byte> payload)
    {
        var samples = new float[payload.Length / _format.BytesPerSample];
        _format.Depacketize(payload, samples);
        lock (_captureLock)
        {
            _capturedTx.AddRange(samples);
        }
    }

    private async Task WriteLineAsync(string line)
    {
        NetworkStream? stream = _tcp;
        if (stream is null)
        {
            return;
        }

        byte[] bytes = Encoding.ASCII.GetBytes(line + "\n");
        await _tcpWrite.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, _lifetime.Token).ConfigureAwait(false);
            await stream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _tcpWrite.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            _udp.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        _listener.Stop();
        foreach (Task? task in new[] { _acceptLoop, _udpLoop })
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

        _udp.Dispose();
        _tcpWrite.Dispose();
        _lifetime.Dispose();
    }
}

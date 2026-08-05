using System.Buffers.Binary;
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

    /// <summary>The odd stream id the mock pushes waveform TX buffers on while keyed (odd = the
    /// transmit direction; the client reflects IQ back on the same id — docs/flex-integration.md §9.2).</summary>
    private const uint WaveformTxStreamId = 0x0A000001;

    private const uint RejectedError = 0x50000000;
    private const uint AlreadyBoundError = 0x5000003E;

    /// <summary>What a real 6500 answers to <c>transmit set hi=</c>/<c>lo=</c> — the transmit filter
    /// is REPORTED as <c>lo</c>/<c>hi</c> but can only be SET with <c>filter_low=</c>/
    /// <c>filter_high=</c>. Modelled so that asymmetry is discoverable offline.</summary>
    private const uint TransmitSetterRejectedError = 0x5000002D;

    /// <summary>The radio's hard ceiling on the transmit filter's high cut: values above this are
    /// silently clamped, not rejected (measured on M0LTE's 6500, 2026-07-26).</summary>
    public const int MaxTransmitFilterHighHz = 10000;

    /// <summary>The slice receive passband a fresh DIGU slice comes up on — an ordinary data
    /// filter, and narrow enough that a client wanting more than ~3 kHz has to ask.</summary>
    public const int DefaultSliceFilterLowHz = 100;

    /// <inheritdoc cref="DefaultSliceFilterLowHz" />
    public const int DefaultSliceFilterHighHz = 2800;

    /// <summary>The factory transmit passband — an ordinary 3 kHz SSB filter. This, not the
    /// waveform's own <c>tx_filter</c>, is what caps occupied bandwidth on air, so a waveform client
    /// that never raises it transmits 3 kHz however wide its IQ is.</summary>
    private const int DefaultTransmitFilterHighHz = 3000;

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
    private readonly List<float> _capturedWaveformIq = [];
    private readonly object _captureLock = new();
    private volatile bool _waveformActive;
    private CancellationTokenSource? _waveformPush;
    private int _waveformPushCount;
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

    // The modelled slice mode. `slice create` reports USB (as a real 6500 does even for a DIGU
    // request); `slice set <idx> mode=<m>` moves it.
    private string _sliceMode = "USB";

    // The modelled transmit passband, reported on the `transmit` object as lo/hi.
    private int _txFilterLow;
    private int _txFilterHigh = DefaultTransmitFilterHighHz;
    private int _rfPower = 100;

    // The modelled slice receive passband, reported on the slice object as filter_lo/filter_hi
    // and moved by `slice set <n> filter_lo= filter_hi=`.
    private int _sliceFilterLow = DefaultSliceFilterLowHz;
    private int _sliceFilterHigh = DefaultSliceFilterHighHz;

    /// <summary>
    /// A ceiling this modelled radio applies to a slice's receive filter high cut, or null (the
    /// default) for a radio that takes whatever it is given.
    /// </summary>
    /// <remarks>
    /// Deliberately not a constant: unlike the transmit filter's 10 kHz clamp, nobody has measured
    /// whether a real slice limits its receive width. Modelling "no limit" by default keeps the mock
    /// from asserting something unmeasured, while letting a test set one and exercise a client's
    /// handling of a radio that will not go as wide as asked.
    /// </remarks>
    public int? MaxSliceFilterHighHz { get; set; }

    /// <summary>The operator's power ceiling the modelled radio enforces. A larger
    /// <c>rfpower</c> is rejected outright, as a FLEX-6500 does (fw 4.2.20, error
    /// <c>0x50000048</c>) — it does not clamp and it does not rescale.</summary>
    public int MaxPowerLevel { get; set; } = 100;

    /// <summary>Models a client the radio does not treat as a station: <c>transmit set
    /// rfpower</c> is answered <c>err=0</c> and discarded. Measured on a FLEX-6500 — and
    /// indistinguishable from success without reading the value back.</summary>
    public bool DiscardRfPowerWrites { get; set; }

    /// <summary>Models a LOST keyup race: <c>xmit 1</c> is answered <c>err=0</c> but no
    /// interlock transition follows - the radio granted somebody else the PA. Pair with
    /// <see cref="InjectStatusAsync"/> to script what the phantom winner's status looks
    /// like. For testing <see cref="FlexArbitratedPtt"/>'s confirm step.</summary>
    public bool SuppressInterlockOnXmit { get; set; }

    /// <summary>Writes one raw status line (e.g. <c>S1B2C3D4|interlock state=TRANSMITTING</c>)
    /// to the connected client, verbatim - scripts a phantom second client's traffic, which
    /// the single-session mock cannot otherwise produce. Deliberately NOT a model of
    /// multi-client TX semantics: those are unverified on hardware, and a mock that encoded
    /// guesses would pin them as truth.</summary>
    public Task InjectStatusAsync(string statusLine) => WriteLineAsync(statusLine);

    /// <summary>The transmit RF power the modelled radio is holding.</summary>
    public int RfPower => _rfPower;

    // The transmitter's audio source. A real 6500 defaults to the mic, NOT to DAX — so a client that
    // sets up DAX streams and transmits without selecting DAX puts nothing on air, with every
    // command returning err=0. Modelled at its real default so that mistake is catchable offline.
    private bool _transmitDax;

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

    /// <summary>All interleaved <c>I, Q</c> samples the client reflected back on the waveform TX
    /// stream (the wideband IQ TX path — docs/flex-integration.md §9.2).</summary>
    public IReadOnlyList<float> CapturedWaveformIq
    {
        get
        {
            lock (_captureLock)
            {
                return _capturedWaveformIq.ToArray();
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
        else if (cmd == "sub tx all")
        {
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
            await SendTransmitStatusAsync().ConfigureAwait(false);
        }
        else if (cmd.StartsWith("transmit set ", StringComparison.Ordinal))
        {
            await HandleTransmitSetAsync(seq, cmd["transmit set ".Length..]).ConfigureAwait(false);
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
                    + "in_use=1 mode=DIGU RF_frequency=14.100000 "
                    + $"filter_lo={_sliceFilterLow} filter_hi={_sliceFilterHigh}").ConfigureAwait(false);
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
                + $"in_use=1 mode={_sliceMode} RF_frequency={_sliceFrequency} "
                + $"filter_lo={_sliceFilterLow} filter_hi={_sliceFilterHigh}").ConfigureAwait(false);
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

            // Reflect a mode change in the slice's status. Acknowledging `mode=` and then still
            // reporting the old mode is the band-persistence trap in another costume: a client that
            // reads the mode back to confirm its waveform engaged would see a false failure here, and
            // one that trusts err=0 alone would miss a real one.
            int modeAt = cmd.IndexOf("mode=", StringComparison.Ordinal);
            if (modeAt >= 0)
            {
                string mode = cmd[(modeAt + 5)..].Split(' ')[0];
                _sliceMode = mode;
                await WriteLineAsync($"S{HandleHex}|slice 0 mode={mode}").ConfigureAwait(false);
            }

            // The receive passband, likewise reflected rather than merely acknowledged — a client
            // that widens its filter and reads back to confirm is doing the right thing, and has to
            // be able to tell "the radio moved it" from "the radio said err=0 and did nothing".
            string? filterLo = ParseArg(cmd, "filter_lo");
            string? filterHi = ParseArg(cmd, "filter_hi");
            if (filterLo is not null && int.TryParse(filterLo, out int lowHz))
            {
                _sliceFilterLow = lowHz;
            }

            if (filterHi is not null && int.TryParse(filterHi, out int highHz))
            {
                // Clamped rather than refused where a ceiling is modelled at all, mirroring how the
                // transmit filter behaves — the failure mode a client has to notice is a silent one.
                _sliceFilterHigh = MaxSliceFilterHighHz is int ceiling
                    ? Math.Min(highHz, ceiling)
                    : highHz;
            }

            if (filterLo is not null || filterHi is not null)
            {
                await WriteLineAsync(
                    $"S{HandleHex}|slice 0 filter_lo={_sliceFilterLow} filter_hi={_sliceFilterHigh}")
                    .ConfigureAwait(false);
            }
        }
        else if (cmd.StartsWith("waveform create", StringComparison.Ordinal))
        {
            // A custom waveform registers — remember it so a subsequent key streams TX buffers for
            // it (the wideband IQ TX path). `waveform set …` falls through to the permissive branch.
            _waveformActive = true;
            // A real 6500 begins asking for transmit buffers as soon as a slice is in a
            // waveform mode and never stops — measured at the full 187.5/s, keyed or not, and
            // continuing indefinitely after xmit 0. Modelling that here is what lets an
            // offline test catch a client that answers when it should not: one that drains
            // its own transmit ring around the clock, so a queued burst is discarded before
            // the PA ever comes up.
            StartWaveformPush();
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
        }
        else if (cmd.StartsWith("waveform remove", StringComparison.Ordinal))
        {
            _waveformActive = false;
            StopWaveformPush();
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
        }
        else if (cmd == "meter list")
        {
            // The metadata table in the radio's own '#'-separated form, modelled on a real
            // FLEX-6500 (firmware v1.4.0.0) — including the awkward parts a parser must
            // survive: a meter name containing both '.' and '+', negative bounds, and
            // descriptions with spaces in them.
            await WriteLineAsync($"R{seq}|0|{MeterListReply}").ConfigureAwait(false);
        }
        else if (cmd == "xmit 1")
        {
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
            if (!SuppressInterlockOnXmit)
            {
                await WriteLineAsync($"S{HandleHex}|interlock state=PTT_REQUESTED").ConfigureAwait(false);
                await WriteLineAsync($"S{HandleHex}|interlock state=TRANSMITTING").ConfigureAwait(false);
            }
        }
        else if (cmd == "xmit 0")
        {
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
            await WriteLineAsync($"S{HandleHex}|interlock state=UNKEY_REQUESTED").ConfigureAwait(false);

            // The DAX path returns to RECEIVE and is modelled as such. The WAVEFORM path does
            // not: on a real 6500 the interlock reports transitions up to UNKEY_REQUESTED and
            // never announces its return, while the radio carries on requesting transmit
            // buffers. Emitting a tidy RECEIVE there would let a client wait on a signal the
            // radio does not send — which is exactly the mistake this models away.
            if (!_waveformActive)
            {
                await WriteLineAsync($"S{HandleHex}|interlock state=RECEIVE").ConfigureAwait(false);
            }
        }
        else
        {
            // Permissive: bind, set send_reduced_bw_dax, dax audio set, audio stream gain,
            // keepalive enable, ping — all succeed with an empty message.
            await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Models <c>transmit set</c>. Only <c>filter_low=</c>/<c>filter_high=</c> move the transmit
    /// passband; the <c>lo=</c>/<c>hi=</c> spellings that appear in the *status* are rejected, and a
    /// high cut above <see cref="MaxTransmitFilterHighHz"/> is silently clamped rather than refused.
    /// </summary>
    private async Task HandleTransmitSetAsync(string seq, string arguments)
    {
        foreach (string token in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                continue;
            }

            string key = token[..eq];
            string value = token[(eq + 1)..];

            // Rejecting these is the point: a client that sets what it reads back gets an error
            // rather than silence, so the asymmetry is discoverable without a radio.
            if (key is "lo" or "hi")
            {
                await WriteLineAsync($"R{seq}|{TransmitSetterRejectedError:X8}|").ConfigureAwait(false);
                return;
            }

            if (!int.TryParse(value, out int number))
            {
                continue;
            }

            switch (key)
            {
                case "dax": _transmitDax = number != 0; break;
                case "filter_low": _txFilterLow = number; break;
                case "filter_high": _txFilterHigh = Math.Min(number, MaxTransmitFilterHighHz); break;
                case "rfpower":
                    if (number > MaxPowerLevel)
                    {
                        await WriteLineAsync($"R{seq}|50000048|").ConfigureAwait(false);
                        return;
                    }

                    if (!DiscardRfPowerWrites)
                    {
                        _rfPower = number;
                    }

                    break;
                default: break;
            }
        }

        await WriteLineAsync($"R{seq}|0|").ConfigureAwait(false);
        await SendTransmitStatusAsync().ConfigureAwait(false);
    }

    private Task SendTransmitStatusAsync() => WriteLineAsync(
        $"S{HandleHex}|transmit lo={_txFilterLow} hi={_txFilterHigh} tx_filter_changes_allowed=1 "
        + $"rfpower={_rfPower} max_power_level={MaxPowerLevel} tune=0 tx_slice_mode={_sliceMode} "
        + $"dax={(_transmitDax ? 1 : 0)} mic_selection=PC");

    /// <summary>Whether the modelled transmitter is taking its audio from DAX.</summary>
    public bool TransmitSourceIsDax => _transmitDax;

    /// <summary>The transmit passband the mock currently models, as (low, high) in Hz.</summary>
    public (int Low, int High) TransmitFilter => (_txFilterLow, _txFilterHigh);

    /// <summary>The slice receive passband the mock currently models, as (low, high) in Hz.</summary>
    public (int Low, int High) SliceFilter => (_sliceFilterLow, _sliceFilterHigh);

    /// <summary>The <c>meter list</c> reply the mock serves — a real FLEX-6500's transmit
    /// meter set, ids and all.</summary>
    public const string MeterListReply =
        "meter 1.src=COD-#1.num=1#1.nam=MICPEAK#1.low=-150.0#1.hi=20.0#1.desc=Signal strength of MIC output in CODEC#1.unit=dBFS#1.fps=40#" +
        "3.src=TX-#3.num=5#3.nam=HWALC#3.low=-150.0#3.hi=20.0#3.desc=Voltage present at the Hardware ALC RCA Plug#3.unit=dBFS#3.fps=20#" +
        "4.src=RAD#4.num=208#4.nam=+13.8A#4.low=10.5#4.hi=15.0#4.desc=Main radio input voltage before fuse#4.unit=Volts#4.fps=0#" +
        "6.src=TX-#6.num=1#6.nam=FWDPWR#6.low=0.0#6.hi=53.0#6.desc=RF Power Forward#6.unit=dBm#6.fps=20#" +
        "7.src=TX-#7.num=2#7.nam=REFPWR#7.low=0.0#7.hi=53.0#7.desc=RF Power Reflected#7.unit=dBm#7.fps=20#" +
        "8.src=TX-#8.num=3#8.nam=SWR#8.low=1.0#8.hi=999.0#8.desc=RF SWR#8.unit=SWR#8.fps=20#" +
        "9.src=TX-#9.num=4#9.nam=PATEMP#9.low=0.0#9.hi=120.0#9.desc=PA Temperature#9.unit=degC#9.fps=0#";

    /// <summary>
    /// Pushes one meter extension packet carrying the given (id, raw) pairs, in the exact
    /// shape a FLEX-6500 emits: extension-data-with-stream, class id present, <b>both</b>
    /// timestamp fields set — a 28-byte preamble. That preamble length is the whole point:
    /// it is what made the double-applied-offset bug bite, so the mock reproduces it rather
    /// than emitting a convenient header no real radio sends.
    /// </summary>
    public void PushMeters(params (int Id, short Raw)[] meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var packet = new byte[28 + (4 * meters.Length)];
        uint header = (3u << 28)          // ExtDataWithStream
            | 0x08000000u                 // C: class id present
            | (1u << 22)                  // TSI
            | (1u << 20)                  // TSF
            | (uint)(packet.Length / 4);
        BinaryPrimitives.WriteUInt32BigEndian(packet, header);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MeterStreamId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), Vita49.FlexOui);
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(12), ((uint)Vita49.FlexInformationClass << 16) | Vita49.MeterClass);
        for (int i = 0; i < meters.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(28 + (4 * i)), (ushort)meters[i].Id);
            BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(30 + (4 * i)), meters[i].Raw);
        }

        DeliverRx(packet);
    }

    /// <summary>The stream id the mock sends meter packets on (a real 6500 uses 0x00000700).</summary>
    public const uint MeterStreamId = 0x00000700;

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
        if (!Vita49.TryParsePreamble(packet, out VitaPreamble preamble))
        {
            return;
        }

        ReadOnlySpan<byte> payload = packet.Slice(preamble.PayloadOffset, preamble.PayloadLength);

        if (preamble.StreamId == WaveformTxStreamId)
        {
            // A waveform TX reflection: capture the interleaved I/Q (always full-bandwidth float32).
            var iq = new float[payload.Length / 4];
            DaxStreamFormat.FullBandwidth.Depacketize(payload, iq);
            lock (_captureLock)
            {
                _capturedWaveformIq.AddRange(iq);
            }

            return;
        }

        if (preamble.StreamId != TxStreamId)
        {
            return;
        }

        CaptureTx(payload);

        if (_mode == MockRxMode.Loopback)
        {
            // Byte-exact echo: same payload, RX stream id, rolling RX count.
            byte[] echo = Vita49.BuildDaxAudioPacket(_format.StreamClass, RxStreamId, _rxCount, payload);
            _rxCount = (_rxCount + 1) & 0x0F;
            DeliverRx(echo);
        }
    }

    // While keyed with a waveform registered, stream TX buffers to the client the way the radio does
    // (odd stream id, full-bandwidth IF-data class, silent "mic" payload) so the client's
    // FlexWaveformIqOutput reflects its buffered IQ back for HandleTxPacket to capture.
    private void StartWaveformPush()
    {
        StopWaveformPush();
        var cts = new CancellationTokenSource();
        _waveformPush = cts;
        _ = Task.Run(() => WaveformPushLoopAsync(cts.Token));
    }

    private void StopWaveformPush()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _waveformPush, null);
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task WaveformPushLoopAsync(CancellationToken cancellation)
    {
        const int SamplesPerPacket = 128;
        var silence = new float[SamplesPerPacket * 2]; // 128 complex "mic" samples the radio hands the waveform

        // Pace at the real 6500's rate — 187.5 packets/s, i.e. 128 complex samples every 5⅓ ms, which
        // is exactly FlexWaveformIqOutput.SampleRate. The deadline is computed from the start rather
        // than slept per iteration, so coarse timer granularity averages out instead of accumulating.
        //
        // This has to be right: a mock that pulls faster than 24 kHz starves *any* correctly paced
        // producer, so a burst comes back zero-filled at the gaps and an offline test reports
        // splatter that no real radio would produce.
        long start = Environment.TickCount64;
        long sent = 0;

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                byte[] buffer = DaxStreamFormat.FullBandwidth.BuildPacket(WaveformTxStreamId, _waveformPushCount, silence);
                _waveformPushCount = (_waveformPushCount + 1) & 0x0F;
                DeliverRx(buffer);
                sent++;

                long due = start + (sent * 1000L * SamplesPerPacket / FlexWaveformIqOutput.SampleRate);
                long wait = due - Environment.TickCount64;
                if (wait > 0)
                {
                    await Task.Delay((int)wait, cancellation).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Unkeyed.
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
        StopWaveformPush();
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

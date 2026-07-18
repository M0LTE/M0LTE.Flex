using System.Globalization;

namespace M0LTE.Flex;

/// <summary>Parameters for a DAX-IQ receive stream.</summary>
/// <param name="FrequencyMHz">Panadapter/DAX-IQ centre frequency, six-decimal Flex form
/// (e.g. <c>"14.100000"</c>). A consumer's channel offsets are relative to this.</param>
/// <param name="Antenna">RX antenna port (<c>ANT1</c>/<c>ANT2</c>/<c>RX_A</c>/…).</param>
/// <param name="DaxChannel">DAX-IQ channel (1–4). Default 2 — a running SmartSDR/DAX-audio client
/// tends to hold channel 1.</param>
/// <param name="RateKsps">DAX-IQ sample rate in kSPS: 24, 48, 96 or 192. Wider = more spectrum to
/// fan out, heavier LAN.</param>
/// <param name="RingSeconds">Depth of the reorder/jitter ring, in seconds of IQ.</param>
public readonly record struct FlexDaxIqOptions(
    string FrequencyMHz = "14.100000",
    string Antenna = "ANT1",
    int DaxChannel = 2,
    int RateKsps = 96,
    double RingSeconds = 0.5);

/// <summary>
/// A wideband <see cref="IIqSource"/> backed by a FlexRadio <b>DAX-IQ</b> receive stream — the live
/// transport a multi-channel receiver fans out to decode several channels at once from one slice.
/// Brings the stream up headlessly over the shared <see cref="FlexClient"/> command session (no
/// SmartSDR), then depacketizes the pushed VITA into interleaved host-endian <c>I, Q</c> floats via
/// a <see cref="DaxIqStreamBuffer"/>.
/// </summary>
/// <remarks>
/// Bring-up (proven on M0LTE's 6500): <c>client gui</c> → <c>display pan c freq= ant=</c> →
/// <c>display pan s &lt;pan&gt; daxiq_channel=</c> → <c>dax iq set &lt;ch&gt; pan= rate=</c> →
/// <c>stream create type=dax_iq</c>. The radio then streams the DAX-IQ VITA (wide classes
/// <c>0x02E3/E4/E5/E6</c>) to our UDP port; we subscribe to <see cref="FlexClient.VitaPacketReceived"/>
/// and route packets matching our stream id into the buffer.
/// </remarks>
public sealed class FlexDaxIqSource : IIqSource, IAsyncDisposable
{
    private readonly FlexClient _client;
    private readonly bool _ownsClient;
    private readonly uint _streamId;
    private readonly string? _panId;
    private readonly DaxIqStreamBuffer _buffer;
    private readonly Action<VitaPreamble, byte[]> _onVita;
    private int _disposed;

    private FlexDaxIqSource(
        FlexClient client, bool ownsClient, uint streamId, string? panId,
        int sampleRate, double centreHz, DaxIqStreamBuffer buffer)
    {
        _client = client;
        _ownsClient = ownsClient;
        _streamId = streamId;
        _panId = panId;
        SampleRate = sampleRate;
        CentreFrequencyHz = centreHz;
        _buffer = buffer;
        _onVita = OnVita;
        client.VitaPacketReceived += _onVita;
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public double CentreFrequencyHz { get; }

    /// <summary>The DAX-IQ VITA stream id the radio tags our packets with.</summary>
    public uint StreamId => _streamId;

    /// <summary>DAX-IQ VITA packets received on our stream.</summary>
    public long PacketsReceived => _buffer.PacketsReceived;

    /// <summary>Packets the VITA counter says were dropped in flight.</summary>
    public long PacketsLost => _buffer.PacketsLost;

    /// <summary>Brings up a DAX-IQ stream over <paramref name="client"/> and starts buffering it.
    /// The client must already be connected (its UDP receive loop live); we register the UDP port,
    /// become a GUI client, create a panadapter and bind DAX-IQ to it.</summary>
    /// <param name="client">A connected <see cref="FlexClient"/>.</param>
    /// <param name="options">Frequency/antenna/channel/rate.</param>
    /// <param name="ownsClient">True to dispose the client when this source is disposed.</param>
    /// <param name="cancellation">Cancels the bring-up.</param>
    public static async Task<FlexDaxIqSource> OpenAsync(
        FlexClient client, FlexDaxIqOptions options, bool ownsClient = false, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (options.RateKsps is not (24 or 48 or 96 or 192))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.RateKsps, "DAX-IQ rate must be 24, 48, 96 or 192 kSPS");
        }

        await client.InitUdpAsync(cancellation).ConfigureAwait(false);
        // Become a GUI client so we may create a panadapter — best-effort: harmless if we already are.
        await client.SendCommandAsync("client gui", cancellation).ConfigureAwait(false);

        FlexResult pan = await client.SendCommandAsync(
            $"display pan c freq={options.FrequencyMHz} ant={options.Antenna}", cancellation).ConfigureAwait(false);
        if (!pan.IsOk)
        {
            throw new FlexProtocolException($"DAX-IQ: 'display pan c' failed (0x{pan.Error:X8} {pan.Message})");
        }

        string panId = pan.Message.Split(',')[0].Trim();
        await ExpectOk(client, $"display pan s {panId} daxiq_channel={options.DaxChannel}", cancellation).ConfigureAwait(false);
        await ExpectOk(client, $"dax iq set {options.DaxChannel} pan={panId} rate={options.RateKsps}", cancellation).ConfigureAwait(false);

        FlexResult stream = await client.SendCommandAsync(
            $"stream create type=dax_iq daxiq_channel={options.DaxChannel}", cancellation).ConfigureAwait(false);
        if (!stream.IsOk)
        {
            throw new FlexProtocolException($"DAX-IQ: 'stream create' failed (0x{stream.Error:X8} {stream.Message})");
        }

        uint streamId = ParseStreamId(stream.Message);
        await ExpectOk(client, $"stream set 0x{streamId:X8} daxiq_rate={options.RateKsps}", cancellation).ConfigureAwait(false);

        int sampleRate = options.RateKsps * 1000;
        double centreHz = double.Parse(options.FrequencyMHz, CultureInfo.InvariantCulture) * 1e6;
        int capacityPairs = Math.Max(sampleRate / 4, (int)(sampleRate * options.RingSeconds));
        return new FlexDaxIqSource(client, ownsClient, streamId, panId, sampleRate, centreHz, new DaxIqStreamBuffer(capacityPairs));
    }

    /// <inheritdoc />
    public int Read(Span<float> interleaved) => _buffer.Read(interleaved);

    private void OnVita(VitaPreamble preamble, byte[] packet)
    {
        if (preamble.StreamId != _streamId)
        {
            return;
        }

        int offset = preamble.PayloadOffset;
        if (offset < 0 || offset >= packet.Length)
        {
            return;
        }

        // Clamp to the bytes actually present rather than trusting the reported length outright — a
        // DAX-IQ packet carries a 4-byte VITA trailer past the IQ, and the buffer consumes only whole
        // 8-byte I/Q pairs, so a slightly-long span is harmless while an over-read would throw.
        int available = packet.Length - offset;
        int length = preamble.PayloadLength > 0 ? Math.Min(preamble.PayloadLength, available) : available;
        _buffer.Ingest(preamble.PacketCount, packet.AsSpan(offset, length));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _client.VitaPacketReceived -= _onVita;
        _buffer.Stop();

        // Best-effort teardown of the radio-side plumbing.
        try
        {
            await _client.SendCommandAsync($"stream remove 0x{_streamId:X8}", CancellationToken.None).ConfigureAwait(false);
            if (_panId is not null)
            {
                await _client.SendCommandAsync($"display pan r {_panId}", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // teardown is best-effort — the client may already be gone
        }

        if (_ownsClient)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task ExpectOk(FlexClient client, string command, CancellationToken cancellation)
    {
        FlexResult r = await client.SendCommandAsync(command, cancellation).ConfigureAwait(false);
        if (!r.IsOk)
        {
            throw new FlexProtocolException($"DAX-IQ: '{command}' failed (0x{r.Error:X8} {r.Message})");
        }
    }

    private static uint ParseStreamId(string message)
    {
        string s = message.Trim();
        int comma = s.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            s = s[..comma];
        }

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}

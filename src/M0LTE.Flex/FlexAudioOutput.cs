using M0LTE.Radio.Audio;
using System.Diagnostics;

namespace M0LTE.Flex;

/// <summary>
/// A FlexRadio DAX-TX audio sink: accumulates float samples into full DAX packets,
/// converts to the transport's big-endian format and sends them over UDP. Presented as an
/// <see cref="IAudioOutput"/> at <see cref="SampleRate"/> (the DAX rate); resample to it if
/// your DSP runs at a lower rate. Unlike a sound card there is no device to block on, so
/// pacing is off the sample clock: a full packet's worth of audio is metered out at the DAX
/// rate, and <see cref="Drain"/> (the sample-domain part of PTT release) flushes the last
/// partial packet and waits out its airtime.
/// </summary>
/// <remarks>
/// The packet layout and the continuous modulo-16 packet counter follow nDAX
/// <c>streamFromPulse</c> (© Andrew Rodland KC2G, MIT); nDAX skips all-zero packets and
/// sleeps 1 ms/packet, whereas we send every packet and pace off the sample clock (the
/// transmitter hands us a whole frame at once, so there is no upstream real-time gate).
/// </remarks>
public sealed class FlexAudioOutput : IAudioOutput
{
    private readonly FlexClient _client;
    private readonly uint _streamId;
    private readonly DaxStreamFormat _format;
    private readonly bool _paceRealTime;
    private readonly float[] _accumulator;
    private readonly Stopwatch _clock = new();

    private int _accumulated;
    private int _packetCount;
    private long _samplesSent;

    /// <summary>Creates a DAX-TX sink for <paramref name="streamId"/>.</summary>
    /// <param name="client">The shared session (UDP already initialised).</param>
    /// <param name="streamId">The DAX-TX stream id from <c>stream create type=dax_tx</c>.</param>
    /// <param name="format">The DAX transport format.</param>
    /// <param name="paceRealTime">Meter packets out at the DAX rate (true for hardware;
    /// tests that loop through the mock can disable it for speed).</param>
    public FlexAudioOutput(FlexClient client, uint streamId, DaxStreamFormat format, bool paceRealTime = true)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(format);
        _client = client;
        _streamId = streamId;
        _format = format;
        _paceRealTime = paceRealTime;
        _accumulator = new float[format.SamplesPerPacket];
    }

    /// <inheritdoc />
    public int SampleRate => _format.SampleRate;

    /// <summary>Total DAX packets sent (diagnostics).</summary>
    public long PacketsSent { get; private set; }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<float> samples)
    {
        int i = 0;
        while (i < samples.Length)
        {
            int need = _format.SamplesPerPacket - _accumulated;
            int take = Math.Min(need, samples.Length - i);
            samples.Slice(i, take).CopyTo(_accumulator.AsSpan(_accumulated));
            _accumulated += take;
            i += take;

            if (_accumulated == _format.SamplesPerPacket)
            {
                SendPacket(_accumulator);
                _accumulated = 0;
            }
        }
    }

    /// <inheritdoc />
    public void Drain()
    {
        if (_accumulated > 0)
        {
            // Flush the last partial packet, zero-padded to a full packet.
            Array.Clear(_accumulator, _accumulated, _format.SamplesPerPacket - _accumulated);
            SendPacket(_accumulator);
            _accumulated = 0;
        }

        if (_paceRealTime && _clock.IsRunning)
        {
            double dueMs = 1000.0 * _samplesSent / _format.SampleRate;
            double remainMs = dueMs - _clock.Elapsed.TotalMilliseconds;
            if (remainMs > 0)
            {
                Thread.Sleep((int)Math.Ceiling(remainMs));
            }
        }

        // Reset the airtime clock for the next transmission; the packet counter rolls on.
        _clock.Reset();
        _samplesSent = 0;
    }

    private void SendPacket(ReadOnlySpan<float> packetSamples)
    {
        byte[] packet = _format.BuildPacket(_streamId, _packetCount, packetSamples);
        _packetCount = (_packetCount + 1) & 0x0F;
        _client.SendVita(packet);
        _samplesSent += _format.SamplesPerPacket;
        PacketsSent++;
        Pace();
    }

    private void Pace()
    {
        if (!_paceRealTime)
        {
            return;
        }

        if (!_clock.IsRunning)
        {
            _clock.Start();
        }

        double dueMs = 1000.0 * _samplesSent / _format.SampleRate;
        double aheadMs = dueMs - _clock.Elapsed.TotalMilliseconds;
        if (aheadMs > 1.0)
        {
            Thread.Sleep((int)aheadMs);
        }
    }
}

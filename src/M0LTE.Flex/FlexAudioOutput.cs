using M0LTE.Radio.Audio;
using System.Diagnostics;
using System.Globalization;

namespace M0LTE.Flex;

/// <summary>
/// A FlexRadio DAX-TX audio sink: accumulates float samples into full DAX packets,
/// converts to the transport's big-endian format and sends them over UDP. Presented as an
/// <see cref="IAudioOutput"/> at <see cref="SampleRate"/> (the DAX rate); resample to it if
/// your DSP runs at a lower rate. Unlike a sound card there is no device to block on, so
/// pacing is off the sample clock: packets go out at the DAX rate, kept a fixed
/// <see cref="TransmitLead"/> ahead of the radio's playout, and <see cref="Drain"/> (the
/// sample-domain part of PTT release) flushes the last partial packet and waits until the radio
/// has played everything it was sent.
/// </summary>
/// <remarks>
/// <para><b>Why a lead.</b> Paced one packet at a time (5.3 ms at 24 kHz), the radio never holds
/// more than about one packet beyond its own small buffer, so any hold-up of the sending thread
/// longer than that (a late wake-up on a busy host, a garbage collection) leaves it with nothing
/// to send: a hole of band noise mid-burst, and the rest of the burst played late by the length
/// of the hole. Seen on the air as 25 to 38 ms holes in long bursts from a headless FLEX-6500.
/// With a lead, a hold shorter than the lead costs nothing on the air.</para>
/// <para><b>How it paces.</b> After each packet the sink compares the audio sent with the time
/// since the keyup's first packet, and sleeps only while it is more than the lead ahead. The
/// first lead's worth therefore goes out at once, and from then on one packet goes out as one
/// packet's worth falls due, so the radio holds between the lead and the lead plus one packet.
/// After a hold longer than the lead the radio has run dry and plays the rest late by the
/// shortfall, so the sink moves its clock late by the same amount: the lead does not grow
/// across a stall, and <see cref="Drain"/> waits for the radio's real end, not for when the
/// audio would have ended without the stall, so the burst's end is not cut off by the unkey.</para>
/// <para><b>What is known and what is assumed about the radio.</b> Known: a radio that ran dry
/// and was then sent the overdue packets back to back played them all, in order, late by the
/// hole, to the end of the burst (seen in remote recordings of the holes above), so the radio
/// buffered at least about 40 ms sent early without dropping or time-compressing it. nDAX sends
/// as fast as PulseAudio hands it audio, with 1 ms between packets, which can run well ahead of
/// real time. AetherSDR's AX.25 modem keeps the radio's DAX-TX buffer 120 ms ahead of real time
/// for the same reason as this sink. Assumed: that the radio plays from the first packet that
/// arrives and does not drop audio held below about 100 ms; FlexRadio does not document the
/// DAX-TX buffer depth, or what it does with audio still queued when the transmitter unkeys.
/// Hence the default of 80 ms: above the worst hold seen on the air (38 ms) and the 40 to 55 ms
/// stalls AetherSDR measured, and well inside the 120 ms AetherSDR runs.</para>
/// <para><b>Turning it off.</b> The environment variable named by
/// <see cref="TransmitLeadVariable"/> overrides the default, read once when the sink is created:
/// whole milliseconds from 0 to 500, where 0 or <c>off</c> restores the one-packet pacing of
/// earlier versions exactly. Any other value is ignored and the default used, so read
/// <see cref="TransmitLead"/> back to see which is in force.</para>
/// <para>The packet layout and the continuous modulo-16 packet counter follow nDAX
/// <c>streamFromPulse</c> (© Andrew Rodland KC2G, MIT); nDAX skips all-zero packets and
/// sleeps 1 ms/packet, whereas we send every packet and pace off the sample clock (the
/// transmitter hands us a whole frame at once, so there is no upstream real-time gate).</para>
/// </remarks>
public sealed class FlexAudioOutput : IAudioOutput
{
    /// <summary>The environment variable that overrides <see cref="DefaultTransmitLead"/>:
    /// whole milliseconds, 0 or <c>off</c> for the one-packet pacing of earlier versions.</summary>
    public const string TransmitLeadVariable = "M0LTE_FLEX_TX_LEAD_MS";

    /// <summary>How far ahead of the radio's playout the sink keeps its audio unless
    /// <see cref="TransmitLeadVariable"/> says otherwise.</summary>
    public static readonly TimeSpan DefaultTransmitLead = TimeSpan.FromMilliseconds(80);

    /// <summary>The largest lead <see cref="TransmitLeadVariable"/> may ask for.</summary>
    internal const int MaxTransmitLeadMilliseconds = 500;

    private readonly Action<byte[]> _send;
    /// <summary>Read per packet, so a rebuilt slice's new DAX-TX stream is used without this
    /// sink being recreated.</summary>
    private readonly Func<uint> _streamId;
    private readonly DaxStreamFormat _format;
    private readonly bool _paceRealTime;
    private readonly double _leadMs;
    private readonly Func<long> _nowTicks;
    private readonly Action<int> _sleep;
    private readonly float[] _accumulator;

    private int _accumulated;
    private int _packetCount;
    private long _samplesSent;
    private bool _clockRunning;
    /// <summary>When the radio's playout of this keyup's audio began, in the clock's ticks,
    /// moved late by every shortfall the radio has had to absorb.</summary>
    private long _originTicks;

    /// <summary>Creates a DAX-TX sink for <paramref name="streamId"/>.</summary>
    /// <param name="client">The shared session (UDP already initialised).</param>
    /// <param name="streamId">The DAX-TX stream id from <c>stream create type=dax_tx</c>.</param>
    /// <param name="format">The DAX transport format.</param>
    /// <param name="paceRealTime">Meter packets out at the DAX rate (true for hardware;
    /// tests that loop through the mock can disable it for speed).</param>
    public FlexAudioOutput(FlexClient client, uint streamId, DaxStreamFormat format, bool paceRealTime = true)
        : this(
            client,
            new FlexSliceLease(new FlexSliceBinding("", "", 0, streamId, 1, IsValid: true)),
            format,
            paceRealTime)
    {
    }

    /// <summary>Creates a DAX-TX sink for the stream named by <paramref name="lease"/>, following
    /// it across a rebuild.</summary>
    /// <param name="client">The shared session (UDP already initialised).</param>
    /// <param name="lease">The station's slice lease.</param>
    /// <param name="format">The DAX transport format.</param>
    /// <param name="paceRealTime">Meter packets out at the DAX rate (true for hardware;
    /// tests that loop through the mock can disable it for speed).</param>
    public FlexAudioOutput(
        FlexClient client, FlexSliceLease lease, DaxStreamFormat format, bool paceRealTime = true)
        : this(
            SendThrough(client),
            StreamOf(lease),
            format,
            paceRealTime,
            ParseTransmitLead(Environment.GetEnvironmentVariable(TransmitLeadVariable)),
            nowTicks: null,
            sleep: null)
    {
    }

    /// <summary>The seam the public constructors and the tests share.</summary>
    /// <param name="send">Hands one built packet to the radio.</param>
    /// <param name="streamId">The DAX-TX stream id, read per packet.</param>
    /// <param name="format">The DAX transport format.</param>
    /// <param name="paceRealTime">Meter packets out at the DAX rate.</param>
    /// <param name="transmitLead">How far ahead of the radio's playout to keep; zero for the
    /// one-packet pacing of earlier versions.</param>
    /// <param name="nowTicks">The clock, in <see cref="TimeSpan"/> ticks; the system's
    /// monotonic clock when null.</param>
    /// <param name="sleep">Blocks for whole milliseconds; <see cref="Thread.Sleep(int)"/> when
    /// null.</param>
    internal FlexAudioOutput(
        Action<byte[]> send, Func<uint> streamId, DaxStreamFormat format, bool paceRealTime,
        TimeSpan transmitLead, Func<long>? nowTicks, Action<int>? sleep)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfLessThan(transmitLead, TimeSpan.Zero);
        _send = send;
        _streamId = streamId;
        _format = format;
        _paceRealTime = paceRealTime;
        TransmitLead = transmitLead;
        _leadMs = transmitLead.TotalMilliseconds;
        _nowTicks = nowTicks ?? SystemTicks;
        _sleep = sleep ?? Thread.Sleep;
        _accumulator = new float[format.SamplesPerPacket];
    }

    /// <inheritdoc />
    public int SampleRate => _format.SampleRate;

    /// <summary>How far ahead of the radio's playout this sink keeps its audio: the default, or
    /// what <see cref="TransmitLeadVariable"/> asked for. Zero is the one-packet pacing of
    /// earlier versions.</summary>
    public TimeSpan TransmitLead { get; }

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
    /// <remarks>Returns once the radio has been given time to play everything sent, on a clock
    /// that has followed it late across any stall, so nothing sent is left queued at the radio
    /// for the unkey to cut off or for the next keyup to inherit. The extra wait the lead adds
    /// here is time the radio spends playing audio it already holds, so a keyup is no longer
    /// than it was.</remarks>
    public void Drain()
    {
        if (_accumulated > 0)
        {
            // Flush the last partial packet, zero-padded to a full packet.
            Array.Clear(_accumulator, _accumulated, _format.SamplesPerPacket - _accumulated);
            SendPacket(_accumulator);
            _accumulated = 0;
        }

        if (_paceRealTime && _clockRunning)
        {
            double remainMs = DueMs - ElapsedMs;
            if (remainMs > 0)
            {
                _sleep((int)Math.Ceiling(remainMs));
            }
        }

        // Reset the airtime clock for the next transmission; the packet counter rolls on.
        _clockRunning = false;
        _samplesSent = 0;
    }

    /// <summary>Reads <see cref="TransmitLeadVariable"/>'s value: unset, blank or not understood
    /// is the default; 0 or <c>off</c> is zero; otherwise whole milliseconds up to
    /// <see cref="MaxTransmitLeadMilliseconds"/>.</summary>
    internal static TimeSpan ParseTransmitLead(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultTransmitLead;
        }

        string text = value.Trim();
        if (string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.Zero;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int ms)
            && ms <= MaxTransmitLeadMilliseconds
            ? TimeSpan.FromMilliseconds(ms)
            : DefaultTransmitLead;
    }

    private static Action<byte[]> SendThrough(FlexClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return packet => client.SendVita(packet);
    }

    private static Func<uint> StreamOf(FlexSliceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return () => lease.Current.TxStreamId;
    }

    private static long SystemTicks() => Stopwatch.GetElapsedTime(0).Ticks;

    /// <summary>Audio sent so far this keyup, in milliseconds of playout.</summary>
    private double DueMs => 1000.0 * _samplesSent / _format.SampleRate;

    /// <summary>Playout time since the keyup's first packet, moved late by any stall.</summary>
    private double ElapsedMs => TimeSpan.FromTicks(_nowTicks() - _originTicks).TotalMilliseconds;

    private void SendPacket(ReadOnlySpan<float> packetSamples)
    {
        byte[] packet = _format.BuildPacket(_streamId(), _packetCount, packetSamples);
        _packetCount = (_packetCount + 1) & 0x0F;
        _send(packet);

        if (_paceRealTime && _clockRunning && _leadMs > 0)
        {
            // This packet reached the radio after everything sent before it had played: the
            // radio ran dry and plays from here late by the shortfall. Follow it, so the lead is
            // rebuilt from where the radio really is rather than grown by the stall, and the
            // drain waits for its real end. Measured after the send, so a hold inside the send
            // counts too; a hold after the packet left makes this one packet early at worst,
            // which the drain spends as a few milliseconds of keyed silence, never cut audio.
            double shortfallMs = ElapsedMs - DueMs;
            if (shortfallMs > 0)
            {
                _originTicks += TimeSpan.FromMilliseconds(shortfallMs).Ticks;
            }
        }

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

        if (!_clockRunning)
        {
            _originTicks = _nowTicks();
            _clockRunning = true;
        }

        // Ahead of the radio by more than the lead: wait off the excess. With no lead this is
        // the one-packet pacing of earlier versions, unchanged.
        double excessMs = DueMs - ElapsedMs - _leadMs;
        if (excessMs > 1.0)
        {
            _sleep((int)excessMs);
        }
    }
}

namespace M0LTE.Flex;

/// <summary>
/// Blocking, source-paced audio input — the capture side of a Flex DAX stream. A
/// <see cref="FlexAudioInput"/> presents the radio's receive audio through this seam so a
/// consumer can treat a FlexRadio, a sound card, or a file the same way. Samples are
/// normalised floats (−1..1) at <see cref="SampleRate"/>.
/// </summary>
public interface IAudioInput
{
    /// <summary>Source sample rate (Hz) of the samples returned by <see cref="Read"/>.</summary>
    int SampleRate { get; }

    /// <summary>Reads up to <paramref name="destination"/>.Length samples as normalised
    /// floats (−1..1). Blocks until at least one sample is available; returns the count
    /// written (0 only when the source is closing).</summary>
    int Read(Span<float> destination);
}

/// <summary>
/// Blocking, sink-paced audio output — the transmit side of a Flex DAX stream. A
/// <see cref="FlexAudioOutput"/> accepts modulated audio through this seam and packetises it
/// to the radio. Samples are normalised floats (−1..1) at <see cref="SampleRate"/>.
/// </summary>
public interface IAudioOutput
{
    /// <summary>Sink sample rate (Hz); the samples written must be at this rate.</summary>
    int SampleRate { get; }

    /// <summary>Writes samples; blocks while the sink consumes them (paced off the sample
    /// clock for a DAX stream, since there is no device to block on).</summary>
    void Write(ReadOnlySpan<float> samples);

    /// <summary>Blocks until everything written has actually left the sink — the
    /// sample-domain part of PTT release.</summary>
    void Drain();
}

/// <summary>Keys and unkeys the transmitter. For a FlexRadio slice this is an API command
/// rather than a hardware line — see <see cref="FlexPtt"/>.</summary>
public interface IPttControl
{
    /// <summary>Asserts PTT.</summary>
    void Key();

    /// <summary>Releases PTT.</summary>
    void Unkey();
}

/// <summary>No-op PTT for VOX-operated interfaces and tests.</summary>
public sealed class NullPtt : IPttControl
{
    /// <inheritdoc />
    public void Key()
    {
    }

    /// <inheritdoc />
    public void Unkey()
    {
    }
}

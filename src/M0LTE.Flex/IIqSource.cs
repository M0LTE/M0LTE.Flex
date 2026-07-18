namespace M0LTE.Flex;

/// <summary>
/// A source of wideband complex baseband (IQ) samples. Samples are interleaved single-precision
/// <c>I, Q, I, Q, …</c> at <see cref="SampleRate"/>, host-endian. <see cref="FlexDaxIqSource"/>
/// implements this over a live DAX-IQ receive stream; a synthetic/replay source implements it for
/// offline use. A consumer (e.g. a multi-channel receiver that digitally down-converts several
/// channels from one wideband slice) reads blocks from one of these.
/// </summary>
public interface IIqSource
{
    /// <summary>Complex sample rate in Hz (I/Q pairs per second).</summary>
    int SampleRate { get; }

    /// <summary>The RF centre frequency the IQ is tuned to, in Hz. Channel offsets are relative to
    /// this. Informational — the DSP works in offset-from-centre terms — and may be 0 when the
    /// absolute frequency is unknown or irrelevant (e.g. synthetic test signals).</summary>
    double CentreFrequencyHz { get; }

    /// <summary>Reads the next block of interleaved <c>I, Q</c> samples into
    /// <paramref name="interleaved"/>, filling as many complete pairs as fit. Returns the number of
    /// <b>float</b> elements written (always even), or 0 at end of stream.</summary>
    int Read(Span<float> interleaved);
}

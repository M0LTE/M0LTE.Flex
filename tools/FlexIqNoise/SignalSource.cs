namespace M0LTE.Flex.Tools.IqNoise;

/// <summary>A source of interleaved <c>I, Q</c> transmit samples, with the level statistics needed to
/// tell the rig's own artefacts from the radio's behaviour.</summary>
internal interface ISignalSource
{
    /// <summary>One line describing how the signal is built, for the report.</summary>
    string Description { get; }

    /// <summary>Complex samples generated so far.</summary>
    long SamplesGenerated { get; }

    /// <summary>Largest absolute component value seen, before clipping.</summary>
    double PeakSample { get; }

    /// <summary>Components clamped to ±1.0. Any clipping splatters outside the band.</summary>
    long ClippedSamples { get; }

    /// <summary>Measured per-component RMS of everything generated so far.</summary>
    double MeasuredRms { get; }

    /// <summary>Fills <paramref name="interleavedIq"/> with the next span of signal.</summary>
    void Fill(Span<float> interleavedIq);
}

/// <summary>Shared level accounting and full-scale clamping for the signal sources.</summary>
internal abstract class SignalSource : ISignalSource
{
    private const double FullScale = 1.0;

    private double _sumSquares;

    public abstract string Description { get; }

    public long SamplesGenerated { get; private set; }

    public double PeakSample { get; private set; }

    public long ClippedSamples { get; private set; }

    public double MeasuredRms => SamplesGenerated == 0 ? 0 : Math.Sqrt(_sumSquares / (2 * SamplesGenerated));

    public void Fill(Span<float> interleavedIq)
    {
        Generate(interleavedIq);
        SamplesGenerated += interleavedIq.Length / 2;
    }

    /// <summary>Produces the samples, calling <see cref="Emit"/> for each complex sample.</summary>
    protected abstract void Generate(Span<float> interleavedIq);

    /// <summary>Records one complex sample's statistics and writes it, clamped to full scale.</summary>
    protected void Emit(Span<float> destination, int n, double i, double q)
    {
        _sumSquares += (i * i) + (q * q);
        PeakSample = Math.Max(PeakSample, Math.Max(Math.Abs(i), Math.Abs(q)));

        destination[2 * n] = Clamp(i);
        destination[(2 * n) + 1] = Clamp(q);
    }

    private float Clamp(double value)
    {
        if (value > FullScale)
        {
            ClippedSamples++;
            return (float)FullScale;
        }

        if (value < -FullScale)
        {
            ClippedSamples++;
            return (float)-FullScale;
        }

        return (float)value;
    }
}

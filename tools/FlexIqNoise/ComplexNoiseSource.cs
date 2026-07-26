namespace M0LTE.Flex.Tools.IqNoise;

/// <summary>
/// Generates band-limited complex Gaussian white noise as interleaved <c>I, Q</c> floats, ready for
/// <see cref="FlexWaveformIqOutput.Write"/>.
/// </summary>
/// <remarks>
/// <para>Three stages, in order:</para>
/// <list type="number">
///   <item>Box–Muller gives two independent N(0,σ) samples per complex sample — I and Q. White
///   complex noise is flat across the whole ±12 kHz of the 24 kHz waveform rate.</item>
///   <item>A linear-phase windowed-sinc FIR (4-term Blackman–Harris, ~−92 dB stopband) low-passes
///   both components at <c>bandwidth/2</c>. Filtering a complex signal with a real, symmetric kernel
///   keeps the passband symmetric about DC, so the result occupies exactly ±bandwidth/2 about the
///   carrier. Tap count is chosen so the rig's own transition band is far sharper than anything the
///   radio will do — otherwise we would be measuring this filter, not the radio.</item>
///   <item>An optional NCO shifts the band away from the slice centre.</item>
/// </list>
/// <para>σ is pre-compensated for the filter's noise gain (√Σh²) so the transmitted RMS is the
/// requested one whatever the bandwidth — a narrow band and a wide one hit the PA equally hard.</para>
/// </remarks>
internal sealed class ComplexNoiseSource : SignalSource
{
    private readonly Random _random;
    private readonly float[]? _taps;
    private readonly float[] _historyI;
    private readonly float[] _historyQ;
    private readonly int _historyLead;      // taps-1 samples of carry-over ahead of each new block
    private readonly double _sigma;         // pre-filter per-component sigma
    private readonly double _phaseStep;     // NCO radians/sample; 0 when no offset
    private readonly int _blockPairs;
    private double _phase;

    /// <summary>Number of FIR taps, or 0 when the full band was requested and no filter applies.</summary>
    public int TapCount => _taps?.Length ?? 0;

    /// <summary>Width of the FIR's transition band in Hz — how sharply the rig's own edges fall.</summary>
    public double TransitionWidthHz { get; }

    public override string Description => _taps is null
        ? "none (full band)"
        : $"{TapCount}-tap Blackman–Harris FIR, {TransitionWidthHz:F0} Hz transition";

    public ComplexNoiseSource(Options options, int blockPairs)
    {
        _random = new Random(options.Seed);
        _blockPairs = blockPairs;

        if (options.IsFullBand)
        {
            // Nothing to filter: white noise already fills the whole complex band.
            _taps = null;
            _historyI = [];
            _historyQ = [];
            _historyLead = 0;
            _sigma = options.Rms;
            TransitionWidthHz = 0;
        }
        else
        {
            double cutoffHz = options.BandwidthHz / 2;
            int taps = ChooseTapCount(options.BandwidthHz, Options.SampleRate, out double transitionHz);
            TransitionWidthHz = transitionHz;
            _taps = DesignLowPass(cutoffHz, Options.SampleRate, taps);

            double noiseGain = 0;
            foreach (float tap in _taps)
            {
                noiseGain += (double)tap * tap;
            }

            // Filtering white noise of variance σ² yields variance σ²·Σh². Invert that so the
            // transmitted RMS is what was asked for regardless of how narrow the band is.
            _sigma = options.Rms / Math.Sqrt(noiseGain);

            _historyLead = taps - 1;
            _historyI = new float[_historyLead + blockPairs];
            _historyQ = new float[_historyLead + blockPairs];
        }

        _phaseStep = options.OffsetHz == 0 ? 0 : 2 * Math.PI * options.OffsetHz / Options.SampleRate;
    }

    /// <summary>Fills <paramref name="interleavedIq"/> (length = 2 × complex samples, at most the
    /// block size this source was built for) with the next span of noise.</summary>
    protected override void Generate(Span<float> interleavedIq)
    {
        int pairs = interleavedIq.Length / 2;
        if (pairs > _blockPairs)
        {
            throw new ArgumentException($"block of {pairs} pairs exceeds the configured {_blockPairs}");
        }

        if (_taps is null)
        {
            for (int n = 0; n < pairs; n++)
            {
                (double i, double q) = NextGaussianPair(_sigma);
                Shift(interleavedIq, n, i, q);
            }
        }
        else
        {
            // Lay the new white samples after the carried-over tail, convolve, then carry the tail on.
            for (int n = 0; n < pairs; n++)
            {
                (double i, double q) = NextGaussianPair(_sigma);
                _historyI[_historyLead + n] = (float)i;
                _historyQ[_historyLead + n] = (float)q;
            }

            float[] taps = _taps;
            for (int n = 0; n < pairs; n++)
            {
                double accI = 0;
                double accQ = 0;
                int newest = _historyLead + n;
                for (int k = 0; k < taps.Length; k++)
                {
                    float tap = taps[k];
                    accI += tap * _historyI[newest - k];
                    accQ += tap * _historyQ[newest - k];
                }

                Shift(interleavedIq, n, accI, accQ);
            }

            Array.Copy(_historyI, pairs, _historyI, 0, _historyLead);
            Array.Copy(_historyQ, pairs, _historyQ, 0, _historyLead);
        }
    }

    /// <summary>Shifts the band off centre, then hands the sample to the base for accounting.</summary>
    private void Shift(Span<float> destination, int n, double i, double q)
    {
        if (_phaseStep != 0)
        {
            (double sin, double cos) = Math.SinCos(_phase);
            (i, q) = ((i * cos) - (q * sin), (i * sin) + (q * cos));
            _phase += _phaseStep;
            if (_phase > Math.PI)
            {
                _phase -= 2 * Math.PI;
            }
            else if (_phase < -Math.PI)
            {
                _phase += 2 * Math.PI;
            }
        }

        Emit(destination, n, i, q);
    }

    /// <summary>Box–Muller: one uniform pair in, two independent N(0, sigma) samples out.</summary>
    private (double I, double Q) NextGaussianPair(double sigma)
    {
        double u1 = 1.0 - _random.NextDouble();     // in (0, 1]; Log(0) is not survivable
        double u2 = _random.NextDouble();
        double magnitude = sigma * Math.Sqrt(-2.0 * Math.Log(u1));
        (double sin, double cos) = Math.SinCos(2.0 * Math.PI * u2);
        return (magnitude * cos, magnitude * sin);
    }

    /// <summary>
    /// Picks a tap count giving a transition band comfortably narrower than the requested bandwidth,
    /// so the measured edges belong to the radio rather than to this filter. A 4-term
    /// Blackman–Harris window has a main lobe 8 bins wide, so transition ≈ 8·fs/taps.
    /// </summary>
    private static int ChooseTapCount(double bandwidthHz, int sampleRate, out double transitionHz)
    {
        // Aim for edges 2% of the bandwidth wide, floored at 25 Hz so narrow bands stay affordable.
        double target = Math.Max(bandwidthHz * 0.02, 25);
        int taps = (int)Math.Ceiling(8.0 * sampleRate / target);
        taps = Math.Clamp(taps, 127, 8191);
        if (taps % 2 == 0)
        {
            taps++;                                  // odd => exact linear phase, integer group delay
        }

        transitionHz = 8.0 * sampleRate / taps;
        return taps;
    }

    /// <summary>Windowed-sinc low-pass, normalised to unity gain at DC.</summary>
    private static float[] DesignLowPass(double cutoffHz, int sampleRate, int taps)
    {
        double fc = cutoffHz / sampleRate;           // normalised, 0..0.5
        var h = new double[taps];
        int last = taps - 1;
        double sum = 0;

        for (int i = 0; i < taps; i++)
        {
            double n = i - (last / 2.0);
            double sinc = n == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * n) / (Math.PI * n);
            double value = sinc * BlackmanHarris(i, last);
            h[i] = value;
            sum += value;
        }

        var result = new float[taps];
        for (int i = 0; i < taps; i++)
        {
            result[i] = (float)(h[i] / sum);
        }

        return result;
    }

    private static double BlackmanHarris(int i, int last)
    {
        double x = 2 * Math.PI * i / last;
        return 0.35875
            - (0.48829 * Math.Cos(x))
            + (0.14128 * Math.Cos(2 * x))
            - (0.01168 * Math.Cos(3 * x));
    }
}

namespace M0LTE.Flex.Tools.IqGen;

/// <summary>
/// The test signals. Every one is generated as complex baseband at
/// <see cref="SampleRate"/> and, by default, placed entirely <b>below DC</b> — because that is the
/// only half a FlexRadio waveform transmits (docs/flex-integration.md §9.5).
/// </summary>
internal static class Signals
{
    /// <summary>The FlexRadio waveform rate, and so the rate everything here is generated at.</summary>
    public const int SampleRate = 24000;

    /// <summary>A single complex tone. Asymmetric by construction, so where it lands identifies the
    /// radio's sideband and orientation outright — unlike a symmetric pair, which cannot.</summary>
    public static float[] Tone(double offsetHz, double seconds, double amplitude)
    {
        int count = (int)(seconds * SampleRate);
        var iq = new float[count * 2];
        double step = 2 * Math.PI * offsetHz / SampleRate;
        double phase = 0;
        for (int n = 0; n < count; n++)
        {
            (double sin, double cos) = Math.SinCos(phase);
            iq[2 * n] = (float)(amplitude * cos);
            iq[(2 * n) + 1] = (float)(amplitude * sin);
            phase += step;
            phase = Wrap(phase);
        }

        return iq;
    }

    /// <summary>Two tones at unequal offsets. Their spacing and order survive an upright path and
    /// reverse under a mirrored one, so this reads inversion without needing a demodulator.</summary>
    public static float[] TwoTone(double firstHz, double secondHz, double seconds, double amplitude)
    {
        float[] a = Tone(firstHz, seconds, amplitude / 2);
        float[] b = Tone(secondHz, seconds, amplitude / 2);
        for (int i = 0; i < a.Length; i++)
        {
            a[i] += b[i];
        }

        return a;
    }

    /// <summary>A linear frequency sweep. On a waterfall the sweep line simply stops where the
    /// radio's passband ends, which maps an edge in one transmission.</summary>
    public static float[] Chirp(double startHz, double endHz, double seconds, double amplitude)
    {
        int count = (int)(seconds * SampleRate);
        var iq = new float[count * 2];
        double phase = 0;
        for (int n = 0; n < count; n++)
        {
            double t = n / (double)count;
            double hz = startHz + ((endHz - startHz) * t);
            (double sin, double cos) = Math.SinCos(phase);
            iq[2 * n] = (float)(amplitude * cos);
            iq[(2 * n) + 1] = (float)(amplitude * sin);
            phase = Wrap(phase + (2 * Math.PI * hz / SampleRate));
        }

        return iq;
    }

    /// <summary>
    /// Noise shaped by an arbitrary per-frequency gain, synthesised in the frequency domain: fill the
    /// wanted bins with a random phase and the wanted magnitude, then inverse-transform. Band edges
    /// are exact and there is no filter transition to mistake for the radio's.
    /// </summary>
    /// <param name="gainAt">Linear amplitude for a given baseband frequency in Hz; return 0 to exclude.</param>
    public static float[] ShapedNoise(Func<double, double> gainAt, double seconds, double amplitude, int seed)
    {
        int count = (int)(seconds * SampleRate);
        int size = 1;
        while (size < count)
        {
            size <<= 1;
        }

        var random = new Random(seed);
        var re = new double[size];
        var im = new double[size];

        for (int k = 0; k < size; k++)
        {
            // Bins above size/2 are the negative frequencies.
            double hz = (k <= size / 2 ? k : k - size) * (double)SampleRate / size;
            double gain = gainAt(hz);
            if (gain <= 0)
            {
                continue;
            }

            double phase = 2 * Math.PI * random.NextDouble();
            (double sin, double cos) = Math.SinCos(phase);
            re[k] = gain * cos;
            im[k] = gain * sin;
        }

        Fft.Transform(re, im, inverse: true);

        // Normalise to the requested per-component RMS.
        double sum = 0;
        for (int n = 0; n < count; n++)
        {
            sum += (re[n] * re[n]) + (im[n] * im[n]);
        }

        double rms = Math.Sqrt(sum / (2 * count));
        double scale = rms > 0 ? amplitude / rms : 0;

        var iq = new float[count * 2];
        for (int n = 0; n < count; n++)
        {
            iq[2 * n] = (float)(re[n] * scale);
            iq[(2 * n) + 1] = (float)(im[n] * scale);
        }

        return iq;
    }

    /// <summary>Flat band-limited noise between two baseband frequencies.</summary>
    public static float[] Noise(double lowHz, double highHz, double seconds, double amplitude, int seed) =>
        ShapedNoise(hz => hz >= lowHz && hz <= highHz ? 1 : 0, seconds, amplitude, seed);

    /// <summary>
    /// Noise in stepped levels across the band — the orientation check. The steps run one way up the
    /// spectrum, so a mirrored path shows them running the other way. A flat noise band or a single
    /// tone cannot reveal that; this can, at a glance, with no receiver-side processing.
    /// </summary>
    public static float[] Staircase(double lowHz, double highHz, int steps, double stepDb, double seconds, double amplitude, int seed)
    {
        double width = (highHz - lowHz) / steps;
        return ShapedNoise(
            hz =>
            {
                if (hz < lowHz || hz > highHz)
                {
                    return 0;
                }

                int step = Math.Min(steps - 1, (int)((hz - lowHz) / width));
                return Math.Pow(10, -stepDb * step / 20);
            },
            seconds,
            amplitude,
            seed);
    }

    /// <summary>
    /// Root-raised-cosine filtered QPSK — a real modulated signal, so a path that mirrors or
    /// companders it produces something that still looks plausible on a spectrum display but will not
    /// demodulate. The one corpus entry whose failure mode is invisible to a waterfall.
    /// </summary>
    public static float[] Qpsk(double symbolRate, double beta, double centreHz, double seconds, double amplitude, int seed)
    {
        int count = (int)(seconds * SampleRate);
        int sps = (int)Math.Round(SampleRate / symbolRate);
        int symbols = (count / sps) + 1;
        var random = new Random(seed);

        // Random QPSK symbols on the diagonals.
        var symI = new double[symbols];
        var symQ = new double[symbols];
        for (int s = 0; s < symbols; s++)
        {
            symI[s] = (random.Next(2) * 2) - 1;
            symQ[s] = (random.Next(2) * 2) - 1;
        }

        // Root-raised-cosine pulse, span 8 symbols.
        int span = 8;
        int taps = (span * sps) + 1;
        var h = new double[taps];
        for (int i = 0; i < taps; i++)
        {
            double t = (i - ((taps - 1) / 2.0)) / sps;
            h[i] = RootRaisedCosine(t, beta);
        }

        double energy = 0;
        foreach (double tap in h)
        {
            energy += tap * tap;
        }

        double norm = 1 / Math.Sqrt(energy);

        // Built unscaled first, then normalised to the requested RMS below: the RRC's tap energy
        // and the symbol constellation together set the level, so a nominal amplitude applied up
        // front lands wherever it happens to. Every corpus entry is meant to transmit at the same
        // level, and this one was ~10 dB light until it was measured.
        var raw = new double[count * 2];
        double phase = 0;
        double step = 2 * Math.PI * centreHz / SampleRate;
        for (int n = 0; n < count; n++)
        {
            double accI = 0;
            double accQ = 0;
            for (int i = 0; i < taps; i++)
            {
                int sampleIndex = n - i + ((taps - 1) / 2);
                if (sampleIndex < 0 || sampleIndex % sps != 0)
                {
                    continue;
                }

                int s = sampleIndex / sps;
                if (s >= 0 && s < symbols)
                {
                    accI += symI[s] * h[i];
                    accQ += symQ[s] * h[i];
                }
            }

            accI *= norm;
            accQ *= norm;

            // Shift the modulated signal to its centre frequency.
            (double sin, double cos) = Math.SinCos(phase);
            raw[2 * n] = (accI * cos) - (accQ * sin);
            raw[(2 * n) + 1] = (accI * sin) + (accQ * cos);
            phase = Wrap(phase + step);
        }

        return Normalise(raw, amplitude);
    }

    /// <summary>Scales a signal so its per-component RMS is <paramref name="amplitude"/>.</summary>
    private static float[] Normalise(double[] raw, double amplitude)
    {
        double sum = 0;
        foreach (double value in raw)
        {
            sum += value * value;
        }

        double rms = Math.Sqrt(sum / raw.Length);
        double scale = rms > 0 ? amplitude / rms : 0;

        var iq = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            iq[i] = (float)(raw[i] * scale);
        }

        return iq;
    }

    private static double RootRaisedCosine(double t, double beta)
    {
        const double Epsilon = 1e-8;
        if (Math.Abs(t) < Epsilon)
        {
            return 1 - beta + (4 * beta / Math.PI);
        }

        if (beta > 0 && Math.Abs(Math.Abs(t) - (1 / (4 * beta))) < Epsilon)
        {
            double inner = ((1 + (2 / Math.PI)) * Math.Sin(Math.PI / (4 * beta)))
                + ((1 - (2 / Math.PI)) * Math.Cos(Math.PI / (4 * beta)));
            return beta / Math.Sqrt(2) * inner;
        }

        double numerator = Math.Sin(Math.PI * t * (1 - beta))
            + (4 * beta * t * Math.Cos(Math.PI * t * (1 + beta)));
        double denominator = Math.PI * t * (1 - ((4 * beta * t) * (4 * beta * t)));
        return numerator / denominator;
    }

    private static double Wrap(double phase)
    {
        if (phase > Math.PI)
        {
            return phase - (2 * Math.PI);
        }

        return phase < -Math.PI ? phase + (2 * Math.PI) : phase;
    }
}

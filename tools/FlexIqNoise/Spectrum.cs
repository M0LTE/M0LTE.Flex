namespace M0LTE.Flex.Tools.IqNoise;

/// <summary>
/// A Welch power-spectrum estimate over complex baseband, used to audit the rig's own output before
/// any conclusion is drawn about the radio. If the noise is not flat and sharply bounded here, no
/// bandwidth measured on the air means anything.
/// </summary>
internal static class Spectrum
{
    /// <summary>
    /// Averaged periodogram of interleaved <c>I, Q</c> samples: Hann-windowed, 50 % overlap.
    /// Returns power per bin in dB, frequency-shifted so index 0 is −fs/2 and the centre bin is DC.
    /// </summary>
    public static double[] Estimate(ReadOnlySpan<float> interleavedIq, int fftSize)
    {
        int pairs = interleavedIq.Length / 2;
        if (pairs < fftSize)
        {
            throw new ArgumentException($"need at least {fftSize} complex samples to analyse");
        }

        var window = new double[fftSize];
        double windowPower = 0;
        for (int n = 0; n < fftSize; n++)
        {
            window[n] = 0.5 * (1 - Math.Cos(2 * Math.PI * n / (fftSize - 1)));
            windowPower += window[n] * window[n];
        }

        var accumulator = new double[fftSize];
        var re = new double[fftSize];
        var im = new double[fftSize];
        int hop = fftSize / 2;
        int segments = 0;

        for (int start = 0; start + fftSize <= pairs; start += hop)
        {
            for (int n = 0; n < fftSize; n++)
            {
                re[n] = interleavedIq[2 * (start + n)] * window[n];
                im[n] = interleavedIq[(2 * (start + n)) + 1] * window[n];
            }

            Fft(re, im);

            for (int k = 0; k < fftSize; k++)
            {
                accumulator[k] += (re[k] * re[k]) + (im[k] * im[k]);
            }

            segments++;
        }

        // Normalise out the segment count and window energy, then shift DC to the middle.
        var result = new double[fftSize];
        double scale = 1.0 / (segments * windowPower);
        int half = fftSize / 2;
        for (int k = 0; k < fftSize; k++)
        {
            int shifted = (k + half) % fftSize;
            double power = accumulator[shifted] * scale;
            result[k] = 10 * Math.Log10(Math.Max(power, 1e-30));
        }

        return result;
    }

    /// <summary>Frequency in Hz of bin <paramref name="index"/> of an <see cref="Estimate"/> result.</summary>
    public static double BinFrequencyHz(int index, int fftSize, int sampleRate) =>
        (index - (fftSize / 2.0)) * sampleRate / fftSize;

    /// <summary>Mean dB over the bins whose frequency falls in [<paramref name="lowHz"/>,
    /// <paramref name="highHz"/>], or null when the range covers no bins.</summary>
    public static double? MeanDbOver(double[] spectrumDb, int sampleRate, double lowHz, double highHz)
    {
        double sum = 0;
        int count = 0;
        for (int k = 0; k < spectrumDb.Length; k++)
        {
            double f = BinFrequencyHz(k, spectrumDb.Length, sampleRate);
            if (f >= lowHz && f <= highHz)
            {
                sum += spectrumDb[k];
                count++;
            }
        }

        return count == 0 ? null : sum / count;
    }

    /// <summary>Standard deviation of dB over a frequency range — for flat noise this reflects the
    /// estimator's own variance, not real ripple.</summary>
    public static double? StdDevDbOver(double[] spectrumDb, int sampleRate, double lowHz, double highHz)
    {
        double? mean = MeanDbOver(spectrumDb, sampleRate, lowHz, highHz);
        if (mean is not double mu)
        {
            return null;
        }

        double sum = 0;
        int count = 0;
        for (int k = 0; k < spectrumDb.Length; k++)
        {
            double f = BinFrequencyHz(k, spectrumDb.Length, sampleRate);
            if (f >= lowHz && f <= highHz)
            {
                sum += (spectrumDb[k] - mu) * (spectrumDb[k] - mu);
                count++;
            }
        }

        return Math.Sqrt(sum / count);
    }

    /// <summary>
    /// Walks outward from <paramref name="centreHz"/> to the outermost frequencies still within
    /// <paramref name="dropDb"/> of <paramref name="referenceDb"/> — the −3 dB / −60 dB style width.
    /// A crossing must persist for three bins so the estimator's own variance does not call an edge.
    /// </summary>
    public static (double LowHz, double HighHz) WidthAtDb(
        double[] spectrumDb, int sampleRate, double centreHz, double referenceDb, double dropDb)
    {
        const int Persistence = 3;
        int size = spectrumDb.Length;
        double threshold = referenceDb - dropDb;
        int centre = Math.Clamp(
            (int)Math.Round((centreHz * size / sampleRate) + (size / 2.0)), 0, size - 1);

        int highest = centre;
        int below = 0;
        for (int k = centre; k < size; k++)
        {
            if (spectrumDb[k] >= threshold)
            {
                highest = k;
                below = 0;
            }
            else if (++below >= Persistence)
            {
                break;
            }
        }

        int lowest = centre;
        below = 0;
        for (int k = centre; k >= 0; k--)
        {
            if (spectrumDb[k] >= threshold)
            {
                lowest = k;
                below = 0;
            }
            else if (++below >= Persistence)
            {
                break;
            }
        }

        return (BinFrequencyHz(lowest, size, sampleRate), BinFrequencyHz(highest, size, sampleRate));
    }

    /// <summary>
    /// The classic occupied bandwidth: the band containing <paramref name="fraction"/> of the total
    /// power, with the remainder split equally between the two tails.
    /// </summary>
    public static (double LowHz, double HighHz) OccupiedBandwidth(
        double[] spectrumDb, int sampleRate, double fraction)
    {
        int size = spectrumDb.Length;
        var power = new double[size];
        double total = 0;
        for (int k = 0; k < size; k++)
        {
            power[k] = Math.Pow(10, spectrumDb[k] / 10);
            total += power[k];
        }

        double tail = total * (1 - fraction) / 2;
        double cumulative = 0;
        int low = 0;
        for (int k = 0; k < size; k++)
        {
            cumulative += power[k];
            if (cumulative >= tail)
            {
                low = k;
                break;
            }
        }

        cumulative = 0;
        int high = size - 1;
        for (int k = size - 1; k >= 0; k--)
        {
            cumulative += power[k];
            if (cumulative >= tail)
            {
                high = k;
                break;
            }
        }

        return (BinFrequencyHz(low, size, sampleRate), BinFrequencyHz(high, size, sampleRate));
    }

    /// <summary>In-place iterative radix-2 Cooley–Tukey FFT. Length must be a power of two.</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        if ((n & (n - 1)) != 0)
        {
            throw new ArgumentException("FFT length must be a power of two");
        }

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            (double wSin, double wCos) = Math.SinCos(angle);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1;
                double curIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k;
                    int b = a + (len / 2);
                    double tRe = (re[b] * curRe) - (im[b] * curIm);
                    double tIm = (re[b] * curIm) + (im[b] * curRe);
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;

                    (curRe, curIm) = ((curRe * wCos) - (curIm * wSin), (curRe * wSin) + (curIm * wCos));
                }
            }
        }
    }
}

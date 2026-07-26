namespace M0LTE.Flex.Tools.IqPaint;

/// <summary>
/// Turns a brightness grid into a signal that draws it on a waterfall: image columns become
/// frequencies, image rows become moments in time.
/// </summary>
/// <remarks>
/// <para>One oscillator per frequency bin runs <b>continuously</b> for the whole transmission,
/// amplitude-modulated by the pixels above it. Restarting a tone each row would be a phase
/// discontinuity — a click — and a click is broadband, so it draws a bright line straight across the
/// picture it is meant to be drawing.</para>
/// <para>Output is ordinary complex baseband at the frequencies asked for — <b>not</b> pre-placed
/// below DC. Which half of the spectrum a Flex waveform actually transmits is the library's problem,
/// not a picture's: <c>FlexWaveformOptions.OccupiedBandwidthHz</c> derives the slice and shifts the
/// samples. Baking the radio's sideband quirk into the file would leak it back to every consumer and
/// tie the file to one transmit path.</para>
/// </remarks>
internal static class Painter
{
    /// <summary>Renders <paramref name="image"/> (row 0 = top) into samples.</summary>
    /// <returns>Mono real samples, or interleaved I/Q when <paramref name="complex"/>.</returns>
    public static float[] Render(double[,] image, PaintOptions options, bool complex)
    {
        int bins = image.GetLength(1);
        int lines = image.GetLength(0);
        int perLine = (int)Math.Round(options.RateHz * options.LineMs / 1000.0);
        int total = perLine * lines;

        var frequencies = new double[bins];
        for (int k = 0; k < bins; k++)
        {
            frequencies[k] = bins == 1
                ? options.LowHz
                : options.LowHz + ((options.HighHz - options.LowHz) * k / (bins - 1.0));
        }

        // Random start phases. With every tone starting at zero their peaks coincide and the crest
        // factor is the bin count, which clips instead of transmitting.
        var random = new Random(options.Seed);
        var phase = new double[bins];
        for (int k = 0; k < bins; k++)
        {
            phase[k] = random.NextDouble() * 2 * Math.PI;
        }

        // Amplitude envelopes per bin, ramped between rows rather than stepped — a step is a click.
        int ramp = Math.Min(perLine / 2, (int)(0.008 * options.RateHz));
        var real = new double[total];
        var imaginary = complex ? new double[total] : [];

        for (int k = 0; k < bins; k++)
        {
            double step = 2 * Math.PI * frequencies[k] / options.RateHz;
            double running = phase[k];
            double level = Level(image, 0, k, options);

            for (int n = 0; n < total; n++)
            {
                int line = Math.Min(n / perLine, lines - 1);
                int into = n % perLine;

                double target = Level(image, line, k, options);
                if (into < ramp && line > 0)
                {
                    double previous = Level(image, line - 1, k, options);
                    double t = (1 - Math.Cos(Math.PI * into / ramp)) / 2;
                    level = previous + ((target - previous) * t);
                }
                else
                {
                    level = target;
                }

                (double sin, double cos) = Math.SinCos(running);
                real[n] += level * cos;
                if (complex)
                {
                    imaginary[n] += level * sin;
                }

                running += step;
                if (running > Math.PI)
                {
                    running -= 2 * Math.PI;
                }
                else if (running < -Math.PI)
                {
                    running += 2 * Math.PI;
                }
            }
        }

        return Normalise(real, imaginary, complex, options.Peak);
    }

    /// <summary>Pixel brightness as a tone amplitude, honouring the ink and time-order conventions.</summary>
    private static double Level(double[,] image, int line, int bin, PaintOptions options)
    {
        int lines = image.GetLength(0);

        // A waterfall that scrolls downward shows the newest line at the top, so the image has to be
        // transmitted bottom-first to come out the right way up.
        int row = options.NewestAtTop ? lines - 1 - line : line;

        double value = image[row, bin];
        if (options.Invert)
        {
            value = 1 - value;                              // dark ink becomes strong tones
        }

        // A waterfall is a log display; squaring keeps mid-greys from washing into the background.
        return Math.Pow(Math.Clamp(value, 0, 1), 1.5);
    }

    /// <summary>
    /// Scales to a peak, not an RMS.
    /// </summary>
    /// <remarks>
    /// Hundreds of tones summing at random phase have a crest factor near 20 dB, so normalising the
    /// RMS to a sensible level puts the peaks well past full scale. One clipped sample splatters
    /// across every bin at once — a bright horizontal streak through the picture. For I/Q the limit
    /// is per component, since that is what the wire format clips.
    /// </remarks>
    private static float[] Normalise(double[] real, double[] imaginary, bool complex, double peak)
    {
        double worst = 0;
        for (int n = 0; n < real.Length; n++)
        {
            worst = Math.Max(worst, Math.Abs(real[n]));
            if (complex)
            {
                worst = Math.Max(worst, Math.Abs(imaginary[n]));
            }
        }

        double scale = peak / Math.Max(worst, 1e-12);

        if (!complex)
        {
            var mono = new float[real.Length];
            for (int n = 0; n < real.Length; n++)
            {
                mono[n] = (float)(real[n] * scale);
            }

            return mono;
        }

        var iq = new float[real.Length * 2];
        for (int n = 0; n < real.Length; n++)
        {
            iq[2 * n] = (float)(real[n] * scale);
            iq[(2 * n) + 1] = (float)(imaginary[n] * scale);
        }

        return iq;
    }
}

/// <summary>How a picture is turned into a signal.</summary>
internal sealed record PaintOptions
{
    public double LowHz { get; init; } = 300;

    public double HighHz { get; init; } = 2700;

    public int RateHz { get; init; } = 48000;

    public double LineMs { get; init; } = 80;

    public double Peak { get; init; } = 0.85;

    public int Seed { get; init; } = 1;

    /// <summary>Dark pixels become strong tones — right for a logo on white.</summary>
    public bool Invert { get; init; } = true;

    /// <summary>Transmit the bottom row first, for a waterfall that scrolls downward.</summary>
    public bool NewestAtTop { get; init; } = true;
}

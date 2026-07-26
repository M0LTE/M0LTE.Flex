using System.Globalization;

namespace M0LTE.Flex.Tools.IqNoise;

/// <summary>
/// A sum of pure complex exponentials — the diagnostic counterpart to the noise source.
/// </summary>
/// <remarks>
/// <para>Gaussian noise centred on the carrier is <b>symmetric</b>, so it cannot distinguish a path
/// that dropped one sideband from one that mirrored the spectrum, nor a complex path from a real one.
/// A single tone at a known <i>positive</i> offset settles all three at once:</para>
/// <list type="bullet">
///   <item>appears only above the carrier → complex IQ, correct orientation;</item>
///   <item>appears only below → the spectrum is inverted (I/Q swapped or conjugated);</item>
///   <item>appears equally on both sides → the path is real-only, ignoring Q;</item>
///   <item>appears on neither → the transmit filter is cutting it.</item>
/// </list>
/// <para>Each tone carries an equal share of the power budget, so the requested RMS holds however
/// many are asked for.</para>
/// </remarks>
internal sealed class ComplexToneSource : SignalSource
{
    private readonly double[] _offsetsHz;
    private readonly double[] _phases;
    private readonly double[] _phaseSteps;
    private readonly double _amplitude;

    public ComplexToneSource(Options options)
    {
        _offsetsHz = options.ToneOffsetsHz;
        _phases = new double[_offsetsHz.Length];
        _phaseSteps = new double[_offsetsHz.Length];
        for (int t = 0; t < _offsetsHz.Length; t++)
        {
            _phaseSteps[t] = 2 * Math.PI * _offsetsHz[t] / Options.SampleRate;

            // Stagger the starting phases so the tones do not all peak together on sample zero.
            _phases[t] = t * Math.PI / Math.Max(_offsetsHz.Length, 1);
        }

        // N tones of amplitude A give a per-component variance of N·A²/2; solve for the requested RMS.
        _amplitude = options.Rms * Math.Sqrt(2.0 / Math.Max(_offsetsHz.Length, 1));
    }

    public override string Description =>
        $"{_offsetsHz.Length} complex tone(s) at "
        + string.Join(", ", Array.ConvertAll(_offsetsHz, f => f.ToString("+#;-#;0", CultureInfo.InvariantCulture) + " Hz"));

    protected override void Generate(Span<float> interleavedIq)
    {
        int pairs = interleavedIq.Length / 2;
        for (int n = 0; n < pairs; n++)
        {
            double i = 0;
            double q = 0;
            for (int t = 0; t < _offsetsHz.Length; t++)
            {
                (double sin, double cos) = Math.SinCos(_phases[t]);
                i += _amplitude * cos;
                q += _amplitude * sin;

                _phases[t] += _phaseSteps[t];
                if (_phases[t] > Math.PI)
                {
                    _phases[t] -= 2 * Math.PI;
                }
                else if (_phases[t] < -Math.PI)
                {
                    _phases[t] += 2 * Math.PI;
                }
            }

            Emit(interleavedIq, n, i, q);
        }
    }
}

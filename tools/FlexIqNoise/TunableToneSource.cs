namespace M0LTE.Flex.Tools.IqNoise;

/// <summary>
/// A single complex tone whose baseband offset can be retuned <b>while transmitting</b> — the source
/// behind explore mode.
/// </summary>
/// <remarks>
/// <para>The tone is moved by changing the NCO's phase increment, never the radio's dial. The slice
/// stays put and the signal moves within the transmitted band, which is the only way to probe where
/// the radio's own passband starts and stops: retuning the dial would move the passband along with
/// the tone and measure nothing.</para>
/// <para><b>Phase is carried across a frequency change</b> rather than reset. A discontinuity at the
/// moment of retune would splatter a click across the whole band — precisely the thing being
/// measured — so only the increment changes and the accumulator runs on.</para>
/// </remarks>
internal sealed class TunableToneSource : SignalSource
{
    private readonly double _amplitude;
    private volatile int _offsetHz;
    private double _phase;

    /// <summary>Creates a tone at <paramref name="startHz"/> with per-component RMS
    /// <paramref name="rms"/>.</summary>
    public TunableToneSource(double rms, int startHz)
    {
        // One tone of amplitude A has per-component variance A²/2.
        _amplitude = rms * Math.Sqrt(2);
        _offsetHz = startHz;
    }

    /// <summary>The tone's current offset from the carrier, in Hz. Safe to set from another thread;
    /// it takes effect at the next generated block.</summary>
    public int OffsetHz
    {
        get => _offsetHz;
        set => _offsetHz = value;
    }

    public override string Description => $"tunable complex tone, {_offsetHz:+#;-#;0} Hz from centre";

    protected override void Generate(Span<float> interleavedIq)
    {
        int pairs = interleavedIq.Length / 2;

        // Sampled once per block, so a retune lands on a block boundary — blocks are short enough
        // (10 ms) that this is imperceptible, and it keeps the inner loop free of a volatile read.
        double step = 2 * Math.PI * _offsetHz / Options.SampleRate;

        for (int n = 0; n < pairs; n++)
        {
            (double sin, double cos) = Math.SinCos(_phase);
            Emit(interleavedIq, n, _amplitude * cos, _amplitude * sin);

            _phase += step;
            if (_phase > Math.PI)
            {
                _phase -= 2 * Math.PI;
            }
            else if (_phase < -Math.PI)
            {
                _phase += 2 * Math.PI;
            }
        }
    }
}

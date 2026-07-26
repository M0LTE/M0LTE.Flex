namespace M0LTE.Flex.Tools.IqNoise;

/// <summary>
/// Wraps the noise source with the two things that make the rig auditable: an optional WAV of exactly
/// what was handed to the radio, and a retained head of the burst for the spectrum self-check.
/// </summary>
internal sealed class BurstGenerator
{
    private const int MaxAnalysisSeconds = 8;

    private readonly IqWavWriter? _wav;
    private readonly float[]? _analysis;
    private int _analysisFloats;

    public BurstGenerator(Options options, int blockPairs)
    {
        Options = options;
        Source = options.IsTone ? new ComplexToneSource(options) : new ComplexNoiseSource(options, blockPairs);

        if (options.WavPath is string path)
        {
            _wav = new IqWavWriter(path, Options.SampleRate);
            WavPath = path;
        }

        int analysisPairs = (int)Math.Min(
            Math.Round(options.Seconds * Options.SampleRate), MaxAnalysisSeconds * Options.SampleRate);
        _analysis = analysisPairs > 0 ? new float[analysisPairs * 2] : null;
    }

    public Options Options { get; }

    public ISignalSource Source { get; }

    /// <summary>Path of the IQ WAV being written, or null.</summary>
    public string? WavPath { get; }

    /// <summary>The retained head of the burst, for the spectrum self-check.</summary>
    public float[]? AnalysisSamples =>
        _analysis is null || _analysisFloats == 0 ? null : _analysis[.._analysisFloats];

    /// <summary>Generates the next span of noise, mirroring it to the WAV and the analysis buffer.
    /// Not thread-safe: one producer at a time.</summary>
    public void Fill(Span<float> interleavedIq)
    {
        Source.Fill(interleavedIq);

        _wav?.Write(interleavedIq);

        if (_analysis is not null && _analysisFloats < _analysis.Length)
        {
            int take = Math.Min(interleavedIq.Length, _analysis.Length - _analysisFloats);
            interleavedIq[..take].CopyTo(_analysis.AsSpan(_analysisFloats, take));
            _analysisFloats += take;
        }
    }

    /// <summary>Closes the WAV, patching its header.</summary>
    public void Finish() => _wav?.Dispose();
}

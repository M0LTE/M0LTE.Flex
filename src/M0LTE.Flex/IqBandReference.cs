namespace M0LTE.Flex;

/// <summary>
/// Where the caller's DC sample sits inside the band it asks <see cref="FlexWaveform"/> to occupy —
/// and therefore what <see cref="FlexWaveformOptions.Frequency"/> refers to.
/// </summary>
/// <remarks>
/// Only meaningful when <see cref="FlexWaveformOptions.OccupiedBandwidthHz"/> is set. Either way the
/// library places the slice, picks the sideband and frequency-shifts the samples so the signal lands
/// exactly on the requested span with its spectrum <b>upright</b> — never mirrored.
/// </remarks>
public enum IqBandReference
{
    /// <summary>
    /// The caller writes DC-centred complex baseband — samples spanning
    /// <c>−bandwidth/2 … +bandwidth/2</c> — and <see cref="FlexWaveformOptions.Frequency"/> names the
    /// <b>centre</b> of the occupied band. This is the usual convention for complex baseband
    /// (GNU Radio, SoapySDR, UHD, SigMF), and what a modulator naturally produces. The default.
    /// </summary>
    Centre,

    /// <summary>
    /// The caller writes a one-sided (analytic) baseband — samples spanning <c>0 … +bandwidth</c> —
    /// and <see cref="FlexWaveformOptions.Frequency"/> names the <b>lower edge</b> of the occupied
    /// band. Natural when reading "a channel starting here, this wide", and what a real audio-band
    /// signal becomes after a Hilbert transform.
    /// </summary>
    LowerEdge,
}

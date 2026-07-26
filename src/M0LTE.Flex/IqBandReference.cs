namespace M0LTE.Flex;

/// <summary>
/// Where the caller's DC sample sits inside the band it asks <see cref="FlexWaveform"/> to occupy —
/// and therefore what <see cref="FlexWaveformOptions.SliceFrequencyMhz"/> refers to.
/// </summary>
/// <remarks>
/// Only meaningful when <see cref="FlexWaveformOptions.Band"/> is set. Either way the
/// library places the slice, picks the sideband and frequency-shifts the samples so the signal lands
/// exactly on the requested span with its spectrum <b>upright</b> — never mirrored.
/// </remarks>
public enum IqBandReference
{
    /// <summary>
    /// The caller writes DC-centred complex baseband — samples spanning
    /// <c>−bandwidth/2 … +bandwidth/2</c> — and <see cref="FlexWaveformOptions.SliceFrequencyMhz"/> names the
    /// <b>centre</b> of the occupied band. This is the usual convention for complex baseband
    /// (GNU Radio, SoapySDR, UHD, SigMF), and what a modulator naturally produces. The default.
    /// </summary>
    Centre,

    /// <summary>
    /// The caller writes a one-sided (analytic) baseband — samples spanning <c>0 … +bandwidth</c> —
    /// and <see cref="FlexWaveformOptions.SliceFrequencyMhz"/> names the <b>lower edge</b> of the occupied
    /// band. Natural when reading "a channel starting here, this wide", and what a real audio-band
    /// signal becomes after a Hilbert transform.
    /// </summary>
    LowerEdge,
}

/// <summary>
/// A band to place a signal in: where it goes, how wide it is, and which convention the caller's
/// samples are written in.
/// </summary>
/// <param name="FrequencyMhz">The signal's centre or lower edge, per <paramref name="Reference"/>.</param>
/// <param name="BandwidthHz">How wide the caller's signal is. Also sets the radio's transmit filter.</param>
/// <param name="Reference">What <paramref name="FrequencyMhz"/> names, and where the caller's DC sits.</param>
/// <remarks>
/// Setting this on <see cref="FlexWaveformOptions"/> selects band placement: the library derives the
/// slice frequency, shifts the samples into the half the radio transmits, opens the transmit filter,
/// and fails setup rather than transmitting a truncated signal. The alternative is
/// <see cref="FlexWaveformOptions.SliceFrequencyMhz"/>, which tunes the slice and sends the samples
/// untouched — the two are mutually exclusive, so which one is in use is visible at the call site
/// rather than implied by whether some other property happens to be set.
/// </remarks>
public sealed record IqBand(
    double FrequencyMhz,
    int BandwidthHz,
    IqBandReference Reference = IqBandReference.Centre);

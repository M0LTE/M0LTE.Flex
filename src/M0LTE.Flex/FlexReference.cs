namespace M0LTE.Flex;

/// <summary>A frequency-reference source the radio can be set to, or be running on.</summary>
public enum FlexOscillatorSource
{
    /// <summary>The radio has not said, or said something this library does not know.</summary>
    Unknown,

    /// <summary>The internal temperature-compensated oscillator.</summary>
    Tcxo,

    /// <summary>An internal oven-controlled oscillator.</summary>
    Ocxo,

    /// <summary>An external 10 MHz reference applied to the rear panel.</summary>
    External,

    /// <summary>A GPS-disciplined oscillator.</summary>
    Gpsdo,
}

/// <summary>
/// The radio's frequency-reference state: which source is configured, which is actually in use,
/// whether the reference PLL is locked, and the frequency-error correction in parts per billion.
/// </summary>
/// <remarks>
/// <para>Reading all four is what distinguishes the cases that matter. A radio configured for an
/// external reference but not running on one is not an internal detail — on the 6000-series,
/// external-reference detection happens <b>only at startup</b>, so selecting the source while the
/// radio is running leaves the master PLL where it was until the radio is rebooted with the
/// reference present. That shows up here as <see cref="Setting"/> = <c>External</c> with
/// <see cref="State"/> something else, or <see cref="Locked"/> false, and a consumer should say
/// so rather than reporting the configured source as though it were in use.</para>
/// <para><see cref="FreqErrorPpb"/> is the calibration correction, not an error measurement:
/// around zero when the radio is disciplined by an accurate external reference, and non-zero
/// when an internal oscillator has been calibrated against a known signal.</para>
/// </remarks>
/// <param name="Setting">The configured source.</param>
/// <param name="State">The source actually in use.</param>
/// <param name="Locked">Whether the reference PLL reports lock.</param>
/// <param name="FreqErrorPpb">Calibration correction in ppb; null when the radio has not said.</param>
/// <param name="SettingRaw">The radio's own word for <see cref="Setting"/>, unmapped.</param>
/// <param name="StateRaw">The radio's own word for <see cref="State"/>, unmapped.</param>
public sealed record FlexReferenceStatus(
    FlexOscillatorSource Setting,
    FlexOscillatorSource State,
    bool Locked,
    int? FreqErrorPpb,
    string SettingRaw = "",
    string StateRaw = "")
{
    /// <summary>Nothing has been heard from the radio about its reference yet.</summary>
    public static FlexReferenceStatus Unknown { get; } =
        new(FlexOscillatorSource.Unknown, FlexOscillatorSource.Unknown, false, null);

    /// <summary>The radio is running on the source it was told to use, and it is locked.</summary>
    public bool IsHealthy =>
        State != FlexOscillatorSource.Unknown && Setting == State && Locked;

    /// <summary>
    /// An external or GPS reference is configured but is not what the radio is running on, or is
    /// not locked — on the 6000-series usually because it was selected without a reboot.
    /// </summary>
    public bool ConfiguredSourceNotInUse =>
        Setting is FlexOscillatorSource.External or FlexOscillatorSource.Gpsdo && !IsHealthy;

    /// <summary>A short line for a status bar.</summary>
    public string Describe()
    {
        if (State == FlexOscillatorSource.Unknown && Setting == FlexOscillatorSource.Unknown)
        {
            return "reference unknown";
        }

        string source = Name(State == FlexOscillatorSource.Unknown ? Setting : State);
        string ppb = FreqErrorPpb is int error and not 0 ? $", {error:+#;-#;0} ppb" : "";

        if (ConfiguredSourceNotInUse)
        {
            // Worth spelling out: the radio is not using what it was told to use, and on this
            // hardware the fix is a reboot, not a retry.
            return $"{Name(Setting)} set but {(Locked ? "not in use" : "not locked")} "
                + $"(running on {Name(State)}{ppb}) — reboot with the reference applied";
        }

        return $"{source} {(Locked ? "locked" : "UNLOCKED")}{ppb}";
    }

    private static string Name(FlexOscillatorSource source) => source switch
    {
        FlexOscillatorSource.Tcxo => "TCXO",
        FlexOscillatorSource.Ocxo => "OCXO",
        FlexOscillatorSource.External => "External ref",
        FlexOscillatorSource.Gpsdo => "GPSDO",
        _ => "unknown source",
    };

    /// <summary>Maps the radio's word for a source; unrecognised words become
    /// <see cref="FlexOscillatorSource.Unknown"/> rather than being guessed at.</summary>
    public static FlexOscillatorSource ParseSource(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "tcxo" => FlexOscillatorSource.Tcxo,
        "ocxo" => FlexOscillatorSource.Ocxo,
        "external" or "ext" => FlexOscillatorSource.External,
        "gpsdo" or "gps" => FlexOscillatorSource.Gpsdo,
        _ => FlexOscillatorSource.Unknown,
    };
}

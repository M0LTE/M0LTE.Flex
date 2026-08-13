namespace M0LTE.Flex;

/// <summary>The station's view of whether it still owns what it set up.</summary>
public enum FlexStationHealth
{
    /// <summary>Bring-up has not completed.</summary>
    Unbound,

    /// <summary>The station owns its slice and the radio agrees.</summary>
    Healthy,

    /// <summary>The slice is gone or belongs to someone else, and recovery has not run yet.</summary>
    SliceLost,

    /// <summary>A rebuild is in progress.</summary>
    Recovering,

    /// <summary>
    /// The slice has been lost repeatedly in a short window and the station has stood down.
    /// </summary>
    /// <remarks>
    /// This is a deliberate stop, not a failure to recover. See
    /// <see cref="FlexContentionPolicy"/> for why continuing to rebuild is the worse option.
    /// </remarks>
    Contended,

    /// <summary>The station has been disposed.</summary>
    Disposed,
}

/// <summary>Why a station's ownership check failed.</summary>
public enum FlexOwnershipFault
{
    /// <summary>The station owns the slice.</summary>
    None,

    /// <summary>The station has no slice bound yet.</summary>
    Unbound,

    /// <summary>The slice object is no longer present on the radio.</summary>
    SliceGone,

    /// <summary>The slice is present but reports <c>in_use=0</c>.</summary>
    SliceNotInUse,

    /// <summary>The slice is present and in use, but its <c>client_handle</c> is somebody else's.</summary>
    ForeignOwner,

    /// <summary>
    /// The radio's state looks fine, but the station has not finished republishing its binding
    /// after a loss. Transmitting now would race the rebuild.
    /// </summary>
    AwaitingRebuild,
}

/// <summary>The result of checking whether the station still owns its slice.</summary>
/// <param name="Fault">What is wrong, or <see cref="FlexOwnershipFault.None"/>.</param>
/// <param name="Detail">A human-readable description, suitable for a log line.</param>
public readonly record struct FlexOwnershipCheck(FlexOwnershipFault Fault, string Detail)
{
    /// <summary>True when the station still owns its slice.</summary>
    public bool IsOwned => Fault == FlexOwnershipFault.None;

    /// <summary>The passing check.</summary>
    public static FlexOwnershipCheck Owned { get; } = new(FlexOwnershipFault.None, "");
}

/// <summary>A health transition, for logging and for a host that wants to surface station state.</summary>
/// <param name="Health">The state entered.</param>
/// <param name="Detail">Why.</param>
public readonly record struct FlexStationHealthReport(FlexStationHealth Health, string Detail);

/// <summary>The outcome of a <see cref="FlexStation.RecoverAsync"/> call.</summary>
/// <param name="Recovered">True if the station owns a working slice again.</param>
/// <param name="Health">The health state the station ended in.</param>
/// <param name="Attempts">How many rebuild attempts were made.</param>
/// <param name="Detail">A human-readable description of the outcome.</param>
public readonly record struct FlexRecoveryResult(
    bool Recovered, FlexStationHealth Health, int Attempts, string Detail);

/// <summary>
/// How hard a station tries to rebuild a slice it has lost, and when it stops trying.
/// </summary>
/// <remarks>
/// <para>The stopping rule is the important half. Recovery works by creating a new slice, which
/// is the same primitive another client would use to take one; so two stations that both
/// recover automatically, on a radio where each one's bring-up displaces the other, will
/// rebuild against each other indefinitely. That is materially worse than one of them being
/// down: the radio churns slices continuously, the transmit slice and the PA state flap
/// underneath both of them, and neither station is reliably usable. It also hides the cause,
/// because the symptom becomes constant recovery rather than a clean stop with a reason.</para>
/// <para>So a station gives up on purpose. <see cref="LossThreshold"/> losses inside
/// <see cref="LossWindow"/> is taken as evidence that something is actively contending rather
/// than that a one-off happened, and the station stands down into
/// <see cref="FlexStationHealth.Contended"/> and stays there until a human or a host decides
/// what should own the radio. One working station and a clear diagnostic beats two stations
/// fighting.</para>
/// <para>The defaults treat a single loss as recoverable (the common case: an operator removed
/// the slice in SmartSDR, or a transient), and three losses in five minutes as a fight.</para>
/// </remarks>
public sealed record FlexContentionPolicy
{
    /// <summary>Rebuild attempts within one recovery episode before reporting failure. Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Delay before the first rebuild attempt. Default 1 s.</summary>
    /// <remarks>
    /// Not zero. If the slice went away because another client is mid-bring-up, rebuilding into
    /// the middle of that is how a race becomes a fight; letting the other party finish first
    /// costs a second and makes the outcome deterministic.
    /// </remarks>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling for the doubling backoff between attempts. Default 30 s.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many losses inside <see cref="LossWindow"/> count as active contention.
    /// Default 3. Set to 0 to never stand down (not advised on a shared radio).</summary>
    public int LossThreshold { get; init; } = 3;

    /// <summary>The rolling window over which <see cref="LossThreshold"/> is counted. Default 5 min.</summary>
    public TimeSpan LossWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The default policy.</summary>
    public static FlexContentionPolicy Default { get; } = new();

    /// <summary>A policy that never rebuilds: losses are reported, nothing is retaken.
    /// For a receive-only client, or one that must never contend for a shared radio.</summary>
    public static FlexContentionPolicy Never { get; } = new() { MaxAttempts = 0, LossThreshold = 1 };
}

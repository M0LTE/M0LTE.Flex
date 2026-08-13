using M0LTE.Radio.Audio;

namespace M0LTE.Flex;

/// <summary>
/// Thrown when a station is asked to key a slice it does not own. Derives from
/// <see cref="FlexProtocolException"/> so callers that already treat a keying failure as a
/// dropped frame keep working; catch this specifically to trigger
/// <see cref="FlexStation.RecoverAsync"/> instead.
/// </summary>
public sealed class FlexOwnershipException : FlexProtocolException
{
    /// <summary>Creates the exception from a failed ownership check.</summary>
    public FlexOwnershipException(FlexOwnershipCheck check)
        : base($"refusing to key: {check.Detail}") => Check = check;

    /// <summary>What was wrong with the ownership.</summary>
    public FlexOwnershipCheck Check { get; }
}

/// <summary>
/// PTT for a FlexRadio slice: there is no serial/GPIO line - keying is a command. Every
/// <see cref="Key"/> makes the slice the TX slice (<c>slice set &lt;idx&gt; tx=1</c>) and sends
/// <c>xmit 1</c>; every unkey sends <c>xmit 0</c>. TX state is observable on the
/// <c>interlock</c> object; an optional confirm waits for <c>state=TRANSMITTING</c> before
/// returning. See docs/flex-integration.md §2.5.
/// </summary>
/// <remarks>
/// <para>Ported with provenance from nCAT <c>ptt.go</c> (© Andrew Rodland KC2G, MIT): the
/// <c>slice set tx=1</c>-then-<c>xmit 1/0</c> sequence and the interlock
/// <c>state==TRANSMITTING</c> read.</para>
/// <para><b>Assumes this process is the radio's only transmitting client.</b> The per-keyup
/// <c>tx=1</c> re-assert (an idempotent one-command cost) means a TX slice moved by someone
/// else - another client, an operator in SmartSDR - is taken back on the next keyup instead
/// of silently transmitting the wrong slice forever, which is what the pre-0.12 once-only
/// claim did. But re-asserting unconditionally is itself the steal primitive on a genuinely
/// shared radio: a radio with a second transmitting station wants
/// <see cref="FlexArbitratedPtt"/>, which keys only into a quiet radio.</para>
/// <para><b>Re-asserting is not the same as owning.</b> Measured on a FLEX-6500 (fw 4.2.20,
/// 2026-08): after another client's bring-up displaced this station's slice, the radio
/// accepted <c>slice set 0 tx=1</c> with <c>err=0</c> on 10,528 consecutive keyups and did
/// nothing with it, because slice index 0 had been recycled to that other client - every
/// <c>xmit 1</c> then failed with 0x50000043 "The transmit slice has not been selected" and
/// the station was off air for six days. A command's success proves it was understood, never
/// that it took effect. So the claim is now preceded by an ownership read
/// (<see cref="FlexStation.VerifyOwnership"/>) and followed by a confirm against
/// <c>interlock tx_client_handle</c>, which is the only field that says the transmitting
/// client is us.</para>
/// </remarks>
public sealed class FlexPtt : IPttControl
{
    private readonly FlexClient _client;
    private readonly FlexSliceLease _lease;
    private readonly bool _confirmInterlock;
    private readonly int _confirmTimeoutMs;

    /// <summary>Creates a PTT following <paramref name="lease"/>, so a station that rebuilds a
    /// lost slice keeps this PTT working without it being recreated.</summary>
    /// <param name="client">The shared session.</param>
    /// <param name="lease">The station's slice lease.</param>
    /// <param name="confirmInterlock">Wait for the radio to confirm the keyup (best-effort; the
    /// transmitter otherwise budgets the settle in <c>--txdelay</c>).</param>
    /// <param name="confirmTimeoutMs">How long to wait for the interlock confirm.</param>
    public FlexPtt(
        FlexClient client, FlexSliceLease lease, bool confirmInterlock = false, int confirmTimeoutMs = 500)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(lease);
        _client = client;
        _lease = lease;
        _confirmInterlock = confirmInterlock;
        _confirmTimeoutMs = confirmTimeoutMs;
    }

    /// <summary>Creates a PTT bound to a fixed slice index, with no ownership checking.</summary>
    /// <param name="client">The shared session.</param>
    /// <param name="sliceIndex">The numeric slice index (e.g. "0").</param>
    /// <param name="confirmInterlock">Wait for <c>interlock state=TRANSMITTING</c> after keying.</param>
    /// <param name="confirmTimeoutMs">How long to wait for the interlock confirm.</param>
    /// <remarks>
    /// For an application that drives a slice it manages itself. Because there is no station
    /// behind it there is no recorded owner to check against, so this cannot detect the slice
    /// being taken; prefer <see cref="FlexStation.CreatePtt"/>.
    /// </remarks>
    public FlexPtt(
        FlexClient client, string sliceIndex, bool confirmInterlock = false, int confirmTimeoutMs = 500)
        : this(
            client,
            new FlexSliceLease(new FlexSliceBinding(
                sliceIndex, OwnerHandle: "", RxStreamId: 0, TxStreamId: 0, Generation: 1, IsValid: true)),
            confirmInterlock,
            confirmTimeoutMs)
    {
        ArgumentException.ThrowIfNullOrEmpty(sliceIndex);
    }

    /// <summary>The slice index currently being keyed.</summary>
    public string SliceIndex => _lease.Current.SliceIndex;

    /// <inheritdoc />
    /// <exception cref="FlexOwnershipException">The slice is gone, or belongs to another client.
    /// Keying on would transmit on somebody else's slice or not at all.</exception>
    public void Key()
    {
        FlexSliceBinding binding = _lease.Current;
        FlexOwnershipCheck check = CheckOwnership(binding);
        if (!check.IsOwned)
        {
            throw new FlexOwnershipException(check);
        }

        // Every keyup, not once: self-heals after anything else moved the TX slice.
        Send($"slice set {binding.SliceIndex} tx=1");
        Send("xmit 1");

        if (_confirmInterlock)
        {
            WaitForTransmitting(binding);
        }
    }

    /// <inheritdoc />
    public void Unkey() => Send("xmit 0");

    /// <summary>
    /// Reads the slice's status back to confirm it is present, in use, and still ours.
    /// </summary>
    /// <remarks>
    /// <para>A lease with no recorded owner (the fixed-slice constructor) cannot be checked for
    /// theft, so only presence is required there. That keeps the standalone constructor behaving
    /// as it always did rather than failing closed on information it never had.</para>
    /// <para>The radio's state is inspected <b>before</b> the lease's validity flag is consulted,
    /// so the fault reported is the real one. The station's status watcher invalidates the lease
    /// as soon as it sees the loss, and reporting that flag first would describe every theft as
    /// a vanished slice - accurate enough to refuse on, useless in a log.</para>
    /// </remarks>
    private FlexOwnershipCheck CheckOwnership(FlexSliceBinding binding)
    {
        if (binding.SliceIndex.Length == 0)
        {
            return new FlexOwnershipCheck(FlexOwnershipFault.Unbound, "no slice is bound");
        }

        if (binding.OwnerHandle.Length == 0)
        {
            return binding.IsValid ? FlexOwnershipCheck.Owned : AwaitingRebuild(binding);
        }

        if (!_client.TryGetObject(
                "slice " + binding.SliceIndex, out IReadOnlyDictionary<string, string> slice)
            || slice.Count == 0)
        {
            return new FlexOwnershipCheck(
                FlexOwnershipFault.SliceGone, $"slice {binding.SliceIndex} is no longer on the radio");
        }

        if (slice.TryGetValue("in_use", out string? inUse) && inUse is "0" or "false" or "False")
        {
            return new FlexOwnershipCheck(
                FlexOwnershipFault.SliceNotInUse, $"slice {binding.SliceIndex} reports in_use=0");
        }

        if (slice.TryGetValue("client_handle", out string? owner)
            && owner.Length > 0
            && !FlexHandle.Matches(owner, binding.OwnerHandle))
        {
            return new FlexOwnershipCheck(
                FlexOwnershipFault.ForeignOwner,
                $"slice {binding.SliceIndex} now belongs to client {owner}, not {binding.OwnerHandle}");
        }

        // The radio is happy. Only now does a lease still marked lost matter: it means a rebuild
        // is in flight and has not republished yet, so keying would race it.
        return binding.IsValid ? FlexOwnershipCheck.Owned : AwaitingRebuild(binding);
    }

    private static FlexOwnershipCheck AwaitingRebuild(FlexSliceBinding binding) =>
        new(FlexOwnershipFault.AwaitingRebuild,
            $"slice {binding.SliceIndex} is marked lost and has not been rebuilt yet");

    private void Send(string command) =>
        _client.SendCommandExpectOkAsync(command).GetAwaiter().GetResult();

    /// <summary>
    /// Waits for the radio to confirm that <b>we</b> are transmitting.
    /// </summary>
    /// <remarks>
    /// <c>state=TRANSMITTING</c> alone is not that confirmation: on a shared radio it is equally
    /// true while another client holds the PA. <c>tx_client_handle</c> is the field that names
    /// the transmitting client, so it is what gets compared when we know who we are.
    /// </remarks>
    private void WaitForTransmitting(FlexSliceBinding binding)
    {
        long deadline = Environment.TickCount64 + _confirmTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_client.TryGetObject("interlock", out IReadOnlyDictionary<string, string> interlock)
                && interlock.TryGetValue("state", out string? current)
                && current == "TRANSMITTING"
                && TransmitterIsOurs(interlock, binding))
            {
                return;
            }

            Thread.Sleep(5);
        }
    }

    private static bool TransmitterIsOurs(
        IReadOnlyDictionary<string, string> interlock, FlexSliceBinding binding)
    {
        // Nothing recorded to compare against, or the radio does not report the field: fall back
        // to the state-only check rather than refusing to confirm on missing information.
        if (binding.OwnerHandle.Length == 0
            || !interlock.TryGetValue("tx_client_handle", out string? tx)
            || tx.Length == 0)
        {
            return true;
        }

        // The idle value. Seen while the interlock has moved to TRANSMITTING but no client has
        // been recorded yet, so it means "not yet", not "somebody else".
        return FlexHandle.IsUnset(tx) || FlexHandle.Matches(tx, binding.OwnerHandle);
    }
}

namespace M0LTE.Flex;

/// <summary>
/// The identifiers a station currently holds on the radio: which slice it owns, which client
/// handle owns it, and the two DAX stream ids bound to it. Immutable, so a reader gets a
/// coherent set rather than a slice index from before a rebuild and a stream id from after.
/// </summary>
/// <param name="SliceIndex">The numeric slice index (e.g. "0"), or empty when unbound.</param>
/// <param name="OwnerHandle">The client handle the slice is expected to belong to. For a
/// headless station this is our own session handle; for an attach station it is the SmartSDR
/// client we bound to.</param>
/// <param name="RxStreamId">The DAX-RX stream id.</param>
/// <param name="TxStreamId">The DAX-TX stream id.</param>
/// <param name="Generation">Increments every time the station rebuilds the binding. Lets a
/// consumer notice it is looking at a different slice than it was without diffing the fields.</param>
/// <param name="IsValid">False once the binding is known lost and not yet rebuilt: the slice
/// went away, or another client took it. A transmitter must not key on an invalid binding.</param>
public readonly record struct FlexSliceBinding(
    string SliceIndex,
    string OwnerHandle,
    uint RxStreamId,
    uint TxStreamId,
    int Generation,
    bool IsValid)
{
    /// <summary>The unbound binding, before setup has run or after it has been surrendered.</summary>
    public static FlexSliceBinding None { get; } = new("", "", 0, 0, 0, false);
}

/// <summary>
/// A mutable, thread-safe holder for the <see cref="FlexSliceBinding"/> a station currently
/// owns, shared by reference with everything the station hands out (PTT, DAX-RX input, DAX-TX
/// output).
/// </summary>
/// <remarks>
/// <para>This exists so that recovering a lost slice does not require rebuilding the objects
/// bound to it. A slice index and its DAX stream ids are captured once at bring-up, and the
/// natural consequence of that is that anything holding them has to be torn down and recreated
/// when the station rebuilds - which pushes the whole problem onto the host application and
/// makes recovery something only a caller sophisticated enough to re-plumb its audio can use.
/// Sharing one lease instead means <see cref="FlexStation.RecoverAsync"/> can swap in the new
/// identifiers and every existing consumer follows, with no cooperation from the host.</para>
/// <para>Reads are lock-free and allocation-free: the binding is a struct published through a
/// single reference, so the DAX receive path can consult it per packet without a lock and
/// without touching the heap. Writes are serialised; they are rare (bring-up and recovery).</para>
/// </remarks>
public sealed class FlexSliceLease
{
    // Boxed so publication is a single reference write, which is atomic. Readers take a copy of
    // the struct; they never observe a half-updated set of identifiers.
    private volatile object _current = FlexSliceBinding.None;

    /// <summary>Creates an unbound lease.</summary>
    public FlexSliceLease()
    {
    }

    /// <summary>Creates a lease already holding <paramref name="binding"/>. Used by the
    /// fixed-identifier constructors that predate leasing, so an application that builds a PTT
    /// or an audio stream by hand behaves exactly as it did before.</summary>
    public FlexSliceLease(FlexSliceBinding binding) => _current = binding;

    /// <summary>The identifiers currently held.</summary>
    public FlexSliceBinding Current => (FlexSliceBinding)_current;

    /// <summary>Publishes a new binding, incrementing <see cref="FlexSliceBinding.Generation"/>.</summary>
    public FlexSliceBinding Bind(string sliceIndex, string ownerHandle, uint rxStreamId, uint txStreamId)
    {
        ArgumentNullException.ThrowIfNull(sliceIndex);
        ArgumentNullException.ThrowIfNull(ownerHandle);

        FlexSliceBinding next = new(
            sliceIndex, ownerHandle, rxStreamId, txStreamId, Current.Generation + 1, IsValid: true);
        _current = next;
        return next;
    }

    /// <summary>
    /// Marks the current binding invalid without discarding what it pointed at.
    /// </summary>
    /// <remarks>
    /// The identifiers are deliberately kept. Recovery needs to know which slice it lost in
    /// order to describe the loss, and teardown needs to know whether there is anything of ours
    /// left on the radio to remove; zeroing them here would throw both away at exactly the
    /// moment they became interesting.
    /// </remarks>
    public FlexSliceBinding Invalidate()
    {
        FlexSliceBinding current = Current;
        if (!current.IsValid)
        {
            return current;
        }

        FlexSliceBinding next = current with { IsValid = false };
        _current = next;
        return next;
    }
}

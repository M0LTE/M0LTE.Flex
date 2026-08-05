using M0LTE.Radio.Audio;

namespace M0LTE.Flex;

/// <summary>Tuning for <see cref="FlexArbitratedPtt"/>. The defaults suit a LAN radio shared by
/// two cooperating stations; every timing is a policy knob, not a protocol constant.</summary>
public sealed record FlexPttArbitrationOptions
{
    /// <summary>How long a keyup may wait for the radio to go quiet before giving up with
    /// <see cref="FlexTxContendedException"/>. Bounded so shutdown mid-contention does not
    /// dawdle; the channel's own inhibit gate defers queued frames long before this.</summary>
    public TimeSpan QuietWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long after <c>xmit 1</c> to wait for the win condition (interlock
    /// TRANSMITTING and our slice still holding <c>tx=1</c>).</summary>
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>How many keyup attempts before the contended give-up. Two racers both keying
    /// in the same few milliseconds is a coincidence; three straight losses is a peer that is
    /// not cooperating.</summary>
    public int KeyAttempts { get; init; } = 3;

    /// <summary>Base backoff between lost-race attempts; jittered so two synchronized losers
    /// do not retry in lock step forever.</summary>
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a parked <c>UNKEY_REQUESTED</c> counts as busy. The waveform-IQ path
    /// (measured on a FLEX-6500, modelled by <see cref="MockFlexRadio"/>) parks the interlock
    /// there and never announces RECEIVE, so without a staleness bound one waveform burst by a
    /// peer would read as busy forever.</summary>
    public TimeSpan StaleUnkeyGrace { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Transmit-filter high cut to re-assert before each keyup, Hz. The filter is a
    /// GLOBAL, PERSISTENT radio setting, so two stations with different bands overwrite each
    /// other; re-asserting ours while the radio is quiet (never mid-peer-burst, which would
    /// truncate THEIR transmission on air) keeps every keyup inside its own filter. Null
    /// leaves the filter alone.</summary>
    public int? TransmitFilterHighHz { get; init; }

    /// <summary>RF power (0-100) to re-assert before each keyup. Null - the default - never
    /// touches it: power is held per station on a real 6500, so each station's own value
    /// should apply when its slice keys. The knob exists for the case hardware probing proves
    /// otherwise.</summary>
    public int? RfPower { get; init; }
}

/// <summary>A keyup gave up because another station held the radio. An outcome, not a fault:
/// distinct from <see cref="FlexProtocolException"/> on purpose, so callers can defer or drop
/// the frame rather than treat the radio as broken.</summary>
public sealed class FlexTxContendedException(string message) : Exception(message);

/// <summary>
/// PTT for a FlexRadio slice on a radio SHARED between transmitting clients - the arbitration
/// <see cref="FlexPtt"/> deliberately does not do. Where <see cref="FlexPtt"/> assumes sole
/// ownership (re-asserting <c>tx=1</c> per keyup, keying unconditionally),
/// this type emits NO radio-global write until the radio is quiet, and only believes it is
/// transmitting when the radio agrees.
/// </summary>
/// <remarks>
/// <para>The keyup sequence is strictly ordered, and the order is load-bearing:</para>
/// <para><c>wait-quiet -> [transmit filter re-assert] -> slice set tx=1 -> xmit 1 -> confirm</c></para>
/// <para><c>slice set tx=1</c> is precisely the primitive that steals the PA from another
/// station mid-burst, and a global filter write mid-peer-burst truncates their transmission on
/// air - so neither is sent before the quiet check passes. The confirm is
/// <c>interlock state=TRANSMITTING</c> <b>and our slice still holds tx=1</b>: TRANSMITTING
/// alone is not a win, because in a tie both racers see it. A keyup that never got a quiet
/// radio throws <see cref="FlexTxContendedException"/> having sent nothing at all (pinned by
/// CommandLog in the tests); a keyup that keyed and then lost the race unkeys and backs off
/// with jitter. <see cref="Unkey"/> sends <c>xmit 0</c> only when the current keyup was won -
/// a losing <c>xmit 0</c> could cut the winner's PA out from under them.</para>
/// <para>Interlock semantics between two live clients (whether <c>slice set tx=1</c> is
/// rejected or steals mid-burst, whether <c>xmit</c> is global or per-client) are not fully
/// characterised on hardware yet; this type is conservative under every reading - it never
/// writes into a busy radio, and it detects rather than assumes a win.</para>
/// </remarks>
public sealed class FlexArbitratedPtt : IPttControl, IDisposable
{
    private readonly FlexClient _client;
    private readonly string _sliceIndex;
    private readonly FlexPttArbitrationOptions _options;
    private readonly FlexInterlockView _view;
    private readonly Random _jitter = new();
    private bool _won;

    /// <summary>Creates an arbitrated PTT bound to slice <paramref name="sliceIndex"/>.</summary>
    public FlexArbitratedPtt(
        FlexClient client, string sliceIndex, FlexPttArbitrationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(sliceIndex);
        _client = client;
        _sliceIndex = sliceIndex;
        _options = options ?? new FlexPttArbitrationOptions();
        _view = new FlexInterlockView(client);
    }

    /// <summary>True while the interlock says somebody is transmitting and it is not a keyup
    /// this instance won - the cheap predicate for a transmit-inhibit gate, so queued frames
    /// defer politely before they are even rendered.</summary>
    public bool AnotherStationTransmitting => !_won && Busy();

    /// <inheritdoc />
    public void Key()
    {
        long deadline = Environment.TickCount64 + (long)_options.QuietWaitTimeout.TotalMilliseconds;
        for (int attempt = 1; ; attempt++)
        {
            // Nothing - not the filter, not the slice claim - goes on the wire until quiet.
            while (Busy())
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw new FlexTxContendedException(
                        $"the radio stayed busy for {_options.QuietWaitTimeout.TotalSeconds:F0} s "
                        + "(another station transmitting); nothing was sent");
                }

                Thread.Sleep(5);
            }

            if (_options.TransmitFilterHighHz is int high)
            {
                Send($"transmit set filter_high={high}");
            }

            if (_options.RfPower is int power)
            {
                Send($"transmit set rfpower={power}");
            }

            Send($"slice set {_sliceIndex} tx=1");
            Send("xmit 1");

            if (ConfirmWon())
            {
                _won = true;
                return;
            }

            // Lost the race window: someone keyed between our quiet read and our xmit landing.
            // Withdraw and back off with jitter so two synchronized losers cannot retry in
            // step. (Pre-hardware-probe policy: if xmit turns out to be per-client refcounted
            // this unkey is REQUIRED; if global it is a bounded risk taken only after a
            // detected tie - see the type remarks.)
            Send("xmit 0");
            if (attempt >= _options.KeyAttempts || Environment.TickCount64 >= deadline)
            {
                throw new FlexTxContendedException(
                    $"lost the keyup race {attempt} time(s) (another station holds the PA)");
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(
                _options.RetryBackoff.TotalMilliseconds * (0.5 + _jitter.NextDouble())));
        }
    }

    /// <inheritdoc />
    public void Unkey()
    {
        // Only a keyup this instance won may drop the PA: an unkey after a lost or contended
        // keyup would cut the WINNING station's burst.
        if (_won)
        {
            _won = false;
            Send("xmit 0");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _view.Dispose();

    private bool Busy()
    {
        (string? state, long sinceMs) = _view.Read();
        return state switch
        {
            "PTT_REQUESTED" or "TRANSMITTING" => true,
            // The waveform path parks here forever; treat it as busy only briefly.
            "UNKEY_REQUESTED" => sinceMs < _options.StaleUnkeyGrace.TotalMilliseconds,
            // RECEIVE, READY, and unknown (no interlock line seen yet) all count as quiet: a
            // cold-joining client may never receive a snapshot, and the confirm step below is
            // what catches keying into a burst the view could not see.
            _ => false,
        };
    }

    private bool ConfirmWon()
    {
        long deadline = Environment.TickCount64 + (long)_options.ConfirmTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            (string? state, _) = _view.Read();
            if (state == "TRANSMITTING" && OurSliceHoldsTx())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }

    private bool OurSliceHoldsTx() =>
        _client.TryGetObject($"slice {_sliceIndex}", out IReadOnlyDictionary<string, string> slice)
        && slice.TryGetValue("tx", out string? tx)
        && tx == "1";

    private void Send(string command) =>
        _client.SendCommandExpectOkAsync(command).GetAwaiter().GetResult();
}

/// <summary>Tracks the interlock's state and when it last changed, off the client's status
/// stream. Internal: the arbitrated PTT is its only consumer; promote if another appears.</summary>
internal sealed class FlexInterlockView : IDisposable
{
    private readonly FlexClient _client;
    private readonly object _gate = new();
    private string? _state;
    private long _sinceTick;

    internal FlexInterlockView(FlexClient client)
    {
        _client = client;
        // Seed from whatever the client already knows, then follow every change. A radio that
        // never sends an interlock line leaves the state null, which reads as quiet by design.
        if (client.TryGetObject("interlock", out IReadOnlyDictionary<string, string> interlock)
            && interlock.TryGetValue("state", out string? seeded))
        {
            _state = seeded;
            _sinceTick = Environment.TickCount64;
        }

        client.StatusUpdated += OnStatus;
    }

    internal (string? State, long SinceMs) Read()
    {
        lock (_gate)
        {
            return (_state, _state is null ? 0 : Environment.TickCount64 - _sinceTick);
        }
    }

    public void Dispose() => _client.StatusUpdated -= OnStatus;

    private void OnStatus(FlexStatusUpdate update)
    {
        if (update.Object != "interlock"
            || !update.Updated.TryGetValue("state", out string? state))
        {
            return;
        }

        lock (_gate)
        {
            if (state != _state)
            {
                _state = state;
                _sinceTick = Environment.TickCount64;
            }
        }
    }
}

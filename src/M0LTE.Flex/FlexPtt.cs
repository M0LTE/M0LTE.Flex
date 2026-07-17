using M0LTE.Radio.Audio;

namespace M0LTE.Flex;

/// <summary>
/// PTT for a FlexRadio slice: there is no serial/GPIO line — keying is a command. On the
/// first <see cref="Key"/> the slice is made the TX slice (<c>slice set &lt;idx&gt; tx=1</c>,
/// once), then every key sends <c>xmit 1</c> and every unkey <c>xmit 0</c>. TX state is
/// observable on the <c>interlock</c> object; an optional confirm waits for
/// <c>state=TRANSMITTING</c> before returning. See docs/flex-integration.md §2.5.
/// </summary>
/// <remarks>
/// Ported with provenance from nCAT <c>ptt.go</c> (© Andrew Rodland KC2G, MIT): the
/// <c>slice set tx=1</c>-then-<c>xmit 1/0</c> sequence and the interlock
/// <c>state==TRANSMITTING</c> read.
/// </remarks>
public sealed class FlexPtt : IPttControl
{
    private readonly FlexClient _client;
    private readonly string _sliceIndex;
    private readonly bool _confirmInterlock;
    private readonly int _confirmTimeoutMs;
    private bool _txSliceClaimed;

    /// <summary>Creates a PTT bound to slice <paramref name="sliceIndex"/>.</summary>
    /// <param name="client">The shared session.</param>
    /// <param name="sliceIndex">The numeric slice index (e.g. "0"), from discovery of the
    /// slice by its letter.</param>
    /// <param name="confirmInterlock">Wait for <c>interlock state=TRANSMITTING</c> after
    /// keying (best-effort; the transmitter otherwise budgets the settle in <c>--txdelay</c>).</param>
    /// <param name="confirmTimeoutMs">How long to wait for the interlock confirm.</param>
    public FlexPtt(
        FlexClient client, string sliceIndex, bool confirmInterlock = false, int confirmTimeoutMs = 500)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(sliceIndex);
        _client = client;
        _sliceIndex = sliceIndex;
        _confirmInterlock = confirmInterlock;
        _confirmTimeoutMs = confirmTimeoutMs;
    }

    /// <inheritdoc />
    public void Key()
    {
        if (!_txSliceClaimed)
        {
            Send($"slice set {_sliceIndex} tx=1");
            _txSliceClaimed = true;
        }

        Send("xmit 1");

        if (_confirmInterlock)
        {
            WaitForInterlock("TRANSMITTING");
        }
    }

    /// <inheritdoc />
    public void Unkey() => Send("xmit 0");

    private void Send(string command) =>
        _client.SendCommandExpectOkAsync(command).GetAwaiter().GetResult();

    private void WaitForInterlock(string state)
    {
        long deadline = Environment.TickCount64 + _confirmTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_client.TryGetObject("interlock", out IReadOnlyDictionary<string, string> interlock)
                && interlock.TryGetValue("state", out string? current)
                && current == state)
            {
                return;
            }

            Thread.Sleep(5);
        }
    }
}

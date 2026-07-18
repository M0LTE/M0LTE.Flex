using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>Disposal robustness for <see cref="FlexClient"/>: it may be disposed more than once
/// without throwing — an owning <see cref="FlexStation"/>/<see cref="FlexWaveform"/> disposes it,
/// so a caller that also wraps the client in <c>await using</c> must not crash on the second call.</summary>
public sealed class FlexClientDisposeTests
{
    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var mock = new MockFlexRadio(DaxStreamFormat.FullBandwidth, MockRxMode.Silence, MockSetupMode.Attach);
        mock.Start();
        await using var _ = mock;

        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        await client.DisposeAsync();

        Func<Task> second = async () => await client.DisposeAsync();
        await second.Should().NotThrowAsync();
    }
}

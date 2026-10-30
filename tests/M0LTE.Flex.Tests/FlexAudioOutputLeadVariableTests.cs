using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>The environment variable reaches a sink made the ordinary way, through
/// <see cref="FlexStation.CreateAudioOutput"/>. Not run alongside other tests, because the
/// environment is the whole process's.</summary>
[Collection(nameof(TransmitLeadVariableCollection))]
public sealed class FlexAudioOutputLeadVariableTests
{
    [Theory]
    [InlineData(null, 80)]
    [InlineData("off", 0)]
    [InlineData("0", 0)]
    [InlineData("40", 40)]
    public async Task A_station_sink_takes_its_lead_from_the_environment(string? value, int expectedMs)
    {
        var mock = new MockFlexRadio(DaxStreamFormat.ReducedBandwidth, MockRxMode.Silence, MockSetupMode.Attach);
        mock.Start();
        await using var _ = mock;
        FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", mock.TcpPort, mock.UdpPort);
        FlexStation station = await FlexStation.SetUpAsync(
            client, DaxStreamFormat.ReducedBandwidth, new FlexStationOptions { Keepalive = false });
        await using var __ = station;

        string? before = Environment.GetEnvironmentVariable(FlexAudioOutput.TransmitLeadVariable);
        Environment.SetEnvironmentVariable(FlexAudioOutput.TransmitLeadVariable, value);
        try
        {
            FlexAudioOutput output = station.CreateAudioOutput();
            output.TransmitLead.Should().Be(TimeSpan.FromMilliseconds(expectedMs));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlexAudioOutput.TransmitLeadVariable, before);
        }
    }
}

/// <summary>Runs the tests that set the process environment on their own.</summary>
[CollectionDefinition(nameof(TransmitLeadVariableCollection), DisableParallelization = true)]
public sealed class TransmitLeadVariableCollection;

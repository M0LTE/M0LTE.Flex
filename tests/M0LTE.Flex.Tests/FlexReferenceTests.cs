using System.Net;
using System.Net.Sockets;
using System.Text;
using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>
/// The radio's frequency-reference state, read off the <c>radio</c> and <c>radio oscillator</c>
/// status objects. The transitions here are the ones observed on a 6500 (issue #11): an internal
/// TCXO calibrated against a GPS-locked 10 MHz, and an external reference after a reboot with the
/// reference applied.
/// </summary>
public sealed class FlexReferenceTests
{
    /// <summary>Runs a scripted radio that emits <paramref name="status"/> lines after the
    /// prologue, and returns the client's reference state once they have all arrived.</summary>
    private static async Task<FlexReferenceStatus> ReferenceAfterAsync(params string[] status)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var settled = new TaskCompletionSource();

        Task server = Task.Run(async () =>
        {
            using TcpClient conn = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = conn.GetStream();
            var writer = new StreamWriter(stream, new ASCIIEncoding()) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync("V1.4.0.0");
            await writer.WriteLineAsync("H12345678");
            foreach (string line in status)
            {
                await writer.WriteLineAsync(line);
            }

            await settled.Task;
        });

        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", port, 0, CancellationToken.None);

        // Poll rather than wait on ReferenceChanged: the status can arrive before a handler
        // is attached, and a source that maps to Unknown would then never satisfy an
        // event-based wait. Presence of the radio's own word is the reliable signal.
        bool wantsPpb = status.Any(s => s.Contains("freq_error_ppb", StringComparison.Ordinal));
        FlexReferenceStatus reference = FlexReferenceStatus.Unknown;
        for (int i = 0; i < 250; i++)
        {
            reference = client.Reference;
            bool heardOscillator = reference.StateRaw.Length > 0 || reference.SettingRaw.Length > 0;
            if (heardOscillator && (!wantsPpb || reference.FreqErrorPpb is not null))
            {
                break;
            }

            await Task.Delay(20);
        }

        settled.SetResult();
        await server;
        return reference;
    }

    [Fact]
    public async Task An_Internal_Tcxo_Calibrated_Against_A_Gps_Locked_Reference_Reads_As_Locked()
    {
        // Observed on a 6500: tcxo/tcxo/locked, with a calibration correction.
        FlexReferenceStatus reference = await ReferenceAfterAsync(
            "S12345678|radio oscillator state=tcxo setting=tcxo locked=1",
            "S12345678|radio freq_error_ppb=-1390");

        Assert.Equal(FlexOscillatorSource.Tcxo, reference.Setting);
        Assert.Equal(FlexOscillatorSource.Tcxo, reference.State);
        Assert.True(reference.Locked);
        Assert.Equal(-1390, reference.FreqErrorPpb);
        Assert.True(reference.IsHealthy);
        Assert.False(reference.ConfiguredSourceNotInUse);
        Assert.Contains("TCXO locked", reference.Describe());
        Assert.Contains("-1390 ppb", reference.Describe());
    }

    [Fact]
    public async Task An_External_Reference_After_A_Reboot_Reads_As_External_And_Locked()
    {
        FlexReferenceStatus reference = await ReferenceAfterAsync(
            "S12345678|radio oscillator state=external setting=external locked=1",
            "S12345678|radio freq_error_ppb=0");

        Assert.Equal(FlexOscillatorSource.External, reference.State);
        Assert.True(reference.IsHealthy);
        Assert.Equal(0, reference.FreqErrorPpb);
        // A zero correction is not worth cluttering a status bar with.
        Assert.Equal("External ref locked", reference.Describe());
    }

    [Fact]
    public async Task A_Gpsdo_Is_Recognised_As_Its_Own_Source()
    {
        FlexReferenceStatus reference = await ReferenceAfterAsync(
            "S12345678|radio oscillator state=gpsdo setting=gpsdo locked=1");

        Assert.Equal(FlexOscillatorSource.Gpsdo, reference.State);
        Assert.True(reference.IsHealthy);
        Assert.Contains("GPSDO locked", reference.Describe());
    }

    [Fact]
    public async Task External_Selected_Without_A_Reboot_Is_Reported_As_Not_In_Use()
    {
        // The case the issue calls out: on the 6000-series, external detection is startup-only,
        // so selecting it live leaves the radio on its internal oscillator. Reporting the
        // configured source as though it were in use would be a lie an operator acts on.
        FlexReferenceStatus reference = await ReferenceAfterAsync(
            "S12345678|radio oscillator state=tcxo setting=external locked=0");

        Assert.Equal(FlexOscillatorSource.External, reference.Setting);
        Assert.Equal(FlexOscillatorSource.Tcxo, reference.State);
        Assert.False(reference.IsHealthy);
        Assert.True(reference.ConfiguredSourceNotInUse);
        Assert.Contains("reboot with the reference applied", reference.Describe());
    }

    [Fact]
    public async Task An_Unrecognised_Source_Is_Unknown_Rather_Than_Guessed_At()
    {
        FlexReferenceStatus reference = await ReferenceAfterAsync(
            "S12345678|radio oscillator state=rubidium setting=rubidium locked=1");

        Assert.Equal(FlexOscillatorSource.Unknown, reference.State);
        // The radio's own word survives, so a consumer can still show it.
        Assert.Equal("rubidium", reference.StateRaw);
        Assert.False(reference.IsHealthy);
    }

    [Fact]
    public void Before_The_Radio_Has_Said_Anything_The_Reference_Is_Unknown()
    {
        FlexReferenceStatus reference = FlexReferenceStatus.Unknown;

        Assert.Equal(FlexOscillatorSource.Unknown, reference.Setting);
        Assert.False(reference.Locked);
        Assert.Null(reference.FreqErrorPpb);
        Assert.Equal("reference unknown", reference.Describe());
    }

    [Theory]
    [InlineData("tcxo", FlexOscillatorSource.Tcxo)]
    [InlineData("TCXO", FlexOscillatorSource.Tcxo)]
    [InlineData("ocxo", FlexOscillatorSource.Ocxo)]
    [InlineData("external", FlexOscillatorSource.External)]
    [InlineData("gpsdo", FlexOscillatorSource.Gpsdo)]
    [InlineData("", FlexOscillatorSource.Unknown)]
    [InlineData(null, FlexOscillatorSource.Unknown)]
    public void Sources_Are_Mapped_Case_Insensitively(string? raw, FlexOscillatorSource expected)
    {
        Assert.Equal(expected, FlexReferenceStatus.ParseSource(raw));
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>Session line parse/serialize against a scripted in-process TCP radio: the
/// prologue, the <c>C&lt;seq&gt;|cmd</c> command format and monotonic sequence, the
/// <c>R&lt;seq&gt;|err|msg</c> result await, and the <c>S…</c> status map.</summary>
public sealed class FlexClientSessionTests
{
    [Fact]
    public async Task Prologue_command_result_and_status_round_trip()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var capturedCommands = new List<string>();
        var serverDone = new TaskCompletionSource();

        Task server = Task.Run(async () =>
        {
            using TcpClient conn = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = conn.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, new ASCIIEncoding()) { AutoFlush = true, NewLine = "\n" };

            // Prologue.
            await writer.WriteLineAsync("V1.4.0.0");
            await writer.WriteLineAsync("H2AB4C1D");

            // First command: sub slice all → OK, then a slice status update.
            string? cmd1 = await reader.ReadLineAsync();
            capturedCommands.Add(cmd1 ?? "");
            await writer.WriteLineAsync("R1|0|");
            await writer.WriteLineAsync("S2AB4C1D|slice 0 index_letter=A in_use=1 RF_frequency=14.100000");

            // Second command: a failing one → non-zero error with a message.
            string? cmd2 = await reader.ReadLineAsync();
            capturedCommands.Add(cmd2 ?? "");
            await writer.WriteLineAsync("R2|50000015|bad command");

            serverDone.SetResult();
        });

        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", port);

        client.Version.Should().Be("1.4.0.0");
        client.Handle.Should().Be("2AB4C1D");

        FlexResult ok = await client.SendCommandAsync("sub slice all");
        ok.Serial.Should().Be(1);
        ok.IsOk.Should().BeTrue();

        // The status update should land in the object map.
        await WaitForAsync(() => client.TryGetObject("slice 0", out _));
        client.TryGetObject("slice 0", out IReadOnlyDictionary<string, string> slice).Should().BeTrue();
        slice["index_letter"].Should().Be("A");
        slice["in_use"].Should().Be("1");
        slice["RF_frequency"].Should().Be("14.100000");

        FlexResult failed = await client.SendCommandAsync("xmit maybe");
        failed.Serial.Should().Be(2);
        failed.IsOk.Should().BeFalse();
        failed.Error.Should().Be(0x50000015u);
        failed.Message.Should().Be("bad command");

        await serverDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server;

        // Command serialization: monotonic C<seq>|<cmd>.
        capturedCommands.Should().Equal("C1|sub slice all", "C2|xmit maybe");
    }

    [Fact]
    public async Task Expect_ok_throws_on_a_non_zero_error()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task server = Task.Run(async () =>
        {
            using TcpClient conn = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = conn.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, new ASCIIEncoding()) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync("V1.0.0.0");
            await writer.WriteLineAsync("H0001");
            await reader.ReadLineAsync();
            await writer.WriteLineAsync("R1|50000015|nope");
            await reader.ReadLineAsync(); // drain until close
        });

        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", port);
        Func<Task> act = () => client.SendCommandExpectOkAsync("stream create type=dax_rx dax_channel=1");
        await act.Should().ThrowAsync<FlexProtocolException>();

        listener.Stop();
    }

    [Fact]
    public async Task Client_station_status_decodes_0x7f_to_space()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task server = Task.Run(async () =>
        {
            using TcpClient conn = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = conn.GetStream();
            var writer = new StreamWriter(stream, new ASCIIEncoding()) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync("V1.0.0.0");
            await writer.WriteLineAsync("H0001");
            // The radio sends embedded spaces in station names as 0x7f.
            await writer.WriteLineAsync("S0001|client 0x0001 station=Main\x7fStation client_id=uuid-1");
            await Task.Delay(200);
        });

        await using FlexClient client = await FlexClient.ConnectAsync("127.0.0.1", port);
        await WaitForAsync(() => client.TryGetObject("client 0x0001", out _));
        client.TryGetObject("client 0x0001", out IReadOnlyDictionary<string, string> obj).Should().BeTrue();
        obj["station"].Should().Be("Main Station");
        obj["client_id"].Should().Be("uuid-1");

        listener.Stop();
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException("condition not met in time");
            }

            await Task.Delay(10);
        }
    }
}

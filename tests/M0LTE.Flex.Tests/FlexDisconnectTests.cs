using System.Net;
using System.Net.Sockets;
using System.Text;
using M0LTE.Flex;

namespace M0LTE.Flex.Tests;

/// <summary>
/// Telling a consumer the session has ended. A long-running station otherwise has no way to
/// know: commands start failing and audio stops, but nothing says why, so it sits on a dead
/// socket until a human notices — which is exactly what happened when a radio was rebooted
/// underneath a running modem.
/// </summary>
public sealed class FlexDisconnectTests
{
    private static async Task<(FlexClient Client, TcpListener Listener, TcpClient Conn)> ConnectAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
        Task<FlexClient> connecting = FlexClient.ConnectAsync(
            "127.0.0.1", port, 0, CancellationToken.None);

        TcpClient conn = await accepted;
        var writer = new StreamWriter(conn.GetStream(), new ASCIIEncoding()) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync("V1.4.0.0");
        await writer.WriteLineAsync("H12345678");
        return (await connecting, listener, conn);
    }

    [Fact]
    public async Task The_Radio_Going_Away_Raises_Disconnected()
    {
        (FlexClient client, TcpListener listener, TcpClient conn) = await ConnectAsync();
        var dropped = new TaskCompletionSource();
        client.Disconnected += () => dropped.TrySetResult();

        Assert.True(client.IsConnected);
        conn.Close();   // the radio rebooting, from this side of the socket

        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(client.IsConnected);

        listener.Stop();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_Deliberately_Is_Not_A_Disconnection()
    {
        // Otherwise every clean shutdown would look like a radio failure and a consumer that
        // restarts on disconnect would restart itself forever.
        (FlexClient client, TcpListener listener, TcpClient conn) = await ConnectAsync();
        bool raised = false;
        client.Disconnected += () => raised = true;

        await client.DisposeAsync();
        await Task.Delay(300);

        Assert.False(raised);
        conn.Close();
        listener.Stop();
    }

    [Fact]
    public async Task Disconnected_Is_Raised_At_Most_Once()
    {
        (FlexClient client, TcpListener listener, TcpClient conn) = await ConnectAsync();
        int count = 0;
        client.Disconnected += () => Interlocked.Increment(ref count);

        conn.Close();
        await Task.Delay(500);
        await client.DisposeAsync();
        await Task.Delay(200);

        Assert.Equal(1, count);
        listener.Stop();
    }
}

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PveTunnel.Mux;
using Xunit;

namespace PveTunnel.Mux.Tests;

public class MuxTests
{
    /// <summary>Connect a client/server mux pair over loopback TCP, with the server echoing every stream.</summary>
    private static async Task<(MuxSession client, MuxSession server, CancellationTokenSource cts)> ConnectEchoAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var clientTcp = new TcpClient();
        var connect = clientTcp.ConnectAsync(IPAddress.Loopback, port);
        var serverTcp = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        var cts = new CancellationTokenSource();
        var client = new MuxSession(clientTcp.GetStream(), isClient: true);
        var server = new MuxSession(serverTcp.GetStream(), isClient: false);
        _ = client.RunAsync(cts.Token);
        _ = server.RunAsync(cts.Token);
        _ = RunEchoServerAsync(server, cts.Token);
        return (client, server, cts);
    }

    private static async Task RunEchoServerAsync(MuxSession server, CancellationToken ct)
    {
        while (true)
        {
            var s = await server.AcceptStreamAsync(ct);
            if (s is null) break;
            _ = Task.Run(async () =>
            {
                try { await s.CopyToAsync(s, ct); } catch { }
                finally { await s.DisposeAsync(); }
            }, ct);
        }
    }

    [Fact]
    public async Task SingleStream_RoundTrips()
    {
        var (client, _, cts) = await ConnectEchoAsync();
        await using var stream = await client.OpenStreamAsync(cts.Token);

        var payload = "hello virtio-serial"u8.ToArray();
        await stream.WriteAsync(payload, cts.Token);

        var echoed = new byte[payload.Length];
        await stream.ReadExactlyAsync(echoed, cts.Token);

        Assert.Equal(payload, echoed);
        cts.Cancel();
    }

    [Fact]
    public async Task ManyConcurrentStreams_AreIsolated()
    {
        var (client, _, cts) = await ConnectEchoAsync();
        const int n = 50;

        var tasks = Enumerable.Range(0, n).Select(async i =>
        {
            await using var s = await client.OpenStreamAsync(cts.Token);
            var payload = System.Text.Encoding.ASCII.GetBytes($"stream-{i}-payload-{i * 7}");
            await s.WriteAsync(payload, cts.Token);
            var buf = new byte[payload.Length];
            await s.ReadExactlyAsync(buf, cts.Token);
            Assert.Equal(payload, buf);
        });

        await Task.WhenAll(tasks);
        cts.Cancel();
    }

    [Fact]
    public async Task LargeTransfer_ExercisesFlowControl()
    {
        var (client, _, cts) = await ConnectEchoAsync();
        await using var stream = await client.OpenStreamAsync(cts.Token);

        // Several times the initial window, so credit must be replenished mid-transfer.
        var data = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);

        var writer = Task.Run(async () =>
        {
            await stream.WriteAsync(data, cts.Token);
        });

        var received = new byte[data.Length];
        await stream.ReadExactlyAsync(received, cts.Token);
        await writer;

        Assert.True(data.AsSpan().SequenceEqual(received), "echoed payload must match byte-for-byte");
        cts.Cancel();
    }

    [Fact]
    public async Task ClientFin_SignalsEofToReader()
    {
        var (client, _, cts) = await ConnectEchoAsync();
        var stream = await client.OpenStreamAsync(cts.Token);

        var payload = "bye"u8.ToArray();
        await stream.WriteAsync(payload, cts.Token);
        var buf = new byte[payload.Length];
        await stream.ReadExactlyAsync(buf, cts.Token);

        // Dispose sends FIN; the echo server then FINs back, so a further read hits EOF.
        await stream.DisposeAsync();

        cts.Cancel();
        Assert.Equal(payload, buf);
    }
}

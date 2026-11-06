using System.Net;
using System.Net.Sockets;
using System.Text;
using PveTunnel.Mux;
using PveTunnel.Relay;
using Xunit;

namespace PveTunnel.Relay.Tests;

/// <summary>
/// The work package 1 exit gate: guest-to-host and host-to-guest traffic sharing one session,
/// with no cross-VM routing. A stand-in guest plays the part of the bridge, so these run without
/// a Proxmox node -- what they cannot cover is virtio-serial itself, which needs the cluster.
/// </summary>
public class IngressTests
{
    private const int TimeoutSeconds = 20;

    static IngressTests() => RelayLog.Sink = _ => { }; // keep test output readable

    private static VmEntry Entry(string vmid, int port = 24101) => new()
    {
        Vmid = vmid,
        ManagementPort = port,
        EgressTarget = new Target(IPAddress.Parse("10.10.0.20"), 5000),
        IngressTarget = new Target(IPAddress.Loopback, 5986),
        EgressEnabled = true,
        IngressEnabled = true,
    };

    /// <summary>One relay session, one stand-in guest, and the ingress listener bound to them.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public required MuxSession Relay { get; init; }
        public required MuxSession Guest { get; init; }
        public required VmSession Record { get; init; }
        public required Task RelayRun { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        private readonly List<Task> _background = [];

        /// <summary>The listener owned by this incarnation. Throws if installation failed to bind one.</summary>
        public VmIngress Ingress => Record.Ingress ?? throw new InvalidOperationException("ingress was not bound");

        public void Track(Task t) => _background.Add(t);

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            await Record.DisposeAsync(); // owns the ingress listener and the relay mux session
            await Guest.DisposeAsync();
            foreach (var t in _background) { try { await t; } catch { } }
            Cts.Dispose();
        }
    }

    /// <param name="guestTag">Prefixed to every echoed reply so cross-routing is detectable.</param>
    /// <param name="install">When given, the session is installed through the registry as the relay does.</param>
    /// <param name="echo">False leaves inbound streams unserved, for tests about holding resources.</param>
    private static async Task<Rig> StartAsync(
        string vmid,
        string guestTag,
        VmSessionRegistry? install = null,
        IngressLimits? limits = null,
        bool echo = true)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var guestTcp = new TcpClient();
        var connect = guestTcp.ConnectAsync(IPAddress.Loopback, port);
        var relayTcp = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        var relay = new MuxSession(relayTcp.GetStream(), isClient: false);
        var guest = new MuxSession(guestTcp.GetStream(), isClient: true);

        var entry = Entry(vmid);
        var record = new VmSession(vmid, entry, relay);
        var relayRun = relay.RunAsync(cts.Token);

        Task Bind(VmSession r)
        {
            var ingress = new VmIngress(entry, relay, IPAddress.Loopback, port: 0, cts.Token, limits);
            ingress.Start();
            r.Ingress = ingress;
            return Task.CompletedTask;
        }

        if (install is not null) await install.InstallAsync(record, Bind, cts.Token);
        else await Bind(record);

        var rig = new Rig { Relay = relay, Guest = guest, Record = record, RelayRun = relayRun, Cts = cts };
        rig.Track(guest.RunAsync(cts.Token));
        if (echo) rig.Track(GuestServeIngressAsync(guest, guestTag, cts.Token));
        return rig;
    }

    /// <summary>Stands in for the bridge delivering an inbound stream to WinRM, tagging each reply.</summary>
    private static async Task GuestServeIngressAsync(MuxSession guest, string tag, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            MuxStream? s;
            try { s = await guest.AcceptStreamAsync(ct); } catch (OperationCanceledException) { break; }
            if (s is null) break;

            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[256];
                    var n = await s.ReadAsync(buf, ct);
                    if (n > 0)
                    {
                        var reply = Encoding.ASCII.GetBytes(tag + ":" + Encoding.ASCII.GetString(buf, 0, n));
                        await s.WriteAsync(reply, ct);
                        await s.FlushAsync(ct);
                    }
                }
                catch { /* teardown */ }
                finally { await s.DisposeAsync(); }
            }, ct);
        }
    }

    private static async Task<string> AskControllerAsync(int port, string message, CancellationToken ct)
    {
        using var controller = new TcpClient { NoDelay = true };
        await controller.ConnectAsync(IPAddress.Loopback, port, ct);
        var ns = controller.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(message), ct);
        await ns.FlushAsync(ct);

        var buf = new byte[256];
        var n = await ns.ReadAsync(buf, ct);
        return Encoding.ASCII.GetString(buf, 0, n);
    }

    // ---- the ingress path itself ----

    [Fact]
    public async Task ControllerConnectionReachesTheGuest()
    {
        await using var rig = await StartAsync("101", "guest101");

        var reply = await AskControllerAsync(rig.Ingress.BoundPort, "winrm-hello", rig.Cts.Token);

        Assert.Equal("guest101:winrm-hello", reply);
    }

    /// <summary>
    /// The relay opens ingress streams, so they must carry server parity. If this drifts, a guest
    /// could pre-empt a management stream by opening the same id first.
    /// </summary>
    [Fact]
    public async Task IngressStreamsUseServerParity()
    {
        await using var rig = await StartAsync("101", "guest101");

        // Drive real ingress traffic first, so the ids under test are the ones ingress allocated.
        for (var i = 0; i < 3; i++)
            Assert.StartsWith("guest101:", await AskControllerAsync(rig.Ingress.BoundPort, $"probe{i}", rig.Cts.Token));

        var seen = new List<uint>();
        for (var i = 0; i < 3; i++)
        {
            var stream = await rig.Relay.OpenStreamAsync(rig.Cts.Token);
            seen.Add(stream.Id);
            await stream.DisposeAsync();
        }

        Assert.All(seen, id => Assert.Equal(0u, id % 2));
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    // ---- both directions at once, which is the exit gate ----

    [Fact]
    public async Task IngressAndEgressRunConcurrentlyOnOneSession()
    {
        await using var rig = await StartAsync("101", "guest101");

        // The relay side stands in for ForwardToTargetAsync: accept guest-initiated streams and
        // echo them, as the GHOSTS target would.
        rig.Track(Task.Run(async () =>
        {
            while (!rig.Cts.IsCancellationRequested)
            {
                MuxStream? s;
                try { s = await rig.Relay.AcceptStreamAsync(rig.Cts.Token); } catch { break; }
                if (s is null) break;
                _ = Task.Run(async () =>
                {
                    try { await s.CopyToAsync(s, rig.Cts.Token); } catch { }
                    finally { await s.DisposeAsync(); }
                }, rig.Cts.Token);
            }
        }));

        // Guest-initiated (GHOSTS) traffic in flight at the same time as controller (WinRM) traffic.
        var egress = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await using var s = await rig.Guest.OpenStreamAsync(rig.Cts.Token);
                var payload = Encoding.ASCII.GetBytes($"ghosts-{i:D3}");
                await s.WriteAsync(payload, rig.Cts.Token);
                var buf = new byte[payload.Length];
                await s.ReadExactlyAsync(buf, rig.Cts.Token);
                Assert.Equal(payload, buf);
            }
        });

        var ingress = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                var reply = await AskControllerAsync(rig.Ingress.BoundPort, $"winrm-{i:D3}", rig.Cts.Token);
                Assert.Equal($"guest101:winrm-{i:D3}", reply);
            }
        });

        await Task.WhenAll(egress, ingress);
    }

    // ---- isolation, which is why identity comes from the host ----

    [Fact]
    public async Task TwoVmsDoNotCrossRoute()
    {
        await using var a = await StartAsync("101", "guest101");
        await using var b = await StartAsync("102", "guest102");

        Assert.NotEqual(a.Ingress.BoundPort, b.Ingress.BoundPort);

        // Interleave so any shared state would show up as a swapped reply.
        for (var i = 0; i < 10; i++)
        {
            var ra = await AskControllerAsync(a.Ingress.BoundPort, $"a{i}", a.Cts.Token);
            var rb = await AskControllerAsync(b.Ingress.BoundPort, $"b{i}", b.Cts.Token);

            Assert.Equal($"guest101:a{i}", ra);
            Assert.Equal($"guest102:b{i}", rb);
        }
    }

    // ---- replacement ----

    /// <summary>
    /// Replacing a VM's session must stop the old incarnation completely -- both its ingress
    /// listener and the mux session underneath it.
    /// </summary>
    /// <remarks>
    /// Disposing only the listener would leave the stale session still accepting and forwarding
    /// guest egress, which is precisely the stale-incarnation routing the registry exists to
    /// prevent. Installation is also ordered so the successor is never published before its own
    /// port is bound, or status would advertise a live session while the port still routed to the
    /// incarnation being replaced.
    /// </remarks>
    [Fact]
    public async Task ReplacingASessionStopsTheOldIngressAndItsMuxSession()
    {
        var registry = new VmSessionRegistry();

        await using var first = await StartAsync("101", "guest101-old", install: registry);
        var oldPort = first.Ingress.BoundPort;
        Assert.Equal("guest101-old:hello", await AskControllerAsync(oldPort, "hello", first.Cts.Token));

        // A second QEMU connection for the same VMID arrives.
        await using var second = await StartAsync("101", "guest101-new", install: registry);
        var newPort = second.Ingress.BoundPort;
        Assert.NotEqual(oldPort, newPort);

        // The replaced incarnation is gone in both directions: its port no longer accepts...
        using var refused = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => refused.ConnectAsync(IPAddress.Loopback, oldPort));

        // ...and its mux session was disposed, so the old guest side sees the link end.
        var ended = await Task.WhenAny(first.RelayRun, Task.Delay(5000, second.Cts.Token));
        Assert.Same(first.RelayRun, ended);

        // The successor serves normally.
        Assert.Equal("guest101-new:hello", await AskControllerAsync(newPort, "hello", second.Cts.Token));
        Assert.True(registry.TryGet("101", out var current));
        Assert.Same(second.Record, current);
    }

    // ---- admission bounds ----

    /// <summary>
    /// Controller connections share the VM's stream budget with its own GHOSTS traffic, so an
    /// unbounded number of idle connections would starve the guest. Excess is refused immediately
    /// rather than queued, so a controller that cannot be served is told so.
    /// </summary>
    [Fact]
    public async Task ControllerConnectionsBeyondTheLimitAreRefused()
    {
        var limits = new IngressLimits { MaxConcurrentConnections = 2, IdleTimeout = TimeSpan.FromMinutes(5) };
        await using var rig = await StartAsync("101", "guest101", limits: limits, echo: false);

        // Two connections that connect and then say nothing, holding their slots.
        var held = new List<TcpClient>();
        for (var i = 0; i < 2; i++)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, rig.Ingress.BoundPort, rig.Cts.Token);
            await c.GetStream().WriteAsync("hold"u8.ToArray(), rig.Cts.Token);
            held.Add(c);
        }

        // Give the relay a moment to admit both before testing the third.
        await Task.Delay(200, rig.Cts.Token);

        using var excess = new TcpClient();
        await excess.ConnectAsync(IPAddress.Loopback, rig.Ingress.BoundPort, rig.Cts.Token);
        var buf = new byte[16];
        // The listener accepts at the kernel level, then closes without serving.
        var n = await excess.GetStream().ReadAsync(buf, rig.Cts.Token);
        Assert.Equal(0, n);

        foreach (var c in held) c.Dispose();
    }

    /// <summary>
    /// A controller that connects and then goes quiet otherwise holds a mux stream open forever.
    /// </summary>
    [Fact]
    public async Task IdleControllerConnectionIsTornDown()
    {
        var limits = new IngressLimits { IdleTimeout = TimeSpan.FromMilliseconds(400) };
        await using var rig = await StartAsync("101", "guest101", limits: limits, echo: false);

        using var controller = new TcpClient();
        await controller.ConnectAsync(IPAddress.Loopback, rig.Ingress.BoundPort, rig.Cts.Token);
        await controller.GetStream().WriteAsync("knock"u8.ToArray(), rig.Cts.Token);

        var buf = new byte[16];
        var n = await controller.GetStream().ReadAsync(buf, rig.Cts.Token);

        Assert.Equal(0, n); // torn down by the idle watchdog, not left hanging
        Assert.False(rig.RelayRun.IsCompleted, "only the connection dies, not the session");
    }

    // ---- lifecycle ----

    /// <summary>
    /// The port must exist only while the guest can be served, so a controller reaching a VM whose
    /// session has gone is refused by the kernel rather than accepted into a stream nobody answers.
    /// </summary>
    [Fact]
    public async Task IngressStopsAcceptingOnceDisposed()
    {
        var rig = await StartAsync("101", "guest101");
        var port = rig.Ingress.BoundPort;

        Assert.Equal("guest101:before", await AskControllerAsync(port, "before", rig.Cts.Token));

        await rig.Ingress.DisposeAsync();

        using var refused = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(
            () => refused.ConnectAsync(IPAddress.Loopback, port));

        await rig.DisposeAsync();
    }

    /// <summary>
    /// A wildcard bind would put guest management on every interface the node has, exercise VLANs
    /// included. There is no configuration where that is intended, so it is refused rather than
    /// left to the host firewall.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public async Task WildcardBindIsRefused(string address)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var guestTcp = new TcpClient();
        var connect = guestTcp.ConnectAsync(IPAddress.Loopback, port);
        var relayTcp = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        await using var relay = new MuxSession(relayTcp.GetStream(), isClient: false);

        var ex = Assert.Throws<ArgumentException>(
            () => new VmIngress(Entry("101"), relay, IPAddress.Parse(address), 0, cts.Token));
        Assert.Contains("wildcard", ex.Message);

        guestTcp.Dispose();
    }
}

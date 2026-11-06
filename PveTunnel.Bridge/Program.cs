using System.Net;
using System.Net.Sockets;
using PveTunnel.Bridge;
using PveTunnel.Mux;

// ---------------------------------------------------------------------------
// pve-tunnel-bridge (runs inside a guest, Windows or Linux)
//
// EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM
//
// Carries two directions over the single virtio-serial port to the per-node relay:
//
//   egress  a local application connects to the loopback listener -> stream to the relay,
//           which dials the host-side target (GHOSTS API)
//   ingress the relay opens a stream -> the bridge connects it to the guest management
//           target (WinRM on 127.0.0.1:5986), so Ansible reaches the guest with no NIC
//
// The ingress target is read from local configuration and never from the stream. A compromised
// relay cannot ask this bridge to connect anywhere else, and nothing on the wire names a target.
//
// Config via environment variables:
//   PVE_TUNNEL_LISTEN          loopback endpoint to listen on   (default 127.0.0.1:5000)
//   PVE_TUNNEL_INGRESS_TARGET  guest management target          (default 127.0.0.1:5986)
//   PVE_TUNNEL_INGRESS_ENABLED set to 0/false to refuse inbound streams (default enabled)
//   PVE_TUNNEL_PORT_NAME       virtio-serial port name          (default pve-tunnel.relay.0)
//   PVE_TUNNEL_PORT_PATH       full device path override        (default per-OS)
// ---------------------------------------------------------------------------

static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
static void Log(string m) => Console.WriteLine(
    $"{DateTime.UtcNow:O} exercise=true scope=out-of-scope component=bridge {m}");

static bool EnvFlag(string k, bool d) =>
    Environment.GetEnvironmentVariable(k) is { Length: > 0 } v
        ? v is not ("0" or "false" or "False" or "no" or "off")
        : d;

var listen = Env("PVE_TUNNEL_LISTEN", "127.0.0.1:5000");
var ingressTargetRaw = Env("PVE_TUNNEL_INGRESS_TARGET", "127.0.0.1:5986");
var ingressEnabled = EnvFlag("PVE_TUNNEL_INGRESS_ENABLED", true);
var portName = Env("PVE_TUNNEL_PORT_NAME", "pve-tunnel.relay.0");
var portPath = Env("PVE_TUNNEL_PORT_PATH", SerialTransport.DefaultPortPath(portName));

var (host, port) = ParseEndpoint(listen);
var endpoint = new IPEndPoint(IPAddress.Parse(host), port);

var (ingressHost, ingressPort) = ParseEndpoint(ingressTargetRaw);
if (!IPAddress.TryParse(ingressHost, out var ingressAddress))
{
    Log($"event=startup_failed reason=\"PVE_TUNNEL_INGRESS_TARGET host {ingressHost} is not a literal IP address\"");
    return 2;
}

// Inbound streams terminate at a guest-local service and nowhere else. This is the part of the
// guest-side target that a stock bridge can actually enforce; it does not constrain a bridge
// binary that has been replaced outright, which is a guest-isolation problem rather than one the
// tunnel can solve.
if (!IPAddress.IsLoopback(ingressAddress))
{
    Log($"event=startup_failed reason=\"PVE_TUNNEL_INGRESS_TARGET {ingressAddress} is not a loopback address\"");
    return 2;
}

using var appCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; appCts.Cancel(); };

// The current live mux session, swapped by the supervisor as the serial link drops/reconnects.
MuxSession? current = null;

var listener = new TcpListener(endpoint);
listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
listener.Start();
Log($"event=startup listen={endpoint} port_path={portPath} ingress_enabled={ingressEnabled} ingress_target={ingressAddress}:{ingressPort}");

var supervisor = Task.Run(() => SuperviseSerialAsync(appCts.Token));
var accepter = Task.Run(() => AcceptLoopAsync(appCts.Token));

try { await Task.WhenAll(supervisor, accepter); }
catch (OperationCanceledException) { }
listener.Stop();
return 0;

// --- maintain the serial mux session, reconnecting forever ---
async Task SuperviseSerialAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        MuxSession? session = null;
        try
        {
            var serial = SerialTransport.Open(portPath);
            session = new MuxSession(serial, isClient: true);
            Interlocked.Exchange(ref current, session);
            Log("event=link_up");

            // Serve relay-initiated streams for as long as this link lasts. The mux read loop and
            // the ingress accept loop end together, so a dropped link tears both down.
            var ingress = ServeIngressAsync(session, ct);
            await session.RunAsync(ct).ConfigureAwait(false); // returns when the link drops
            await ingress.ConfigureAwait(false);
            Log("event=link_down");
        }
        catch (OperationCanceledException) { }
        catch (MuxTransportException ex) { Log($"event=transport_fault error=\"{ex.Message}\""); }
        catch (MuxProtocolException ex) { Log($"event=protocol_violation error=\"{ex.Message}\""); }
        catch (Exception ex) { Log($"event=serial_error error=\"{ex.Message}\""); }
        finally
        {
            Interlocked.CompareExchange(ref current, null, session);
            if (session is not null) await session.DisposeAsync();
        }
        if (!ct.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }
}

// --- inbound: relay opened a stream, connect it to the guest management target ---
async Task ServeIngressAsync(MuxSession session, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        MuxStream? stream;
        try { stream = await session.AcceptStreamAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { break; }
        if (stream is null) break; // session ended

        if (!ingressEnabled)
        {
            Log($"event=ingress_refused stream={stream.Id} reason=disabled");
            await stream.DisposeAsync().ConfigureAwait(false);
            continue;
        }
        _ = ForwardToGuestTargetAsync(stream, ct);
    }
}

async Task ForwardToGuestTargetAsync(MuxStream stream, CancellationToken ct)
{
    using var client = new TcpClient { NoDelay = true };
    try
    {
        Log($"event=ingress_open stream={stream.Id} target={ingressAddress}:{ingressPort}");
        await client.ConnectAsync(ingressAddress, ingressPort, ct).ConfigureAwait(false);
        var counts = await PumpHelper.BidirectionalAsync(client.GetStream(), stream, ct).ConfigureAwait(false);
        Log($"event=ingress_close stream={stream.Id} bytes_to_target={counts.AToB} bytes_from_target={counts.BToA}");
    }
    catch (Exception ex)
    {
        // The usual cause is WinRM not listening yet on a guest that is still coming up.
        Log($"event=ingress_error stream={stream.Id} error=\"{ex.Message}\"");
    }
    finally { await stream.DisposeAsync().ConfigureAwait(false); }
}

// --- egress: accept loopback TCP connections and hand each to the current session ---
async Task AcceptLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { break; }
        catch (ObjectDisposedException) { break; }

        var session = Volatile.Read(ref current);
        if (session is null)
        {
            Log("event=egress_dropped reason=\"serial link not ready\"");
            client.Dispose();
            continue;
        }
        _ = HandleClientAsync(client, session, ct);
    }
}

async Task HandleClientAsync(TcpClient client, MuxSession session, CancellationToken ct)
{
    client.NoDelay = true;
    var tcp = client.GetStream();
    MuxStream? mux = null;
    try
    {
        mux = await session.OpenStreamAsync(ct).ConfigureAwait(false);
        Log($"event=egress_open stream={mux.Id}");
        var counts = await PumpHelper.BidirectionalAsync(tcp, mux, ct).ConfigureAwait(false);
        Log($"event=egress_close stream={mux.Id} bytes_to_relay={counts.AToB} bytes_from_relay={counts.BToA}");
    }
    catch (Exception ex) { Log($"event=egress_error error=\"{ex.Message}\""); }
    finally
    {
        if (mux is not null) await mux.DisposeAsync();
        client.Dispose();
    }
}

static (string host, int port) ParseEndpoint(string s)
{
    var i = s.LastIndexOf(':');
    return i < 0 ? (s, 5000) : (s[..i], int.Parse(s[(i + 1)..]));
}

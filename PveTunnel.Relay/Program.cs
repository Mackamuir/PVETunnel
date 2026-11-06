using System.Net;
using System.Net.Sockets;
using PveTunnel.Mux;
using PveTunnel.Relay;

// ---------------------------------------------------------------------------
// pve-tunnel-relay (runs on each Proxmox VE node)
//
// EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM
//
// Listens on a Unix domain socket that the VMs' QEMU virtio-serial chardevs connect to
// (server=off,reconnect=5). Each connection is one VM's mux session, carrying traffic both ways:
//
//   egress  guest opens a stream -> relay dials that VM's configured target (GHOSTS API)
//   ingress controller connects to the VM's allocated port -> relay opens a stream ->
//           guest bridge delivers it to its own configured target (WinRM on guest loopback 5986)
//
// VMID is derived host-side from SO_PEERCRED and the QEMU command line. Nothing a guest sends
// influences identity or routing, and no target address ever travels on the wire.
//
// Config via environment variables:
//   PVE_TUNNEL_SOCKET          listening Unix socket path  (default /run/pve-tunnel/relay.sock)
//   PVE_TUNNEL_REGISTRY        VM registry JSON            (default /etc/pve/pve-tunnel/vms.json)
//   PVE_TUNNEL_TARGET          fallback egress target, single-target mode only
//   PVE_TUNNEL_ALLOW_NO_REGISTRY set to 1 to run without a registry (single-target mode)
//   PVE_TUNNEL_INGRESS_BIND    controller-facing bind addr (default 127.0.0.1)
//   PVE_TUNNEL_CONTROL_SOCKET  root-only status socket     (default /run/pve-tunnel/control.sock)
// ---------------------------------------------------------------------------

const string RelayVersion = "0.2.0";

static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;

var socketPath = Env("PVE_TUNNEL_SOCKET", "/run/pve-tunnel/relay.sock");
var registryPath = Env("PVE_TUNNEL_REGISTRY", "/etc/pve/pve-tunnel/vms.json");
var fallbackTarget = Env("PVE_TUNNEL_TARGET", "127.0.0.1:5000");
var controlSocketPath = Env("PVE_TUNNEL_CONTROL_SOCKET", "/run/pve-tunnel/control.sock");
var allowNoRegistry = Environment.GetEnvironmentVariable("PVE_TUNNEL_ALLOW_NO_REGISTRY")
    is "1" or "true" or "True" or "yes" or "on";

// Idle connections hold a mux stream that the VM's own traffic then cannot have.
var idleTimeout = TimeSpan.FromMinutes(5);

if (!IPAddress.TryParse(Env("PVE_TUNNEL_INGRESS_BIND", "127.0.0.1"), out var ingressBind))
{
    RelayLog.Write("startup_failed", ("reason", "PVE_TUNNEL_INGRESS_BIND is not a literal IP address"));
    return 2;
}

// The registry is required. A missing one is far more likely to be a typo, an unmounted
// /etc/pve, or a failed deploy than a deliberate choice, and silently falling back would turn
// authoritative per-VM policy into a single global egress target without anyone being told.
// Single-target mode is still available, but only when asked for explicitly.
IReadOnlyDictionary<string, VmEntry> registry;
bool registryAuthoritative;
try
{
    if (File.Exists(registryPath))
    {
        registry = VmRegistry.Load(registryPath);
        registryAuthoritative = true;
    }
    else if (allowNoRegistry)
    {
        registry = new Dictionary<string, VmEntry>();
        registryAuthoritative = false;
        RelayLog.Write("registry_absent_by_request",
            ("path", registryPath), ("fallback_target", fallbackTarget), ("ingress", false));
    }
    else
    {
        RelayLog.Write("startup_failed",
            ("reason", "VM registry not found; set PVE_TUNNEL_ALLOW_NO_REGISTRY=1 for single-target mode"),
            ("registry", registryPath));
        return 2;
    }
}
catch (VmRegistryException ex)
{
    // Fail closed. A registry we cannot validate could route one VM's management traffic to
    // another VM's guest, which is worse than not starting.
    RelayLog.Write("startup_failed", ("reason", ex.Message), ("registry", registryPath));
    return 2;
}

Target? fallback = null;
if (!registryAuthoritative)
{
    try { fallback = ParseFallback(fallbackTarget); }
    catch (Exception ex)
    {
        RelayLog.Write("startup_failed", ("reason", $"PVE_TUNNEL_TARGET invalid: {ex.Message}"));
        return 2;
    }
}

var sessions = new VmSessionRegistry();

using var appCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; appCts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => appCts.Cancel();

// Bind and harden before anything is served. A control socket that could not be restricted to
// its owner is not a root-only surface, and continuing would quietly publish status wherever the
// umask allowed.
Socket controlListener;
try
{
    controlListener = ControlServer.Bind(controlSocketPath);
}
catch (Exception ex)
{
    RelayLog.Write("startup_failed",
        ("reason", $"control socket could not be bound owner-only: {ex.Message}"),
        ("path", controlSocketPath));
    return 2;
}

var control = ControlServer.ServeAsync(
    controlListener,
    controlSocketPath,
    () => ControlServer.BuildStatusJson(
        RelayVersion,
        sessions.Snapshot(registry),
        socketPath,
        registryAuthoritative ? registryPath : null),
    appCts.Token);

var dir = Path.GetDirectoryName(socketPath);
if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
if (File.Exists(socketPath)) File.Delete(socketPath);

var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
listener.Bind(new UnixDomainSocketEndPoint(socketPath));
listener.Listen(256);

RelayLog.Write("startup",
    ("notice", RelayLog.ExerciseNotice),
    ("version", RelayVersion),
    ("socket", socketPath),
    ("registry", registryAuthoritative ? registryPath : "none"),
    ("configured_vms", registry.Count),
    ("ingress_bind", ingressBind));

try
{
    while (!appCts.IsCancellationRequested)
    {
        var conn = await listener.AcceptAsync(appCts.Token).ConfigureAwait(false);
        _ = HandleVmSessionLoggedAsync(conn, appCts.Token);
    }
}
catch (OperationCanceledException) { }
finally
{
    listener.Dispose();
    try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { /* ignore */ }
    try { await control.ConfigureAwait(false); } catch { /* teardown */ }
    RelayLog.Write("shutdown");
}
return 0;

// The accept loop does not await sessions, so anything a session throws would otherwise vanish
// and leave QEMU connected to a socket nobody reads.
async Task HandleVmSessionLoggedAsync(Socket conn, CancellationToken ct)
{
    try { await HandleVmSessionAsync(conn, ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        RelayLog.Write("session_failed", ("error", ex.GetType().Name + ": " + ex.Message));
        conn.Dispose();
    }
}

// One mux session per VM, per QEMU virtio-serial connection.
async Task HandleVmSessionAsync(Socket conn, CancellationToken ct)
{
    // No NoDelay here: this is a Unix socket, and setting TCP_NODELAY on one fails with
    // EOPNOTSUPP, which killed every session before it started.
    var vmid = PeerCred.ResolveVmid(PeerCred.GetPeerPid(conn));

    registry.TryGetValue(vmid, out var entry);
    if (entry is null && registryAuthoritative)
    {
        // Fail closed on ambiguous identity: no entry means no allocated port and no known target.
        RelayLog.Write("session_refused", ("vmid", vmid), ("reason", "no registry entry"));
        conn.Dispose();
        return;
    }

    entry ??= new VmEntry
    {
        Vmid = vmid,
        ManagementPort = 0,
        EgressTarget = fallback!.Value,
        IngressTarget = fallback!.Value,
        EgressEnabled = true,
        IngressEnabled = false,
    };

    var transport = new NetworkStream(conn, ownsSocket: true);
    var session = new MuxSession(transport, isClient: false);
    var record = new VmSession(vmid, entry, session);
    var run = session.RunAsync(ct);

    // Installing is serialised per VMID and ordered so two incarnations are never both usable:
    // any previous owner is torn down and awaited, then this one binds its port, and only then is
    // it published. The ingress listener therefore exists only while this session is live.
    var replaced = await sessions.InstallAsync(record, async r =>
    {
        if (!entry.IngressEnabled) return;
        try
        {
            var ingress = new VmIngress(entry, session, ingressBind, entry.ManagementPort, ct);
            ingress.Start();
            r.Ingress = ingress;
        }
        catch (Exception ex)
        {
            RelayLog.Write("ingress_bind_failed",
                ("vmid", vmid), ("port", entry.ManagementPort), ("error", ex.Message));
        }
        await Task.CompletedTask;
    }, ct).ConfigureAwait(false);

    if (replaced)
        RelayLog.Write("session_replaced", ("vmid", vmid), ("reason", "a newer QEMU connection arrived for this VMID"));

    RelayLog.Write("session_up",
        ("vmid", vmid),
        ("egress", entry.EgressEnabled),
        ("ingress", entry.IngressEnabled),
        ("ingress_port", record.Ingress?.BoundPort),
        ("egress_target", entry.EgressTarget));

    try
    {
        while (!ct.IsCancellationRequested)
        {
            var stream = await session.AcceptStreamAsync(ct).ConfigureAwait(false);
            if (stream is null) break; // session ended
            if (!entry.EgressEnabled)
            {
                RelayLog.Write("stream_refused",
                    ("vmid", vmid), ("direction", "egress"), ("stream", stream.Id), ("reason", "egress disabled"));
                await stream.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            _ = ForwardToTargetAsync(stream, entry, ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (MuxTransportException ex)
    {
        // Distinct from a peer violation: this is our own link failing mid-frame. How often it
        // fires on real virtio-serial hardware under load is not yet measured, and this record is
        // how the cluster acceptance run will find out.
        RelayLog.Write("session_transport_fault", ("vmid", vmid), ("error", ex.Message));
    }
    catch (MuxProtocolException ex)
    {
        // One guest corrupting its own session must not disturb the node or another VM.
        RelayLog.Write("session_protocol_violation", ("vmid", vmid), ("error", ex.Message));
    }
    finally
    {
        sessions.Unregister(record);
        await record.DisposeAsync().ConfigureAwait(false); // owns both the ingress listener and the mux session
        try { await run.ConfigureAwait(false); }
        catch (MuxTransportException ex) { RelayLog.Write("session_transport_fault", ("vmid", vmid), ("error", ex.Message)); }
        catch (MuxProtocolException ex) { RelayLog.Write("session_protocol_violation", ("vmid", vmid), ("error", ex.Message)); }
        catch { /* transport teardown */ }
        RelayLog.Write("session_down", ("vmid", vmid));
    }
}

// One TCP connection to the VM's configured target per guest-initiated stream.
async Task ForwardToTargetAsync(MuxStream stream, VmEntry entry, CancellationToken ct)
{
    using var client = new TcpClient { NoDelay = true };
    try
    {
        RelayLog.Write("stream_open",
            ("vmid", entry.Vmid), ("direction", "egress"), ("stream", stream.Id), ("target", entry.EgressTarget));

        await client.ConnectAsync(entry.EgressTarget.Address, entry.EgressTarget.Port, ct).ConfigureAwait(false);
        var counts = await PumpHelper
            .BidirectionalAsync(client.GetStream(), stream, ct, idleTimeout)
            .ConfigureAwait(false);

        RelayLog.Write("stream_close",
            ("vmid", entry.Vmid), ("direction", "egress"), ("stream", stream.Id),
            ("bytes_to_target", counts.AToB), ("bytes_from_target", counts.BToA));
    }
    catch (Exception ex)
    {
        RelayLog.Write("stream_error",
            ("vmid", entry.Vmid), ("direction", "egress"), ("stream", stream.Id), ("error", ex.Message));
    }
    finally { await stream.DisposeAsync().ConfigureAwait(false); }
}

static Target ParseFallback(string s)
{
    var i = s.LastIndexOf(':');
    var host = i < 0 ? s : s[..i];
    var port = i < 0 ? 5000 : int.Parse(s[(i + 1)..]);
    if (!IPAddress.TryParse(host, out var address))
        throw new FormatException($"host {host} is not a literal IP address");
    return new Target(address, port);
}

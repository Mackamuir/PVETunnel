using System.Net;
using System.Net.Sockets;
using PveTunnel.Mux;

namespace PveTunnel.Relay;

/// <summary>
/// Bounds on what a controller can consume on one VM's ingress port.
/// </summary>
/// <remarks>
/// Every stream a controller holds is a stream the guest's own GHOSTS traffic cannot have, since
/// both share one mux session. Without a cap, connections that merely sit there will exhaust
/// <see cref="MuxOptions.MaxConcurrentStreams"/> and starve the VM in both directions.
/// </remarks>
public sealed record IngressLimits
{
    /// <summary>Controller connections served at once for one VM. Deliberately well under the session stream cap.</summary>
    public int MaxConcurrentConnections { get; init; } = 32;

    /// <summary>How long to wait for a mux stream before giving the controller up as unservable.</summary>
    public TimeSpan StreamOpenTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Torn down once no byte has moved either way for this long.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public static IngressLimits Default { get; } = new();
}

/// <summary>
/// The controller-facing listener for one VM. Each accepted connection becomes a
/// relay-initiated mux stream that the guest bridge delivers to its configured management
/// target, normally WinRM on guest loopback 5986.
/// </summary>
/// <remarks>
/// The listener exists only while that VM's mux session is live. It is created when the session
/// is installed and disposed when the session is replaced or drops, so a controller connecting to
/// a port whose guest has gone away is refused by the kernel rather than accepted into a stream
/// that can never be served.
///
/// Nothing here reads a destination from the wire. The relay only opens a stream; the bridge
/// decides where it terminates from its own local configuration.
/// </remarks>
public sealed class VmIngress : IAsyncDisposable
{
    private readonly VmEntry _entry;
    private readonly MuxSession _session;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly IngressLimits _limits;
    private readonly SemaphoreSlim _admission;
    private readonly HashSet<Task> _inFlight = [];
    private readonly object _inFlightGate = new();
    private Task _acceptLoop = Task.CompletedTask;
    private int _disposed;

    /// <summary>The port actually bound. Equals the registry allocation unless port 0 was requested.</summary>
    public int BoundPort { get; }

    public string Vmid => _entry.Vmid;

    /// <param name="port">
    /// Normally <see cref="VmEntry.ManagementPort"/>. Tests pass 0 to take an ephemeral port.
    /// </param>
    public VmIngress(
        VmEntry entry,
        MuxSession session,
        IPAddress bindAddress,
        int port,
        CancellationToken ct,
        IngressLimits? limits = null)
    {
        // A wildcard bind would expose guest management on every interface the node has, including
        // exercise VLANs and WireGuard client networks. There is no configuration in which that is
        // what was wanted, so refuse it rather than trusting the host firewall to compensate.
        if (bindAddress.Equals(IPAddress.Any) || bindAddress.Equals(IPAddress.IPv6Any))
            throw new ArgumentException(
                $"refusing to bind VM {entry.Vmid} ingress to the wildcard address {bindAddress}; " +
                "bind loopback on a single node, or the node management address in a cluster",
                nameof(bindAddress));

        _entry = entry;
        _session = session;
        _limits = limits ?? IngressLimits.Default;
        _admission = new SemaphoreSlim(_limits.MaxConcurrentConnections, _limits.MaxConcurrentConnections);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(bindAddress, port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start() => _acceptLoop = AcceptLoopAsync(_cts.Token);

    /// <summary>Completes when the accept loop has stopped.</summary>
    public Task Completion => _acceptLoop;

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        RelayLog.Write("ingress_listening",
            ("vmid", _entry.Vmid),
            ("port", BoundPort),
            ("endpoint", _listener.LocalEndpoint),
            ("max_connections", _limits.MaxConcurrentConnections));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient controller;
                try { controller = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex)
                {
                    RelayLog.Write("ingress_accept_error", ("vmid", _entry.Vmid), ("error", ex.Message));
                    continue;
                }

                // Refuse immediately rather than queueing. A controller that cannot be served now
                // is better told so than left holding a connection that may never get a stream.
                if (!_admission.Wait(0, CancellationToken.None))
                {
                    RelayLog.Write("ingress_rejected",
                        ("vmid", _entry.Vmid),
                        ("peer", controller.Client.RemoteEndPoint?.ToString()),
                        ("reason", "at MaxConcurrentConnections"),
                        ("limit", _limits.MaxConcurrentConnections));
                    controller.Dispose();
                    continue;
                }

                Track(ServeControllerAsync(controller, ct));
            }
        }
        finally
        {
            RelayLog.Write("ingress_stopped", ("vmid", _entry.Vmid), ("port", BoundPort));
        }
    }

    private void Track(Task task)
    {
        lock (_inFlightGate) _inFlight.Add(task);
        _ = task.ContinueWith(
            t => { lock (_inFlightGate) _inFlight.Remove(t); },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ServeControllerAsync(TcpClient controller, CancellationToken ct)
    {
        controller.NoDelay = true;
        var remote = controller.Client.RemoteEndPoint?.ToString() ?? "unknown";
        MuxStream? stream = null;
        try
        {
            using var open = CancellationTokenSource.CreateLinkedTokenSource(ct);
            open.CancelAfter(_limits.StreamOpenTimeout);
            stream = await _session.OpenStreamAsync(open.Token).ConfigureAwait(false);

            RelayLog.Write("stream_open",
                ("vmid", _entry.Vmid),
                ("direction", "ingress"),
                ("stream", stream.Id),
                ("peer", remote),
                ("guest_target", _entry.IngressTarget));

            var counts = await PumpHelper
                .BidirectionalAsync(controller.GetStream(), stream, ct, _limits.IdleTimeout)
                .ConfigureAwait(false);

            RelayLog.Write("stream_close",
                ("vmid", _entry.Vmid),
                ("direction", "ingress"),
                ("stream", stream.Id),
                ("bytes_to_guest", counts.AToB),
                ("bytes_from_guest", counts.BToA),
                ("idle_timeout", counts.TimedOut ? true : null));
        }
        catch (Exception ex)
        {
            // The usual cause is the guest session dropping between accept and stream open.
            RelayLog.Write("stream_error",
                ("vmid", _entry.Vmid),
                ("direction", "ingress"),
                ("stream", stream?.Id),
                ("peer", remote),
                ("error", ex.Message));
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            controller.Dispose();
            _admission.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _listener.Stop(); } catch { /* ignore */ }
        try { await _acceptLoop.ConfigureAwait(false); } catch { /* teardown */ }

        // Await the connections themselves, or disposal would return while streams were still
        // being served on a session the caller is about to tear down.
        Task[] pending;
        lock (_inFlightGate) pending = [.. _inFlight];
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { /* teardown */ }

        _cts.Dispose();
        _admission.Dispose();
    }
}

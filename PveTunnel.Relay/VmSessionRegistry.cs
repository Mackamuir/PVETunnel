using System.Collections.Concurrent;
using PveTunnel.Mux;

namespace PveTunnel.Relay;

/// <summary>
/// One VM's live incarnation: its mux session, its ingress listener, and the lifetime that ends
/// both. The record owns them, so tearing down the record tears down everything routing for that
/// VM.
/// </summary>
/// <remarks>
/// Ownership matters because a stale incarnation that keeps serving is exactly the cross-routing
/// the registry exists to prevent. Disposing only the ingress listener would leave the old mux
/// session still accepting and forwarding guest egress.
/// </remarks>
public sealed class VmSession(string vmid, VmEntry? entry, MuxSession? mux = null) : IAsyncDisposable
{
    private int _disposed;

    public string Vmid { get; } = vmid;

    /// <summary>The registry entry for this VMID, or null when the VM is not in the registry.</summary>
    public VmEntry? Entry { get; } = entry;

    /// <summary>The mux session this incarnation owns. Null only in tests that exercise bookkeeping alone.</summary>
    public MuxSession? Mux { get; } = mux;

    public DateTime SinceUtc { get; } = DateTime.UtcNow;

    /// <summary>Set while installing, before the record is published. Null when ingress is disabled.</summary>
    public VmIngress? Ingress { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Ingress first: stop admitting controllers before the session underneath them goes away.
        if (Ingress is not null) await Ingress.DisposeAsync().ConfigureAwait(false);
        if (Mux is not null) await Mux.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Point-in-time view of one VM, for health and readiness queries.</summary>
public sealed record VmStatus(
    string Vmid,
    bool SessionLive,
    DateTime? SinceUtc,
    int? IngressPort,
    bool IngressEnabled,
    bool EgressEnabled,
    string? EgressTarget,
    string? IngressTarget);

/// <summary>
/// Which VMIDs currently have a usable mux session on this node.
/// </summary>
/// <remarks>
/// VMID comes from <see cref="PeerCred"/> on the host side, never from anything the guest sends,
/// so an entry here means QEMU for that VM is genuinely connected to this relay socket.
/// </remarks>
public sealed class VmSessionRegistry
{
    private readonly ConcurrentDictionary<string, VmSession> _live = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Installs a new incarnation for a VMID, replacing any previous one.
    /// </summary>
    /// <remarks>
    /// Serialised per VMID, and ordered so no window exists in which two incarnations are both
    /// usable: the previous owner is disposed and awaited first, then <paramref name="prepare"/>
    /// binds the successor's ingress, and only then is the successor published. Publishing before
    /// binding would let status report a live session with no ingress while the port was still
    /// routed by the incarnation being replaced.
    ///
    /// A VM has exactly one virtio-serial chardev, so a second host-side connection for the same
    /// VMID means the earlier one is finished -- typically after an abrupt QEMU restart the relay
    /// has not yet noticed. That assumption is safe only because the previous session is torn down
    /// here rather than assumed dead.
    /// </remarks>
    /// <returns>True if an earlier incarnation was replaced.</returns>
    public async Task<bool> InstallAsync(VmSession session, Func<VmSession, Task> prepare, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(session.Vmid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var replaced = false;
            if (_live.TryRemove(session.Vmid, out var previous))
            {
                await previous.DisposeAsync().ConfigureAwait(false);
                replaced = true;
            }

            await prepare(session).ConfigureAwait(false);
            _live[session.Vmid] = session;
            return replaced;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Removes a session only if it is still the registered one, so a late teardown of a replaced
    /// incarnation cannot evict its successor.
    /// </summary>
    public bool Unregister(VmSession session)
        => ((ICollection<KeyValuePair<string, VmSession>>)_live)
            .Remove(new KeyValuePair<string, VmSession>(session.Vmid, session));

    public bool TryGet(string vmid, out VmSession session) => _live.TryGetValue(vmid, out session!);

    public int Count => _live.Count;

    /// <summary>
    /// Status for every VM the registry knows about, whether or not its session is live, so an
    /// operator can tell "configured but down" from "not configured at all".
    /// </summary>
    public IReadOnlyList<VmStatus> Snapshot(IReadOnlyDictionary<string, VmEntry> configured)
    {
        var result = new List<VmStatus>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (vmid, entry) in configured)
        {
            seen.Add(vmid);
            _live.TryGetValue(vmid, out var live);
            result.Add(new VmStatus(
                vmid,
                live is not null,
                live?.SinceUtc,
                live?.Ingress?.BoundPort,
                entry.IngressEnabled,
                entry.EgressEnabled,
                entry.EgressTarget.ToString(),
                entry.IngressTarget.ToString()));
        }

        // A connected VM with no registry entry is worth surfacing: it is the shape of both a
        // missing allocation and a VM that was removed from the registry while still running.
        foreach (var (vmid, live) in _live)
        {
            if (seen.Contains(vmid)) continue;
            result.Add(new VmStatus(vmid, true, live.SinceUtc, null, false, false, null, null));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Vmid, b.Vmid));
        return result;
    }
}

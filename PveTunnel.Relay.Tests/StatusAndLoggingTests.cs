using System.Net;
using System.Text.Json;
using PveTunnel.Relay;
using Xunit;

namespace PveTunnel.Relay.Tests;

/// <summary>
/// Status output and log records are the operator's only view of a transport that deliberately
/// bypasses the guest NIC. The EXERCISE / OUT OF SCOPE markers on both are a checklist
/// requirement, not decoration: a defender who greps node logs must be able to tell instantly
/// that this traffic is range infrastructure.
/// </summary>
public class StatusAndLoggingTests
{
    private static VmEntry Entry(string vmid, int port, bool ingress) => new()
    {
        Vmid = vmid,
        ManagementPort = port,
        EgressTarget = new Target(IPAddress.Parse("10.10.0.20"), 5000),
        IngressTarget = new Target(IPAddress.Loopback, 5986),
        EgressEnabled = true,
        IngressEnabled = ingress,
    };

    // ---- structured logging ----

    private static string Capture(Action act)
    {
        var previous = RelayLog.Sink;
        var lines = new List<string>();
        RelayLog.Sink = lines.Add;
        try { act(); } finally { RelayLog.Sink = previous; }
        return string.Join("\n", lines);
    }

    [Fact]
    public void EveryLogRecordCarriesTheExerciseMarkers()
    {
        var line = Capture(() => RelayLog.Write("session_up", ("vmid", "101")));

        Assert.Contains("exercise=true", line);
        Assert.Contains("scope=out-of-scope", line);
        Assert.Contains("component=relay", line);
        Assert.Contains("event=session_up", line);
        Assert.Contains("vmid=101", line);
    }

    /// <summary>
    /// VMIDs come from the host, but targets and error strings can contain arbitrary text. A value
    /// with a space in it must not be able to forge an extra field in a log parser.
    /// </summary>
    [Fact]
    public void ValuesContainingSpacesOrQuotesAreEscaped()
    {
        var line = Capture(() => RelayLog.Write("stream_error", ("error", "connection refused vmid=999")));

        Assert.Contains("error=\"connection refused vmid=999\"", line);

        // The forged text is present, but only inside the quoted value. What matters is that a
        // parser honouring quotes sees no vmid field at all, so strip quoted runs and re-check.
        Assert.DoesNotContain("vmid=", StripQuoted(line));
    }

    /// <summary>Removes every double-quoted run, honouring backslash escapes, as a log parser would.</summary>
    private static string StripQuoted(string line)
    {
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes && c == '\\' && i + 1 < line.Length) { i++; continue; }
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes) sb.Append(c);
        }
        return sb.ToString();
    }

    [Fact]
    public void NewlinesCannotForgeAnExtraRecord()
    {
        var line = Capture(() => RelayLog.Write("stream_error", ("error", "bad\nexercise=false")));

        Assert.Single(line.Split('\n'));
        Assert.Contains("\\n", line);
    }

    [Fact]
    public void NullFieldsAreOmittedRatherThanLoggedAsEmpty()
    {
        var line = Capture(() => RelayLog.Write("stream_error", ("stream", null), ("vmid", "101")));

        Assert.DoesNotContain("stream=", line);
        Assert.Contains("vmid=101", line);
    }

    // ---- session registry ----

    private static Task NoPrepare(VmSession _) => Task.CompletedTask;

    [Fact]
    public async Task InstallingTheSameVmidTwiceReplacesTheStaleSession()
    {
        var registry = new VmSessionRegistry();
        var first = new VmSession("101", Entry("101", 24101, true));
        var second = new VmSession("101", Entry("101", 24101, true));

        Assert.False(await registry.InstallAsync(first, NoPrepare, CancellationToken.None));
        Assert.True(await registry.InstallAsync(second, NoPrepare, CancellationToken.None));

        Assert.True(registry.TryGet("101", out var current));
        Assert.Same(second, current);
        Assert.Equal(1, registry.Count);
    }

    /// <summary>
    /// The successor must not be published until its own preparation has run, or status can report
    /// a live session whose ingress port is not yet bound -- and, worse, while the port is still
    /// routed by the incarnation being replaced.
    /// </summary>
    [Fact]
    public async Task SuccessorIsNotVisibleUntilPreparationCompletes()
    {
        var registry = new VmSessionRegistry();
        var first = new VmSession("101", Entry("101", 24101, true));
        await registry.InstallAsync(first, NoPrepare, CancellationToken.None);

        var release = new TaskCompletionSource();
        var second = new VmSession("101", Entry("101", 24101, true));
        var install = registry.InstallAsync(second, async _ => await release.Task, CancellationToken.None);

        // Preparation is still running: the old incarnation is already gone, and the new one is
        // not yet published, so nothing is routable for this VMID.
        await Task.Delay(50);
        Assert.False(install.IsCompleted);
        Assert.False(registry.TryGet("101", out _));

        release.SetResult();
        Assert.True(await install);
        Assert.True(registry.TryGet("101", out var current));
        Assert.Same(second, current);
    }

    /// <summary>
    /// A late teardown of a stale session must not evict the live one that replaced it, or a VM
    /// that reconnected cleanly would silently lose its ingress.
    /// </summary>
    [Fact]
    public async Task LateUnregisterOfAStaleSessionDoesNotEvictItsSuccessor()
    {
        var registry = new VmSessionRegistry();
        var stale = new VmSession("101", Entry("101", 24101, true));
        var live = new VmSession("101", Entry("101", 24101, true));

        await registry.InstallAsync(stale, NoPrepare, CancellationToken.None);
        await registry.InstallAsync(live, NoPrepare, CancellationToken.None);

        Assert.False(registry.Unregister(stale));
        Assert.True(registry.TryGet("101", out var current));
        Assert.Same(live, current);

        Assert.True(registry.Unregister(live));
        Assert.False(registry.TryGet("101", out _));
    }

    [Fact]
    public async Task SnapshotDistinguishesConfiguredDownFromUnconfiguredUp()
    {
        var registry = new VmSessionRegistry();
        var configured = new Dictionary<string, VmEntry>
        {
            ["101"] = Entry("101", 24101, true),
            ["102"] = Entry("102", 24102, false),
        };

        // 101 is configured and live; 102 is configured and down; 999 is live but not configured.
        await registry.InstallAsync(new VmSession("101", configured["101"]), NoPrepare, CancellationToken.None);
        await registry.InstallAsync(new VmSession("999", null), NoPrepare, CancellationToken.None);

        var snap = registry.Snapshot(configured);

        Assert.Equal(3, snap.Count);
        Assert.True(snap.Single(v => v.Vmid == "101").SessionLive);
        Assert.False(snap.Single(v => v.Vmid == "102").SessionLive);

        var stray = snap.Single(v => v.Vmid == "999");
        Assert.True(stray.SessionLive);
        Assert.Null(stray.EgressTarget);
    }

    // ---- control status document ----

    [Fact]
    public void StatusDocumentIsUnmistakablyExerciseInfrastructure()
    {
        var json = ControlServer.BuildStatusJson("0.2.0", [], "/run/pve-tunnel/relay.sock", "/etc/pve/pve-tunnel/vms.json");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("exercise").GetBoolean());
        Assert.Equal("out-of-scope", root.GetProperty("scope").GetString());
        Assert.Contains("OUT OF SCOPE", root.GetProperty("notice").GetString());
        Assert.Equal("pve-tunnel-relay", root.GetProperty("component").GetString());
    }

    /// <summary>
    /// Work package 5 waits on readiness before handing a VM to Ansible, so "ready" must mean the
    /// controller can actually reach the guest, not merely that a session exists.
    /// </summary>
    [Fact]
    public void ReadyMeansTheControllerHasSomewhereToConnect()
    {
        var vms = new List<VmStatus>
        {
            // ingress configured and bound
            new("101", true, DateTime.UtcNow, 24101, true, true, "10.10.0.20:5000", "127.0.0.1:5986"),
            // ingress configured but no port bound yet
            new("102", true, DateTime.UtcNow, null, true, true, "10.10.0.20:5000", "127.0.0.1:5986"),
            // egress-only VM, ready as soon as the session is up
            new("103", true, DateTime.UtcNow, null, false, true, "10.10.0.20:5000", "127.0.0.1:5986"),
            // no session at all
            new("104", false, null, null, true, true, "10.10.0.20:5000", "127.0.0.1:5986"),
        };

        using var doc = JsonDocument.Parse(ControlServer.BuildStatusJson("0.2.0", vms, "/run/s.sock", "/etc/r.json"));
        var byId = doc.RootElement.GetProperty("vms").EnumerateArray()
            .ToDictionary(e => e.GetProperty("vmid").GetString()!, e => e.GetProperty("ready").GetBoolean());

        Assert.True(byId["101"]);
        Assert.False(byId["102"]);
        Assert.True(byId["103"]);
        Assert.False(byId["104"]);

        Assert.Equal(3, doc.RootElement.GetProperty("sessions_live").GetInt32());
    }
}

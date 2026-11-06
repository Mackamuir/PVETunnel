using System.Net;
using System.Text.Json;

namespace PveTunnel.Relay;

/// <summary>A validated target address. Only ever produced from trusted host-side configuration.</summary>
public readonly record struct Target(IPAddress Address, int Port)
{
    public override string ToString() => $"{Address}:{Port}";
}

/// <summary>One VM's entry in the registry.</summary>
public sealed record VmEntry
{
    public required string Vmid { get; init; }

    /// <summary>Host port the controller connects to for this VM. Allocated elsewhere, never here.</summary>
    public required int ManagementPort { get; init; }

    /// <summary>Guest-initiated streams are dialled to this host-side target (GHOSTS).</summary>
    public required Target EgressTarget { get; init; }

    /// <summary>
    /// Where the guest bridge is expected to deliver controller-initiated streams.
    /// </summary>
    /// <remarks>
    /// Advisory. The bridge holds the authoritative value in its own configuration, because the
    /// target must never travel on the wire where a guest could influence it. This field exists so
    /// operator status output can say what the guest is supposed to be doing; it can drift from
    /// the bridge's real setting and is not enforced by the relay.
    /// </remarks>
    public required Target IngressTarget { get; init; }

    public bool EgressEnabled { get; init; } = true;
    public bool IngressEnabled { get; init; }
}

/// <summary>Raised when the registry file cannot be trusted. Always fatal to startup or reload.</summary>
public sealed class VmRegistryException(string message) : Exception(message);

/// <summary>
/// The root-owned VMID to management-port mapping, normally under <c>/etc/pve/pve-tunnel/</c> so
/// it is shared cluster-wide and a live migration can rebind the same allocation on the
/// destination node.
/// </summary>
/// <remarks>
/// The relay is strictly a <b>consumer</b> of port allocations. It never invents one. The handoff
/// document calls allocation and registry locking a design gate and rejects hash/modulo schemes
/// without collision detection, so choosing the allocator is deliberately left outside this code;
/// what happens here is validation, and anything ambiguous fails closed rather than guessing.
/// </remarks>
public static class VmRegistry
{
    public const int SupportedVersion = 1;

    /// <summary>Lowest port that may be allocated for controller ingress. Below this is privileged.</summary>
    public const int MinManagementPort = 1024;

    public static IReadOnlyDictionary<string, VmEntry> Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new VmRegistryException($"cannot read VM registry at {path}: {ex.Message}");
        }
        return Parse(text);
    }

    public static IReadOnlyDictionary<string, VmEntry> Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new VmRegistryException($"VM registry is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new VmRegistryException("VM registry root must be a JSON object");

            if (!root.TryGetProperty("version", out var versionEl) || !versionEl.TryGetInt32(out var version))
                throw new VmRegistryException("VM registry is missing an integer \"version\"");
            if (version != SupportedVersion)
                throw new VmRegistryException($"VM registry version {version} is not supported (expected {SupportedVersion})");

            if (!root.TryGetProperty("vms", out var vmsEl) || vmsEl.ValueKind != JsonValueKind.Array)
                throw new VmRegistryException("VM registry is missing a \"vms\" array");

            var byVmid = new Dictionary<string, VmEntry>(StringComparer.Ordinal);
            var portOwner = new Dictionary<int, string>();

            foreach (var el in vmsEl.EnumerateArray())
            {
                var entry = ParseEntry(el);

                if (!byVmid.TryAdd(entry.Vmid, entry))
                    throw new VmRegistryException($"VM registry lists VMID {entry.Vmid} more than once");

                // A port collision would silently route one VM's management traffic into another
                // VM's guest. There is no safe way to pick a winner, so refuse the whole file.
                if (!portOwner.TryAdd(entry.ManagementPort, entry.Vmid))
                    throw new VmRegistryException(
                        $"management port {entry.ManagementPort} is claimed by both VMID {portOwner[entry.ManagementPort]} and VMID {entry.Vmid}");
            }

            return byVmid;
        }
    }

    private static VmEntry ParseEntry(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new VmRegistryException("each entry in \"vms\" must be a JSON object");

        var vmid = RequireString(el, "vmid");
        if (!IsValidVmid(vmid))
            throw new VmRegistryException($"invalid vmid {Quote(vmid)}: expected 1 to 10 digits");

        if (!el.TryGetProperty("managementPort", out var portEl) || !portEl.TryGetInt32(out var port))
            throw new VmRegistryException($"VMID {vmid}: missing an integer \"managementPort\"");
        if (port < MinManagementPort || port > 65535)
            throw new VmRegistryException(
                $"VMID {vmid}: managementPort {port} is outside {MinManagementPort}-65535");

        var ingressEnabled = OptionalBool(el, "ingress", false);
        var egressEnabled = OptionalBool(el, "egress", true);
        if (!ingressEnabled && !egressEnabled)
            throw new VmRegistryException($"VMID {vmid}: neither ingress nor egress is enabled, so the entry does nothing");

        var egressTarget = ParseTarget(vmid, "egressTarget", RequireString(el, "egressTarget"), requireLoopback: false);
        var ingressTarget = ParseTarget(vmid, "ingressTarget", RequireString(el, "ingressTarget"), requireLoopback: true);

        return new VmEntry
        {
            Vmid = vmid,
            ManagementPort = port,
            EgressTarget = egressTarget,
            IngressTarget = ingressTarget,
            EgressEnabled = egressEnabled,
            IngressEnabled = ingressEnabled,
        };
    }

    /// <summary>
    /// Parses "address:port". Only literal IP addresses are accepted -- no DNS. A hostname would
    /// make the target depend on resolution state at connect time, which is exactly the kind of
    /// late binding a fixed-target rule exists to prevent.
    /// </summary>
    private static Target ParseTarget(string vmid, string field, string raw, bool requireLoopback)
    {
        var sep = raw.LastIndexOf(':');
        if (sep <= 0 || sep == raw.Length - 1)
            throw new VmRegistryException($"VMID {vmid}: {field} {Quote(raw)} is not address:port");

        var hostPart = raw[..sep];
        // Accept a bracketed IPv6 literal, which is how such an address must be written with a port.
        if (hostPart.Length >= 2 && hostPart[0] == '[' && hostPart[^1] == ']')
            hostPart = hostPart[1..^1];

        if (!IPAddress.TryParse(hostPart, out var address))
            throw new VmRegistryException(
                $"VMID {vmid}: {field} host {Quote(hostPart)} is not a literal IP address (hostnames are not accepted)");

        if (!int.TryParse(raw[(sep + 1)..], out var port) || port < 1 || port > 65535)
            throw new VmRegistryException($"VMID {vmid}: {field} port in {Quote(raw)} is not 1-65535");

        if (requireLoopback && !IPAddress.IsLoopback(address))
            throw new VmRegistryException(
                $"VMID {vmid}: {field} {Quote(raw)} must be a guest loopback address");

        return new Target(address, port);
    }

    private static string RequireString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new VmRegistryException($"VM registry entry is missing the string field \"{name}\"");
        return v.GetString() ?? throw new VmRegistryException($"VM registry field \"{name}\" is null");
    }

    private static bool OptionalBool(JsonElement el, string name, bool fallback)
    {
        if (!el.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new VmRegistryException($"VM registry field \"{name}\" must be a boolean"),
        };
    }

    /// <summary>Proxmox VMIDs are numeric. Anything else means the registry disagrees with the host.</summary>
    public static bool IsValidVmid(string vmid)
    {
        if (vmid.Length is 0 or > 10) return false;
        foreach (var c in vmid)
            if (c is < '0' or > '9') return false;
        return true;
    }

    private static string Quote(string s) => $"\"{s}\"";
}

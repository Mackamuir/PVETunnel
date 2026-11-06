using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PveTunnel.Relay;

/// <summary>
/// Root-only status surface on a Unix domain socket. A connection receives one JSON object and
/// the socket closes; there are no commands, so nothing here can change relay behaviour.
/// </summary>
/// <remarks>
/// Read-only by construction. Work package 5 needs "is this VM's session registered yet" before
/// it hands a VM to Ansible, and work package 9 needs operator status; both are answered by
/// reading. Anything that could mutate state would need an authorisation model this does not have.
/// </remarks>
public static class ControlServer
{
    /// <summary>
    /// Builds the status document. Pure, so it can be asserted on without a socket.
    /// </summary>
    public static string BuildStatusJson(
        string version,
        IReadOnlyList<VmStatus> vms,
        string socketPath,
        string? registryPath)
    {
        var arr = new JsonArray();
        foreach (var vm in vms)
        {
            arr.Add(new JsonObject
            {
                ["vmid"] = vm.Vmid,
                ["session_live"] = vm.SessionLive,
                ["since_utc"] = vm.SinceUtc?.ToString("O"),
                ["ingress_port"] = vm.IngressPort,
                ["ingress_enabled"] = vm.IngressEnabled,
                ["egress_enabled"] = vm.EgressEnabled,
                ["egress_target"] = vm.EgressTarget,
                ["guest_ingress_target"] = vm.IngressTarget,
                // Readiness in the sense work package 5 needs: the VM has a live session and, when
                // ingress is configured, a bound port for the controller to reach.
                ["ready"] = vm.SessionLive && (!vm.IngressEnabled || vm.IngressPort is not null),
            });
        }

        var doc = new JsonObject
        {
            // The visibility checklist requires status output to be unmistakable, not just logs.
            ["exercise"] = true,
            ["scope"] = "out-of-scope",
            ["notice"] = RelayLog.ExerciseNotice,
            ["component"] = "pve-tunnel-relay",
            ["version"] = version,
            ["socket"] = socketPath,
            ["registry"] = registryPath,
            ["generated_utc"] = DateTime.UtcNow.ToString("O"),
            ["sessions_live"] = vms.Count(v => v.SessionLive),
            ["vms"] = arr,
        };

        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>
    /// Binds the control socket and restricts it to the owner, or throws.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from serving, and deliberately fatal. Continuing after a failed
    /// permission change would serve the status surface at whatever the umask allowed, which is
    /// exactly the case where the root-only contract matters -- an operator having pointed the
    /// socket path at a less restrictive directory. The socket is removed again on failure so no
    /// partly-hardened endpoint is left behind.
    /// </remarks>
    public static Socket Bind(string socketPath)
    {
        var dir = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(socketPath)) File.Delete(socketPath);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(16);

            if (OperatingSystem.IsLinux())
            {
                // Narrow it before anything is told where to find it.
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                var mode = File.GetUnixFileMode(socketPath);
                const UnixFileMode allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                if ((mode & ~allowed) != 0)
                    throw new IOException($"control socket permissions are {mode}, expected owner-only");
            }
        }
        catch
        {
            listener.Dispose();
            try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { /* best effort */ }
            throw;
        }

        RelayLog.Write("control_listening", ("path", socketPath));
        return listener;
    }

    /// <summary>Serves status on an already-bound, already-hardened listener until cancelled.</summary>
    public static async Task ServeAsync(
        Socket listener,
        string socketPath,
        Func<string> statusProvider,
        CancellationToken ct)
    {
        try
        {
            using (listener)
            {
                while (!ct.IsCancellationRequested)
                {
                    Socket conn;
                    try { conn = await listener.AcceptAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { continue; }
                    catch (ObjectDisposedException) { break; }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var payload = Encoding.UTF8.GetBytes(statusProvider() + "\n");
                            await conn.SendAsync(payload, SocketFlags.None, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) { RelayLog.Write("control_write_failed", ("error", ex.Message)); }
                        finally { conn.Dispose(); }
                    }, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { /* ignore */ }
        }
    }
}

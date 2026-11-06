using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PveTunnel.Relay;

/// <summary>
/// Resolves the identity of a process connected over a Unix domain socket via SO_PEERCRED,
/// then maps its PID to a Proxmox VMID by reading the QEMU command line (<c>-id &lt;vmid&gt;</c>).
/// Used for structured logging only. The guest cannot forge this because the connecting process
/// is QEMU on the host, not the guest.
/// </summary>
internal static class PeerCred
{
    private const int SOL_SOCKET = 1;
    private const int SO_PEERCRED = 17; // Linux

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int sockfd, int level, int optname, byte[] optval, ref int optlen);

    /// <summary>Returns the peer process PID, or -1 if unavailable.</summary>
    public static int GetPeerPid(Socket socket)
    {
        if (!OperatingSystem.IsLinux()) return -1;
        try
        {
            // struct ucred { pid_t pid; uid_t uid; gid_t gid; } => 3 x int32
            var buf = new byte[12];
            var len = buf.Length;
            var fd = (int)socket.Handle;
            if (getsockopt(fd, SOL_SOCKET, SO_PEERCRED, buf, ref len) != 0) return -1;
            return BitConverter.ToInt32(buf, 0);
        }
        catch { return -1; }
    }

    /// <summary>Best-effort PID → Proxmox VMID. Returns the VMID string, or "unknown".</summary>
    public static string ResolveVmid(int pid)
    {
        if (pid <= 0) return "unknown";
        try
        {
            var raw = File.ReadAllText($"/proc/{pid}/cmdline");
            var args = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-id")
                    return args[i + 1];
        }
        catch { /* process gone or not readable */ }
        return "unknown";
    }
}

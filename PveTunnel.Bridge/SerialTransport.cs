using System.Runtime.InteropServices;

namespace PveTunnel.Bridge;

/// <summary>
/// Opens the guest end of the virtio-serial port as a bidirectional <see cref="Stream"/>.
/// The QEMU device is defined as <c>virtserialport,name=pve-tunnel.relay.0</c>, which surfaces as:
///   Windows: \\.\Global\pve-tunnel.relay.0  (requires the virtio-serial "vioser" driver from virtio-win)
///   Linux:   /dev/virtio-ports/pve-tunnel.relay.0
/// </summary>
internal static class SerialTransport
{
    public static string DefaultPortPath(string portName) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $@"\\.\Global\{portName}"
            : $"/dev/virtio-ports/{portName}";

    public static Stream Open(string path)
    {
        // FileShare.ReadWrite: the device is a single bidirectional pipe; we hold both directions.
        // No buffering on our side beyond FileStream's default — the mux flushes each frame.
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            bufferSize: 0,
            useAsync: true);
    }
}

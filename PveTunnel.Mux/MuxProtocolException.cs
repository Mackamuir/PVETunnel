namespace PveTunnel.Mux;

/// <summary>
/// A peer sent something the mux will not tolerate: an oversized or malformed frame, a stream id
/// with the wrong parity, a duplicate SYN, a control frame with a bad payload size, an unknown
/// frame type, or more data than its receive window allowed.
/// </summary>
/// <remarks>
/// This is always fatal to the <b>session</b> and never to the process. The relay hosts one
/// session per VM; a guest that corrupts its own session must not take down the node relay or
/// disturb another VM's streams. Callers should log the VMID, drop that session, and apply
/// whatever reconnect rate limit they enforce.
/// </remarks>
public sealed class MuxProtocolException : IOException
{
    public MuxProtocolException(string message) : base(message) { }
}

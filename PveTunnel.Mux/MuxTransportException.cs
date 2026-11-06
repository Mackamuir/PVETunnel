namespace PveTunnel.Mux;

/// <summary>
/// A frame write failed after it had begun, so what the peer received is unknown.
/// </summary>
/// <remarks>
/// This is deliberately fatal to the session rather than recoverable. Once part of a frame may
/// have reached the peer, neither the framing nor the delivery state can be observed from this
/// side: a header may have landed without its payload, leaving the peer's parser mid-frame, or a
/// whole frame may have arrived before the exception surfaced. Recovering from a state you cannot
/// observe is not recovery, so the session is torn down and rebuilt instead.
///
/// The relay logs this distinctly from a peer protocol violation. How often it fires on real
/// virtio-serial hardware under load is not yet known, and that number is worth having before
/// anyone tunes reconnect behaviour.
/// </remarks>
public sealed class MuxTransportException(string message, Exception inner)
    : IOException(message, inner);

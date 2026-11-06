namespace PveTunnel.Mux;

/// <summary>
/// Resource limits for one <see cref="MuxSession"/>. Every value bounds something a peer can
/// drive, because the peer at the guest end of the virtio-serial port is assumed hostile: a
/// trainee with local administrator rights can replace the bridge with anything they like.
/// The relay applies one instance per VM so a single malicious guest cannot exhaust the node.
/// </summary>
public sealed record MuxOptions
{
    /// <summary>Largest DATA payload accepted or emitted in one frame.</summary>
    public int MaxFramePayload { get; init; } = 16 * 1024;

    /// <summary>Bytes a peer may have in flight per stream before it must wait for a WINDOW frame.</summary>
    public int InitialWindow { get; init; } = 256 * 1024;

    /// <summary>Concurrent streams a peer may hold open on this session.</summary>
    public int MaxConcurrentStreams { get; init; } = 256;

    /// <summary>
    /// Undelivered DATA frames a peer may queue on one stream. The byte window is the real
    /// bound; this is a second bound on frame <i>count</i>, so a peer cannot turn a 256 KiB window
    /// into 256k single-byte allocations. With a 16 KiB frame and a 256 KiB window a well-behaved
    /// peer needs 16, so this is deliberately generous.
    /// </summary>
    public int MaxBufferedChunks { get; init; } = 4096;

    /// <summary>
    /// Undelivered payload bytes a peer may hold across <b>every</b> stream on this session.
    /// </summary>
    /// <remarks>
    /// The per-stream window does not compose into a per-VM bound. One session is one VM, so
    /// without this a guest could open <see cref="MaxConcurrentStreams"/> streams and pin
    /// MaxConcurrentStreams * InitialWindow bytes -- 64 MiB at the defaults. The work package
    /// requires buffered bytes to be bounded per VM, and the relay cannot see bytes queued inside
    /// individual streams, so the budget has to live at the session.
    /// </remarks>
    public int MaxSessionBufferedBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Undelivered DATA frames a peer may hold across every stream on this session.</summary>
    public int MaxSessionBufferedChunks { get; init; } = 8192;

    public static MuxOptions Default { get; } = new();

    /// <summary>Throws if any limit is nonsensical. Called by <see cref="MuxSession"/> at construction.</summary>
    internal void Validate()
    {
        if (MaxFramePayload <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxFramePayload), MaxFramePayload, "must be positive");
        if (InitialWindow < MaxFramePayload)
            throw new ArgumentOutOfRangeException(nameof(InitialWindow), InitialWindow,
                $"must be at least {nameof(MaxFramePayload)} ({MaxFramePayload}), otherwise a single full frame can never be sent");
        if (MaxConcurrentStreams <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentStreams), MaxConcurrentStreams, "must be positive");
        if (MaxBufferedChunks <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBufferedChunks), MaxBufferedChunks, "must be positive");
        if (MaxSessionBufferedBytes < InitialWindow)
            throw new ArgumentOutOfRangeException(nameof(MaxSessionBufferedBytes), MaxSessionBufferedBytes,
                $"must be at least {nameof(InitialWindow)} ({InitialWindow}), otherwise a single stream can never fill its window");
        if (MaxSessionBufferedChunks <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxSessionBufferedChunks), MaxSessionBufferedChunks, "must be positive");
    }
}

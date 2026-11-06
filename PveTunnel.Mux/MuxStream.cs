using System.Threading.Channels;

namespace PveTunnel.Mux;

/// <summary>
/// A single logical stream within a <see cref="MuxSession"/>, exposed as a standard
/// <see cref="Stream"/> so it can be pumped to/from a TCP socket with <c>CopyToAsync</c>.
/// </summary>
/// <remarks>
/// Inbound buffering is bounded by the receive window, and that bound is <b>enforced</b> rather
/// than assumed. A well-behaved peer stops sending when it runs out of credit; a hostile one
/// ignores WINDOW frames entirely, so exceeding the granted allowance is treated as a protocol
/// violation and kills the session.
/// </remarks>
public sealed class MuxStream : Stream
{
    private readonly MuxSession _session;
    private readonly MuxOptions _options;
    // SingleReader is deliberately off: teardown drains queued chunks to return their budget,
    // and that can race with a reader still in ReadAsync.
    private readonly Channel<byte[]> _inbound =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = false });
    private readonly FlowController _sendFlow;
    private readonly CancellationTokenSource _resetCts = new();

    private ReadOnlyMemory<byte> _current;
    private long _consumedSinceCredit;

    /// <summary>Total bytes the peer has been permitted to send: the initial window plus every credit granted.</summary>
    private long _receiveAllowed;

    /// <summary>Total bytes the peer has actually sent.</summary>
    private long _receiveUsed;

    /// <summary>DATA frames queued and not yet handed to a reader.</summary>
    private int _bufferedChunks;

    private int _finSent;
    private bool _disposed;

    public uint Id { get; }

    internal MuxStream(MuxSession session, uint id, MuxOptions options)
    {
        _session = session;
        _options = options;
        _sendFlow = new FlowController(options.InitialWindow);
        _receiveAllowed = options.InitialWindow;
        Id = id;
    }

    // ---- inbound dispatch (called by the session read loop) ----

    /// <summary>
    /// Queues a received DATA payload. Throws <see cref="MuxProtocolException"/> if the peer has
    /// overrun its receive window or queued an implausible number of frames; the session read
    /// loop turns that into a session teardown.
    /// </summary>
    internal void OnData(byte[] data)
    {
        var used = Interlocked.Add(ref _receiveUsed, data.Length);
        var allowed = Interlocked.Read(ref _receiveAllowed);
        if (used > allowed)
            throw new MuxProtocolException(
                $"stream {Id}: peer sent {used} bytes but was only granted {allowed}");

        if (Interlocked.Increment(ref _bufferedChunks) > _options.MaxBufferedChunks)
            throw new MuxProtocolException(
                $"stream {Id}: peer queued more than {_options.MaxBufferedChunks} unread frames");

        // Per-stream limits do not compose into a per-VM limit, and one session is one VM.
        _session.AcquireBuffer(Id, data.Length);

        if (!_inbound.Writer.TryWrite(data))
        {
            // The stream was reset or disposed between the charge and the write, so nothing will
            // ever dequeue this chunk. Return the charge here or it is leaked for the session
            // lifetime.
            Interlocked.Decrement(ref _bufferedChunks);
            _session.ReleaseBuffer(data.Length);
        }
    }

    internal void OnFin() => _inbound.Writer.TryComplete();

    internal void OnReset()
    {
        _inbound.Writer.TryComplete(new IOException("stream reset by peer"));
        ReleaseQueuedCharges();
        _sendFlow.Fault();
        try { _resetCts.Cancel(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Returns the session budget held by anything still queued and unread.
    /// </summary>
    /// <remarks>
    /// Without this a peer can fill a stream inside its own receive window, reset it, and repeat on
    /// fresh stream ids until <see cref="MuxOptions.MaxSessionBufferedBytes"/> is permanently
    /// consumed -- at which point a later, entirely compliant stream is the one that tears the VM
    /// session down. Completing the channel is not enough; the buffered chunks have to be taken out
    /// of it. Each chunk is removed by exactly one of this method or <see cref="ReadAsync"/>, so a
    /// charge is never released twice.
    /// </remarks>
    private void ReleaseQueuedCharges()
    {
        while (_inbound.Reader.TryRead(out var chunk))
        {
            Interlocked.Decrement(ref _bufferedChunks);
            _session.ReleaseBuffer(chunk.Length);
        }
    }

    internal void OnWindow(uint credit) => _sendFlow.Release(credit);

    // ---- Stream read ----

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return 0;

        while (_current.Length == 0)
        {
            try
            {
                if (!await _inbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    return 0; // FIN: clean EOF
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            if (!_inbound.Reader.TryRead(out var chunk)) continue;
            Interlocked.Decrement(ref _bufferedChunks);
            _session.ReleaseBuffer(chunk.Length);
            _current = chunk;
        }

        var n = Math.Min(buffer.Length, _current.Length);
        _current.Span.Slice(0, n).CopyTo(buffer.Span);
        _current = _current.Slice(n);

        // Replenish the peer's send window once we have drained about half of it. The allowance is
        // recorded before the WINDOW frame goes out, so data arriving on the credit we are about to
        // grant is never mistaken for an overrun.
        //
        // There is deliberately no rollback if the write fails. Rolling back would only be sound
        // if a failed write proved the peer never received the credit, and it does not: the frame
        // may have arrived before the exception surfaced. Rolling back then would have local
        // enforcement reject traffic the peer was legitimately told it could send. A failed write
        // is fatal to the session instead, so the allowance dies with it.
        _consumedSinceCredit += n;
        if (_consumedSinceCredit >= _options.InitialWindow / 2)
        {
            var credit = (uint)_consumedSinceCredit;
            _consumedSinceCredit = 0;
            Interlocked.Add(ref _receiveAllowed, credit);
            await _session.SendWindowAsync(Id, credit, ct).ConfigureAwait(false);
        }
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);

    // ---- Stream write ----

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _resetCts.Token);
        while (!buffer.IsEmpty)
        {
            var want = Math.Min(buffer.Length, _options.MaxFramePayload);
            var take = await _sendFlow.AcquireAsync(want, linked.Token).ConfigureAwait(false);
            await _session.SendDataAsync(Id, buffer.Slice(0, take), linked.Token).ConfigureAwait(false);
            buffer = buffer.Slice(take);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (Interlocked.Exchange(ref _finSent, 1) == 0)
        {
            try { await _session.SendFinAsync(Id, CancellationToken.None).ConfigureAwait(false); }
            catch { /* transport already gone */ }
        }
        _session.RemoveStream(Id);
        _inbound.Writer.TryComplete();
        ReleaseQueuedCharges(); // local dispose with unread data leaks the same way a reset does
        _resetCts.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    // ---- Stream boilerplate ----

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

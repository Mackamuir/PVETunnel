using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PveTunnel.Mux;

/// <summary>
/// Multiplexes many <see cref="MuxStream"/>s over a single bidirectional byte-stream transport
/// (the virtio-serial port). One end is the "client" (opens streams), the other the "server"
/// (accepts them). Stream ids are partitioned by parity to avoid collisions: client uses odd ids,
/// server uses even ids.
/// </summary>
/// <remarks>
/// The peer is not trusted. Every field of every inbound frame is validated before it is acted on,
/// and a violation raises <see cref="MuxProtocolException"/> out of <see cref="RunAsync"/>, which
/// tears down this session alone. Parity enforcement matters most: once the relay opens
/// server-initiated streams for controller ingress, a guest able to SYN an even id could collide
/// with a WinRM stream the relay is about to allocate.
/// </remarks>
public sealed class MuxSession : IAsyncDisposable
{
    private readonly Stream _transport;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _writeHeader = new byte[Protocol.HeaderSize];
    private readonly ConcurrentDictionary<uint, MuxStream> _streams = new();
    private readonly Channel<MuxStream> _accepted =
        Channel.CreateUnbounded<MuxStream>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly bool _isClient;
    private readonly MuxOptions _options;
    private uint _nextId;
    private int _streamCount;
    private long _sessionBufferedBytes;
    private int _sessionBufferedChunks;
    private Exception? _writeFault;
    private int _disposed;

    public MuxSession(Stream transport, bool isClient, MuxOptions? options = null)
    {
        _options = options ?? MuxOptions.Default;
        _options.Validate();
        _transport = transport;
        _isClient = isClient;
        _nextId = isClient ? 1u : 2u; // odd for client, even for server
    }

    public MuxOptions Options => _options;

    /// <summary>Parity of the stream ids the peer may open: the one this side does not allocate.</summary>
    private uint RemoteParity => _isClient ? 0u : 1u;

    /// <summary>
    /// Runs the read loop until the transport ends or the session is disposed. Throws
    /// <see cref="MuxProtocolException"/> if the peer violates the protocol; the caller should
    /// treat that as "drop this VM session", not as a node-level failure.
    /// </summary>
    public Task RunAsync(CancellationToken ct = default) => ReadLoopAsync(ct);

    /// <summary>Open a new outbound stream (client side).</summary>
    public async Task<MuxStream> OpenStreamAsync(CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _streamCount) > _options.MaxConcurrentStreams)
        {
            Interlocked.Decrement(ref _streamCount);
            throw new InvalidOperationException(
                $"cannot open stream: already at MaxConcurrentStreams ({_options.MaxConcurrentStreams})");
        }

        var id = Interlocked.Add(ref _nextId, 2) - 2;
        if (id == 0 || _streams.ContainsKey(id))
        {
            // _nextId wraps after 2^31 streams. Parity survives the wrap but ids repeat, so fail
            // closed rather than overwriting a live stream with a colliding id.
            Interlocked.Decrement(ref _streamCount);
            throw new InvalidOperationException($"stream id space exhausted: id {id} collides with a live stream");
        }

        var stream = new MuxStream(this, id, _options);
        _streams[id] = stream;
        try
        {
            await SendControlAsync(FrameType.Syn, id, ct).ConfigureAwait(false);
        }
        catch
        {
            // The SYN never reached the peer, so as far as it is concerned this stream does not
            // exist. Returning it would hand the caller something that looks usable but silently
            // goes nowhere, and would leak both the dictionary entry and the stream count.
            RemoveStream(id);
            stream.OnReset();
            throw;
        }
        return stream;
    }

    /// <summary>Accept the next inbound stream (server side). Returns null when the session ends.</summary>
    public async Task<MuxStream?> AcceptStreamAsync(CancellationToken ct = default)
    {
        try
        {
            if (await _accepted.Reader.WaitToReadAsync(ct).ConfigureAwait(false) &&
                _accepted.Reader.TryRead(out var s))
                return s;
        }
        catch (ChannelClosedException) { }
        return null;
    }

    // ---- read loop ----

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var token = linked.Token;
        var header = new byte[Protocol.HeaderSize];
        try
        {
            while (true)
            {
                await _transport.ReadExactlyAsync(header, token).ConfigureAwait(false);
                Protocol.ParseHeader(header, out var id, out var rawType, out var rawLength);

                if (!Protocol.IsKnown(rawType))
                    throw new MuxProtocolException($"unknown frame type {rawType} on stream {id}");
                var type = (FrameType)rawType;

                // Bound the length before allocating. rawLength is a uint straight off the wire;
                // an unchecked cast to int would turn 0x80000000 negative, the payload read would
                // be skipped, and those bytes would then be misread as the next frame header.
                var expectedControl = Protocol.ExpectedControlPayload(type);
                if (expectedControl >= 0)
                {
                    if (rawLength != (uint)expectedControl)
                        throw new MuxProtocolException(
                            $"{type} frame on stream {id} declares payload {rawLength}, expected {expectedControl}");
                }
                else if (rawLength > (uint)_options.MaxFramePayload)
                {
                    throw new MuxProtocolException(
                        $"DATA frame on stream {id} declares payload {rawLength}, limit {_options.MaxFramePayload}");
                }

                var length = (int)rawLength; // safe: bounded above by a control size or MaxFramePayload
                byte[] payload = Array.Empty<byte>();
                if (length > 0)
                {
                    payload = new byte[length];
                    await _transport.ReadExactlyAsync(payload, token).ConfigureAwait(false);
                }
                Dispatch(id, type, payload);
            }
        }
        catch (EndOfStreamException) { /* transport closed */ }
        catch (OperationCanceledException)
        {
            // A write fault cancels the read loop, so distinguish that from an ordinary shutdown.
            if (Volatile.Read(ref _writeFault) is { } fault)
            {
                FailAll();
                throw new MuxTransportException(
                    "session ended because a frame write failed mid-frame; delivery state is indeterminate", fault);
            }
        }
        catch (MuxProtocolException)
        {
            FailAll();
            throw; // fatal to this session only; the caller drops this VM
        }
        catch (IOException) { }
        finally
        {
            FailAll();
        }
    }

    private void Dispatch(uint id, FrameType type, byte[] payload)
    {
        switch (type)
        {
            case FrameType.Syn:
                // The peer may only open ids of the parity this side does not allocate, and never
                // id 0. Without this check a guest could SYN into the relay even-id space and
                // collide with a controller-ingress stream the relay is about to open.
                if (id == 0 || id % 2 != RemoteParity)
                    throw new MuxProtocolException(
                        $"peer opened stream {id} with wrong parity (expected nonzero {(RemoteParity == 1u ? "odd" : "even")})");

                if (_streams.ContainsKey(id))
                    throw new MuxProtocolException($"duplicate SYN for stream {id}");

                if (Interlocked.Increment(ref _streamCount) > _options.MaxConcurrentStreams)
                {
                    Interlocked.Decrement(ref _streamCount);
                    _ = FireAndForgetControlAsync(FrameType.Rst, id);
                    break;
                }

                var incoming = new MuxStream(this, id, _options);
                if (_streams.TryAdd(id, incoming))
                {
                    _accepted.Writer.TryWrite(incoming);
                }
                else
                {
                    Interlocked.Decrement(ref _streamCount);
                    throw new MuxProtocolException($"duplicate SYN for stream {id}");
                }
                break;

            case FrameType.Data:
                if (_streams.TryGetValue(id, out var ds)) ds.OnData(payload);
                else _ = FireAndForgetControlAsync(FrameType.Rst, id);
                break;

            case FrameType.Fin:
                if (_streams.TryGetValue(id, out var fs)) fs.OnFin();
                break;

            case FrameType.Rst:
                if (_streams.TryRemove(id, out var rs))
                {
                    Interlocked.Decrement(ref _streamCount);
                    rs.OnReset();
                }
                break;

            case FrameType.Window:
                // Length was already validated as exactly WindowPayloadSize.
                if (_streams.TryGetValue(id, out var ws))
                    ws.OnWindow(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload));
                break;
        }
    }

    // ---- frame writers (serialized) ----

    /// <summary>
    /// Records a mid-frame write failure and ends the session. Delivery and framing state are
    /// indeterminate once any byte of a frame may have gone out, so there is nothing safe to
    /// resume from. The first fault wins; later ones are noise from the same collapse.
    /// </summary>
    private void FaultSession(Exception ex)
    {
        Interlocked.CompareExchange(ref _writeFault, ex, null);
        try { _shutdown.Cancel(); } catch { /* already shutting down */ }
    }


    internal async Task SendDataAsync(uint id, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Protocol.WriteHeader(_writeHeader, id, FrameType.Data, data.Length);
            await _transport.WriteAsync(_writeHeader, ct).ConfigureAwait(false);
            await _transport.WriteAsync(data, ct).ConfigureAwait(false);
            await _transport.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FaultSession(ex);
            throw;
        }
        finally { _writeLock.Release(); }
    }

    internal Task SendFinAsync(uint id, CancellationToken ct) => SendControlAsync(FrameType.Fin, id, ct);

    internal async Task SendWindowAsync(uint id, uint credit, CancellationToken ct)
    {
        var payload = new byte[Protocol.WindowPayloadSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload, credit);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Protocol.WriteHeader(_writeHeader, id, FrameType.Window, payload.Length);
            await _transport.WriteAsync(_writeHeader, ct).ConfigureAwait(false);
            await _transport.WriteAsync(payload, ct).ConfigureAwait(false);
            await _transport.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FaultSession(ex);
            throw;
        }
        finally { _writeLock.Release(); }
    }

    private async Task SendControlAsync(FrameType type, uint id, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Protocol.WriteHeader(_writeHeader, id, type, 0);
            await _transport.WriteAsync(_writeHeader, ct).ConfigureAwait(false);
            await _transport.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FaultSession(ex);
            throw;
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Best-effort control send for teardown paths that have no caller to report to. A write
    /// failure means the transport is gone, so end the session rather than let the error vanish
    /// into an unobserved task.
    /// </summary>
    private async Task FireAndForgetControlAsync(FrameType type, uint id)
    {
        try
        {
            await SendControlAsync(type, id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException)
        {
            try { _shutdown.Cancel(); } catch { /* already shutting down */ }
        }
    }

    /// <summary>
    /// Charges undelivered payload against the session-wide budget. Throws if the peer is holding
    /// more across all its streams than one VM is allowed.
    /// </summary>
    internal void AcquireBuffer(uint streamId, int bytes)
    {
        var total = Interlocked.Add(ref _sessionBufferedBytes, bytes);
        if (total > _options.MaxSessionBufferedBytes)
        {
            Interlocked.Add(ref _sessionBufferedBytes, -bytes);
            throw new MuxProtocolException(
                $"stream {streamId}: peer holds {total} unread bytes across the session, limit {_options.MaxSessionBufferedBytes}");
        }

        var chunks = Interlocked.Increment(ref _sessionBufferedChunks);
        if (chunks > _options.MaxSessionBufferedChunks)
        {
            Interlocked.Decrement(ref _sessionBufferedChunks);
            Interlocked.Add(ref _sessionBufferedBytes, -bytes);
            throw new MuxProtocolException(
                $"stream {streamId}: peer holds {chunks} unread frames across the session, limit {_options.MaxSessionBufferedChunks}");
        }
    }

    /// <summary>Returns budget once a chunk has been handed to a reader.</summary>
    internal void ReleaseBuffer(int bytes)
    {
        Interlocked.Add(ref _sessionBufferedBytes, -bytes);
        Interlocked.Decrement(ref _sessionBufferedChunks);
    }

    internal void RemoveStream(uint id)
    {
        if (_streams.TryRemove(id, out _)) Interlocked.Decrement(ref _streamCount);
    }

    private void FailAll()
    {
        _accepted.Writer.TryComplete();
        foreach (var kv in _streams)
            kv.Value.OnReset();
        _streams.Clear();
        Interlocked.Exchange(ref _streamCount, 0);
        Interlocked.Exchange(ref _sessionBufferedBytes, 0L);
        Interlocked.Exchange(ref _sessionBufferedChunks, 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _shutdown.Cancel(); } catch { /* ignore */ }
        FailAll();
        _shutdown.Dispose();
        _writeLock.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}

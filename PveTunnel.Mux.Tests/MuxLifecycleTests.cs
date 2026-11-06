using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PveTunnel.Mux;
using Xunit;

namespace PveTunnel.Mux.Tests;

/// <summary>
/// Lifecycle and accounting tests: what happens when the transport fails underneath a control
/// write, and whether resource budgets hold across streams rather than only within one.
/// These cover the paths reverse ingress will depend on, where a stream that looks open but was
/// never announced to the peer would be a silent hang rather than a visible error.
/// </summary>
public class MuxLifecycleTests
{
    private const int TimeoutSeconds = 15;
    private const int HeaderSize = 9;
    private const byte Syn = 1, Data = 2, Rst = 4, Window = 5;

    private static byte[] Frame(uint id, byte type, uint declaredLength, byte[]? payload = null)
    {
        var body = payload ?? Array.Empty<byte>();
        var buf = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, id);
        buf[4] = type;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(5), declaredLength);
        body.CopyTo(buf, HeaderSize);
        return buf;
    }

    private static byte[] Frame(uint id, byte type, byte[] payload) => Frame(id, type, (uint)payload.Length, payload);

    // ---- transport doubles ----

    /// <summary>A transport whose writes always fail and whose reads never complete.</summary>
    private sealed class DeadWriteStream : Stream
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await _never.Task.WaitAsync(ct).ConfigureAwait(false);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => throw new IOException("injected transport write failure");

        public override Task FlushAsync(CancellationToken ct) => throw new IOException("injected transport flush failure");

        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    /// <summary>Wraps a real transport and fails a chosen number of upcoming writes.</summary>
    private sealed class FaultInjectingStream(Stream inner) : Stream
    {
        private int _skipWrites;
        private int _failWrites;
        private volatile bool _failFlush;

        public void FailNextWrites(int count) => Interlocked.Exchange(ref _failWrites, count);

        /// <summary>Lets a frame start cleanly, then fails: header out, payload lost.</summary>
        public void FailWriteAfter(int successes, int count = 1)
        {
            Interlocked.Exchange(ref _skipWrites, successes);
            Interlocked.Exchange(ref _failWrites, count);
        }

        /// <summary>The whole frame reaches the peer and the failure surfaces afterwards.</summary>
        public void FailFlushes() => _failFlush = true;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Decrement(ref _skipWrites) >= 0) return inner.WriteAsync(buffer, ct);
            if (Interlocked.Decrement(ref _failWrites) >= 0)
                throw new IOException("injected transport write failure");
            return inner.WriteAsync(buffer, ct);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => inner.ReadAsync(buffer, ct);

        public override Task FlushAsync(CancellationToken ct)
            => _failFlush ? throw new IOException("injected transport flush failure") : inner.FlushAsync(ct);

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    private sealed record Rig(NetworkStream Raw, FaultInjectingStream Fault, MuxSession Victim, Task Run, CancellationTokenSource Cts)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Run; } catch { }
            await Victim.DisposeAsync();
            await Raw.DisposeAsync();
            Cts.Dispose();
        }
    }

    private static async Task<Rig> RawPeerAsync(bool victimIsClient = false, MuxOptions? options = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var attacker = new TcpClient();
        var connect = attacker.ConnectAsync(IPAddress.Loopback, port);
        var victimTcp = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        var fault = new FaultInjectingStream(victimTcp.GetStream());
        var victim = new MuxSession(fault, victimIsClient, options);
        return new Rig(attacker.GetStream(), fault, victim, victim.RunAsync(cts.Token), cts);
    }

    // ---- parity, client role ----

    /// <summary>
    /// The server-role half of parity was already covered. This is the other half, and it is the
    /// direction that matters for reverse ingress: the relay will run as the server allocating
    /// even ids, so the guest bridge running as client must accept exactly those.
    /// </summary>
    [Theory]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(4096u)]
    public async Task ClientRole_AcceptsEvenStreamsFromPeer(uint id)
    {
        await using var rig = await RawPeerAsync(victimIsClient: true);
        await rig.Raw.WriteAsync(Frame(id, Syn, 0));

        var accepted = await rig.Victim.AcceptStreamAsync(rig.Cts.Token);
        Assert.NotNull(accepted);
        Assert.Equal(id, accepted!.Id);
        Assert.False(rig.Run.IsCompleted);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(4097u)]
    public async Task ClientRole_RejectsOddStreamsFromPeer(uint id)
    {
        await using var rig = await RawPeerAsync(victimIsClient: true);
        await rig.Raw.WriteAsync(Frame(id, Syn, 0));

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("parity", ex.Message);
    }

    // ---- control-write failure ----

    /// <summary>
    /// A stream whose SYN never reached the peer is not a stream. Before this was fixed the
    /// control writer swallowed IOException, so OpenStreamAsync returned a usable-looking stream
    /// that silently went nowhere, and leaked both the dictionary entry and the stream slot.
    /// </summary>
    [Fact]
    public async Task OpenStream_WhenSynWriteFails_ThrowsInsteadOfReturningDeadStream()
    {
        await using var session = new MuxSession(new DeadWriteStream(), isClient: true);
        await Assert.ThrowsAsync<IOException>(() => session.OpenStreamAsync(CancellationToken.None));
    }

    /// <summary>
    /// The leak half of the same defect: a failed open must return its stream slot, otherwise a
    /// flapping transport walks the session into MaxConcurrentStreams and stays there.
    /// </summary>
    [Fact]
    public async Task OpenStream_WhenSynWriteFails_DoesNotLeakStreamSlots()
    {
        var options = new MuxOptions { MaxConcurrentStreams = 4 };
        await using var session = new MuxSession(new DeadWriteStream(), isClient: true, options);

        // Far more failed opens than there are slots. If slots leaked, the later attempts would
        // fail with InvalidOperationException rather than the transport IOException.
        for (var i = 0; i < 20; i++)
            await Assert.ThrowsAsync<IOException>(() => session.OpenStreamAsync(CancellationToken.None));
    }

    /// <summary>
    /// Drives a stream to the point where the next read must emit a WINDOW frame, so a test can
    /// choose how that write fails.
    /// </summary>
    private static async Task<(Rig Rig, MuxStream Stream)> StreamAtCreditThresholdAsync()
    {
        var options = new MuxOptions { MaxFramePayload = 16 * 1024, InitialWindow = 64 * 1024 };
        var rig = await RawPeerAsync(options: options);

        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        var stream = await rig.Victim.AcceptStreamAsync(rig.Cts.Token);
        Assert.NotNull(stream);

        // Fill the window exactly, so draining it crosses the half-window credit threshold.
        for (var i = 0; i < 4; i++)
            await rig.Raw.WriteAsync(Frame(1, Data, new byte[16 * 1024]));

        return (rig, stream!);
    }

    private static async Task DrainUntilFailureAsync(MuxStream stream, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
    }

    /// <summary>
    /// A frame write that fails after it has begun leaves delivery and framing state
    /// indeterminate, so the session dies rather than trying to recover.
    /// </summary>
    /// <remarks>
    /// This replaces an earlier design that rolled the credit back and carried on. Rollback is only
    /// sound if a failed write proves the peer never received the frame, and it does not: the frame
    /// may have arrived before the exception surfaced, in which case local enforcement would go on
    /// to reject traffic the peer had legitimately been told it could send. The three shapes below
    /// are indistinguishable from this side, which is the whole argument for treating them alike.
    /// </remarks>
    [Fact]
    public async Task WindowWriteFailingOutright_TerminatesTheSession()
    {
        var (rig, stream) = await StreamAtCreditThresholdAsync();
        await using var _ = rig;

        rig.Fault.FailNextWrites(1); // header never leaves

        await Assert.ThrowsAnyAsync<IOException>(() => DrainUntilFailureAsync(stream, rig.Cts.Token));
        var fatal = await Assert.ThrowsAsync<MuxTransportException>(() => rig.Run);
        Assert.Contains("indeterminate", fatal.Message);
    }

    [Fact]
    public async Task PartialFrameWrite_TerminatesTheSession()
    {
        var (rig, stream) = await StreamAtCreditThresholdAsync();
        await using var _ = rig;

        // Header succeeds, payload fails: the peer's parser is left mid-frame.
        rig.Fault.FailWriteAfter(successes: 1);

        await Assert.ThrowsAnyAsync<IOException>(() => DrainUntilFailureAsync(stream, rig.Cts.Token));
        await Assert.ThrowsAsync<MuxTransportException>(() => rig.Run);
    }

    [Fact]
    public async Task CompletedFrameThenFailure_TerminatesTheSession()
    {
        var (rig, stream) = await StreamAtCreditThresholdAsync();
        await using var _ = rig;

        // Both writes land and the failure surfaces on flush: the peer very likely got the credit.
        rig.Fault.FailFlushes();

        await Assert.ThrowsAnyAsync<IOException>(() => DrainUntilFailureAsync(stream, rig.Cts.Token));
        await Assert.ThrowsAsync<MuxTransportException>(() => rig.Run);
    }

    // ---- session-wide buffer budget ----

    /// <summary>
    /// Per-stream limits do not compose into a per-VM limit. One session is one VM, so without a
    /// session-wide budget a guest could open MaxConcurrentStreams streams and pin
    /// MaxConcurrentStreams * InitialWindow bytes -- 64 MiB at the original defaults -- while every
    /// individual stream stayed politely inside its own window.
    /// </summary>
    [Fact]
    public async Task BufferedBytesAreBoundedAcrossStreams_NotOnlyWithinOne()
    {
        var options = new MuxOptions
        {
            MaxFramePayload = 16 * 1024,
            InitialWindow = 64 * 1024,
            MaxConcurrentStreams = 16,
            MaxSessionBufferedBytes = 64 * 1024, // exactly one stream's worth
        };
        await using var rig = await RawPeerAsync(options: options);

        // Two streams, each staying strictly inside its own 64 KiB window. Nothing reads them.
        foreach (var id in new uint[] { 1, 3 })
        {
            await rig.Raw.WriteAsync(Frame(id, Syn, 0));
            for (var i = 0; i < 4; i++)
                await rig.Raw.WriteAsync(Frame(id, Data, new byte[16 * 1024]));
        }

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("across the session", ex.Message);
    }

    /// <summary>A single stream inside both its own window and the session budget is untouched.</summary>
    [Fact]
    public async Task SessionBudget_DoesNotPenaliseAWellBehavedStream()
    {
        var options = new MuxOptions
        {
            MaxFramePayload = 16 * 1024,
            InitialWindow = 64 * 1024,
            MaxSessionBufferedBytes = 256 * 1024,
        };
        await using var rig = await RawPeerAsync(options: options);

        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        for (var i = 0; i < 4; i++)
            await rig.Raw.WriteAsync(Frame(1, Data, new byte[16 * 1024]));

        var stream = await rig.Victim.AcceptStreamAsync(rig.Cts.Token);
        Assert.NotNull(stream);

        var buf = new byte[64 * 1024];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await stream!.ReadAsync(buf.AsMemory(total), rig.Cts.Token);
            if (n == 0) break;
            total += n;
        }

        Assert.Equal(buf.Length, total);
        Assert.False(rig.Run.IsCompleted, "a compliant peer must not be torn down");
    }

    /// <summary>
    /// Draining a stream must return its budget, or a long-lived session leaks its own allowance
    /// and eventually kills a peer that never misbehaved.
    /// </summary>
    [Fact]
    public async Task SessionBudget_IsReleasedAsDataIsRead()
    {
        var options = new MuxOptions
        {
            MaxFramePayload = 16 * 1024,
            InitialWindow = 64 * 1024,
            MaxSessionBufferedBytes = 64 * 1024,
        };
        await using var rig = await RawPeerAsync(options: options);

        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        var stream = await rig.Victim.AcceptStreamAsync(rig.Cts.Token);
        Assert.NotNull(stream);

        // Push and fully drain the whole budget several times over. If budget were not returned,
        // the second pass would trip the session limit.
        var buf = new byte[16 * 1024];
        for (var round = 0; round < 8; round++)
        {
            await rig.Raw.WriteAsync(Frame(1, Data, new byte[16 * 1024]));
            var total = 0;
            while (total < buf.Length)
            {
                var n = await stream!.ReadAsync(buf.AsMemory(total), rig.Cts.Token);
                if (n == 0) break;
                total += n;
            }
            Assert.Equal(buf.Length, total);
        }

        Assert.False(rig.Run.IsCompleted);
    }

    // ---- discarded queue accounting ----

    private static MuxOptions TightBudget() => new()
    {
        MaxFramePayload = 16 * 1024,
        InitialWindow = 64 * 1024,
        MaxSessionBufferedBytes = 64 * 1024, // exactly one stream's window
    };

    /// <summary>
    /// A stream that is reset with data still queued must give its session budget back.
    /// </summary>
    /// <remarks>
    /// Otherwise a peer fills a stream inside its own receive window, resets it, and repeats on
    /// fresh ids until the whole session budget is permanently consumed. Nothing looks wrong until
    /// a later, entirely compliant stream is the one that tears the VM session down. The budget
    /// was added to stop a denial of service and, unreleased, becomes one.
    /// </remarks>
    [Fact]
    public async Task ResetWithUnreadDataReturnsSessionBudget()
    {
        await using var rig = await RawPeerAsync(options: TightBudget());

        // Five rounds, each consuming the entire session budget and then discarding it.
        foreach (uint id in new uint[] { 1, 3, 5, 7, 9 })
        {
            await rig.Raw.WriteAsync(Frame(id, Syn, 0));
            for (var i = 0; i < 4; i++)
                await rig.Raw.WriteAsync(Frame(id, Data, new byte[16 * 1024]));
            await rig.Raw.WriteAsync(Frame(id, Rst, 0));
        }

        // A compliant stream afterwards must still be able to use the full budget.
        await rig.Raw.WriteAsync(Frame(11, Syn, 0));
        for (var i = 0; i < 4; i++)
            await rig.Raw.WriteAsync(Frame(11, Data, new byte[16 * 1024]));

        var survivor = await AcceptStreamWithIdAsync(rig, 11u);
        var buf = new byte[64 * 1024];
        await survivor.ReadExactlyAsync(buf, rig.Cts.Token);

        Assert.False(rig.Run.IsCompleted, "the session must survive resets that discarded queued data");
    }

    /// <summary>Disposing a stream locally while data is still queued leaks the same way a reset does.</summary>
    [Fact]
    public async Task LocalDisposeWithUnreadDataReturnsSessionBudget()
    {
        await using var rig = await RawPeerAsync(options: TightBudget());

        foreach (uint id in new uint[] { 1, 3, 5, 7, 9 })
        {
            await rig.Raw.WriteAsync(Frame(id, Syn, 0));
            for (var i = 0; i < 4; i++)
                await rig.Raw.WriteAsync(Frame(id, Data, new byte[16 * 1024]));

            // Accept and drop it without reading a byte.
            var s = await AcceptStreamWithIdAsync(rig, id);
            await s.DisposeAsync();
        }

        await rig.Raw.WriteAsync(Frame(11, Syn, 0));
        for (var i = 0; i < 4; i++)
            await rig.Raw.WriteAsync(Frame(11, Data, new byte[16 * 1024]));

        var survivor = await AcceptStreamWithIdAsync(rig, 11u);
        var buf = new byte[64 * 1024];
        await survivor.ReadExactlyAsync(buf, rig.Cts.Token);

        Assert.False(rig.Run.IsCompleted, "the session must survive local disposal of unread streams");
    }

    /// <summary>Accepts inbound streams until the wanted id appears, so tests need not track order.</summary>
    private static async Task<MuxStream> AcceptStreamWithIdAsync(Rig rig, uint id)
    {
        while (true)
        {
            var s = await rig.Victim.AcceptStreamAsync(rig.Cts.Token);
            Assert.NotNull(s);
            if (s!.Id == id) return s;
        }
    }

    // ---- id space ----

    [Fact]
    public void Options_RejectSessionBudgetSmallerThanOneWindow()
    {
        var bad = new MuxOptions { InitialWindow = 256 * 1024, MaxSessionBufferedBytes = 1024 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new MuxSession(new DeadWriteStream(), true, bad));
    }
}

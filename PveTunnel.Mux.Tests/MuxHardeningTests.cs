using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PveTunnel.Mux;
using Xunit;

namespace PveTunnel.Mux.Tests;

/// <summary>
/// Adversarial tests. Each one plays the part of a compromised guest bridge writing hand-built
/// frames at a real <see cref="MuxSession"/>, which is the threat model the Ludus integration
/// assumes: a trainee with local administrator rights can replace the guest bridge with anything.
/// The required outcome is always the same shape -- the offending session dies with
/// <see cref="MuxProtocolException"/>, and nothing else does.
/// </summary>
public class MuxHardeningTests
{
    private const int TimeoutSeconds = 15;

    /// <summary>
    /// A raw TCP peer wired to a real MuxSession. The victim defaults to the server role, so the
    /// ids it will accept from us are the odd ones.
    /// </summary>
    private sealed record Rig(NetworkStream Raw, MuxSession Victim, Task Run, CancellationTokenSource Cts)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Run; } catch { /* the point of most of these tests */ }
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
        var victim = new MuxSession(victimTcp.GetStream(), victimIsClient, options);
        var run = victim.RunAsync(cts.Token);
        return new Rig(attacker.GetStream(), victim, run, cts);
    }

    /// <summary>
    /// Builds a frame. <paramref name="declaredLength"/> is written to the header verbatim and is
    /// deliberately allowed to disagree with the actual payload, which is how the oversize and
    /// wrapped-length cases are expressed.
    /// </summary>
    private static byte[] Frame(uint id, byte type, uint declaredLength, byte[]? payload = null)
    {
        var body = payload ?? Array.Empty<byte>();
        var buf = new byte[Protocol9 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, id);
        buf[4] = type;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(5), declaredLength);
        body.CopyTo(buf, Protocol9);
        return buf;
    }

    private const int Protocol9 = 9;
    private const byte Syn = 1, Data = 2, Fin = 3, Rst = 4, Window = 5;

    private static byte[] Frame(uint id, byte type, byte[] payload)
        => Frame(id, type, (uint)payload.Length, payload);

    // ---- frame-length validation ----

    [Fact]
    public async Task DataFrame_LargerThanMaxPayload_KillsSession()
    {
        await using var rig = await RawPeerAsync();
        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        await rig.Raw.WriteAsync(Frame(1, Data, 64 * 1024)); // limit is 16 KiB

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("limit", ex.Message);
    }

    /// <summary>
    /// Regression for the original defect: the header length was cast straight from uint to int,
    /// so 0x80000000 became negative, the payload read was skipped, and the declared payload bytes
    /// were then misparsed as the next frame header. Silent desync rather than a clean failure.
    /// </summary>
    [Fact]
    public async Task DataFrame_LengthWithHighBitSet_KillsSessionRatherThanDesyncing()
    {
        await using var rig = await RawPeerAsync();
        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        await rig.Raw.WriteAsync(Frame(1, Data, 0x8000_0000u));

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("2147483648", ex.Message);
    }

    [Fact]
    public async Task DataFrame_LengthUintMax_KillsSession()
    {
        await using var rig = await RawPeerAsync();
        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        await rig.Raw.WriteAsync(Frame(1, Data, uint.MaxValue));

        await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
    }

    // ---- frame type and control payload validation ----

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)6)]
    [InlineData((byte)200)]
    [InlineData((byte)255)]
    public async Task UnknownFrameType_KillsSession(byte type)
    {
        await using var rig = await RawPeerAsync();
        await rig.Raw.WriteAsync(Frame(1, type, 0));

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("unknown frame type", ex.Message);
    }

    [Theory]
    [InlineData(Syn)]
    [InlineData(Fin)]
    [InlineData(Rst)]
    public async Task ControlFrameCarryingPayload_KillsSession(byte type)
    {
        await using var rig = await RawPeerAsync();
        await rig.Raw.WriteAsync(Frame(1, type, new byte[] { 0xAA, 0xBB }));

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("expected 0", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public async Task WindowFrameWithWrongPayloadSize_KillsSession(int size)
    {
        await using var rig = await RawPeerAsync();
        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        await rig.Raw.WriteAsync(Frame(1, Window, new byte[size]));

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("expected 4", ex.Message);
    }

    // ---- stream id validation ----

    /// <summary>
    /// The parity rule is what stops a guest reaching into the relay id space. Once the relay opens
    /// server-initiated (even) streams for controller WinRM ingress, a guest that could SYN an even
    /// id would be able to collide with a management stream the relay is about to allocate.
    /// </summary>
    [Theory]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(1000u)]
    public async Task PeerOpeningStreamWithWrongParity_KillsSession(uint id)
    {
        await using var rig = await RawPeerAsync(victimIsClient: false); // victim accepts odd ids only
        await rig.Raw.WriteAsync(Frame(id, Syn, 0));

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("parity", ex.Message);
    }

    [Fact]
    public async Task PeerOpeningStreamZero_KillsSession()
    {
        await using var rig = await RawPeerAsync();
        await rig.Raw.WriteAsync(Frame(0, Syn, 0));

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("parity", ex.Message);
    }

    [Fact]
    public async Task DuplicateSyn_KillsSession()
    {
        await using var rig = await RawPeerAsync();
        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        await rig.Raw.WriteAsync(Frame(1, Syn, 0));

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("duplicate SYN", ex.Message);
    }

    // ---- resource limits ----

    [Fact]
    public async Task PeerExceedingReceiveWindow_KillsSession()
    {
        var options = new MuxOptions { MaxFramePayload = 16 * 1024, InitialWindow = 64 * 1024 };
        await using var rig = await RawPeerAsync(options: options);

        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        // Nothing reads this stream, so no credit is ever granted. Five full frames overrun a
        // four-frame window.
        for (var i = 0; i < 5; i++)
            await rig.Raw.WriteAsync(Frame(1, Data, new byte[16 * 1024]));

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("granted", ex.Message);
    }

    /// <summary>
    /// The byte window alone would let a peer turn a large window into a huge number of tiny
    /// allocations, so frame count is bounded separately.
    /// </summary>
    [Fact]
    public async Task PeerQueueingTooManyTinyFrames_KillsSession()
    {
        var options = new MuxOptions
        {
            MaxFramePayload = 16 * 1024,
            InitialWindow = 1024 * 1024, // deliberately generous so the byte window is not what trips
            MaxBufferedChunks = 8,
        };
        await using var rig = await RawPeerAsync(options: options);

        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        for (var i = 0; i < 20; i++)
            await rig.Raw.WriteAsync(Frame(1, Data, new byte[] { 0x41 }));

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => rig.Run);
        Assert.Contains("unread frames", ex.Message);
    }

    /// <summary>
    /// A SYN flood past the limit is refused per stream with RST, not by killing the session:
    /// running out of stream slots is a capacity condition, not evidence of a malformed peer.
    /// </summary>
    [Fact]
    public async Task SynBeyondStreamLimit_IsRefusedWithRstAndSessionSurvives()
    {
        var options = new MuxOptions { MaxConcurrentStreams = 2 };
        await using var rig = await RawPeerAsync(options: options);

        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        await rig.Raw.WriteAsync(Frame(3, Syn, 0));
        await rig.Raw.WriteAsync(Frame(5, Syn, 0)); // one too many

        var header = new byte[Protocol9];
        await rig.Raw.ReadExactlyAsync(header, rig.Cts.Token);

        Assert.Equal(5u, BinaryPrimitives.ReadUInt32BigEndian(header));
        Assert.Equal(Rst, header[4]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5)));
        Assert.False(rig.Run.IsCompleted, "the session must survive a refused stream");
    }

    // ---- isolation ----

    /// <summary>
    /// The relay hosts one session per VM. A guest that corrupts its own session must not disturb
    /// another VM, which is the whole basis for putting Ansible management on this transport.
    /// </summary>
    [Fact]
    public async Task MalformedSession_DoesNotDisturbAnotherSession()
    {
        await using var hostile = await RawPeerAsync();
        await using var innocent = await RawPeerAsync();

        await hostile.Raw.WriteAsync(Frame(1, 99, 0)); // unknown type
        await Assert.ThrowsAsync<MuxProtocolException>(() => hostile.Run);

        // The bystander is still live and still processing frames.
        Assert.False(innocent.Run.IsCompleted);
        await innocent.Raw.WriteAsync(Frame(1, Syn, 0));
        var accepted = await innocent.Victim.AcceptStreamAsync(innocent.Cts.Token);
        Assert.NotNull(accepted);
        Assert.Equal(1u, accepted!.Id);
    }

    // ---- cancellation ----

    [Fact]
    public async Task Cancellation_EndsReadLoopCleanly()
    {
        await using var rig = await RawPeerAsync();
        await rig.Raw.WriteAsync(Frame(1, Syn, 0));

        rig.Cts.Cancel();

        // Cancellation is a normal shutdown, not a protocol failure.
        await rig.Run;
        Assert.True(rig.Run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task TransportClosingMidFrame_EndsReadLoopCleanly()
    {
        await using var rig = await RawPeerAsync();

        // A header promising a payload, then the transport dies before the payload arrives.
        await rig.Raw.WriteAsync(Frame(1, Syn, 0));
        await rig.Raw.WriteAsync(Frame(1, Data, 4096, new byte[16]));
        rig.Raw.Socket.Shutdown(SocketShutdown.Send);

        await rig.Run; // EndOfStreamException is swallowed as a clean close
        Assert.True(rig.Run.IsCompletedSuccessfully);
    }

    // ---- fuzz ----

    /// <summary>
    /// Property: for any byte sequence, the parser either consumes it or fails with
    /// MuxProtocolException. It must never allocate on an unvalidated length, never surface an
    /// exception type the relay is not prepared to catch, and never hang.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Fuzz_ArbitraryFrames_FailCleanlyOrNotAtAll(int seed)
    {
        var rng = new Random(seed);
        await using var rig = await RawPeerAsync();

        try
        {
            for (var i = 0; i < 200; i++)
            {
                var id = (uint)rng.Next(0, 16);
                var type = (byte)rng.Next(0, 256);
                var declared = rng.Next(0, 4) switch
                {
                    0 => (uint)rng.Next(0, 32),
                    1 => (uint)rng.Next(0, int.MaxValue),
                    2 => 0x8000_0000u + (uint)rng.Next(0, 1000),
                    _ => uint.MaxValue,
                };
                var payload = new byte[rng.Next(0, 32)];
                rng.NextBytes(payload);
                await rig.Raw.WriteAsync(Frame(id, type, declared, payload));
            }
            rig.Raw.Socket.Shutdown(SocketShutdown.Send);
            await rig.Run;
        }
        catch (MuxProtocolException) { /* the expected failure mode */ }
        catch (IOException) { /* transport torn down by the victim; also acceptable */ }

        Assert.True(rig.Run.IsCompleted, "the read loop must not hang on malformed input");
    }
}

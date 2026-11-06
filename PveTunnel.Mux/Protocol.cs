using System.Buffers.Binary;

namespace PveTunnel.Mux;

/// <summary>
/// Frame type byte. The mux carries multiple logical streams over one byte-stream transport
/// (the virtio-serial port). Wire layout per frame is a fixed 9-byte header:
///   [streamId : uint32 big-endian][type : 1 byte][length : uint32 big-endian][payload : length bytes]
/// </summary>
public enum FrameType : byte
{
    /// <summary>Open a new stream (no payload).</summary>
    Syn = 1,

    /// <summary>Stream data (payload = bytes).</summary>
    Data = 2,

    /// <summary>Half-close: sender will send no more data on this stream (no payload).</summary>
    Fin = 3,

    /// <summary>Abort the stream immediately (no payload).</summary>
    Rst = 4,

    /// <summary>Flow-control credit. Payload = uint32 big-endian: bytes the peer may now send.</summary>
    Window = 5,
}

internal static class Protocol
{
    public const int HeaderSize = 9;

    /// <summary>Exact payload size of a WINDOW frame.</summary>
    public const int WindowPayloadSize = 4;

    public static void WriteHeader(Span<byte> header, uint streamId, FrameType type, int length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(header, streamId);
        header[4] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(5), (uint)length);
    }

    /// <summary>
    /// Parses a header without trusting any of it. The length is returned as the raw
    /// <see cref="uint"/> it is on the wire, deliberately <b>not</b> cast to <see cref="int"/>:
    /// casting here is how a peer-supplied 0x80000000 becomes a negative length that the read
    /// loop then skips, leaving the declared payload in the transport to be misread as the next
    /// header. The caller must bound the length before allocating. The type is likewise returned
    /// as a raw byte, because a value outside <see cref="FrameType"/> must be rejected rather
    /// than silently ignored by a switch with no default arm.
    /// </summary>
    public static void ParseHeader(ReadOnlySpan<byte> header, out uint streamId, out byte rawType, out uint length)
    {
        streamId = BinaryPrimitives.ReadUInt32BigEndian(header);
        rawType = header[4];
        length = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(5));
    }

    /// <summary>Payload size a control frame is allowed to carry, or -1 if the type is not a control frame.</summary>
    public static int ExpectedControlPayload(FrameType type) => type switch
    {
        FrameType.Syn => 0,
        FrameType.Fin => 0,
        FrameType.Rst => 0,
        FrameType.Window => WindowPayloadSize,
        _ => -1,
    };

    public static bool IsKnown(byte rawType) =>
        rawType is >= (byte)FrameType.Syn and <= (byte)FrameType.Window;
}

using System.Text;

namespace Musicthingy.Services.Sabr;

// ────────────────────────────────────────────────────────────────────────────
// SABR protocol primitives — minimal protobuf wire format + UMP framing.
//
// No external NuGet packages. The SABR client only needs a handful of
// well-understood message types (see Docs/SABR_Research.md for the full
// field tables and the sources they were verified against).
//
// Protobuf encoding (standard wire format, as used by every YouTube proto):
//   tag  = (field_number << 3) | wire_type
//   wire type 0: varint   (int32 / int64 / uint64 / bool / enum)
//   wire type 2: length-delimited (string / bytes / submessage)
//
// UMP ("application/vnd.yt-ump", the SABR response container) is a stream of
// parts, each framed as
//   [part_type: UMP varint][part_size: UMP varint][part_data: part_size bytes]
// UMP varints are NOT protobuf varints: the leading bits of the first byte
// declare the width (0xxxxxxx = 1 byte, 10xxxxxx = 2, 110xxxxx = 3,
// 1110xxxx = 4, 11110xxx = 5) and the value is big-endian over that width
// (the 5-byte form stores a plain little-endian 32-bit value in bytes 1-4).
// ────────────────────────────────────────────────────────────────────────────

/// <summary>One UMP part: its type id and the fully-read part data.</summary>
internal sealed record UmpPart(int Type, byte[] Data)
{
    public const int MediaHeader = 20;
    public const int Media = 21;
    public const int MediaEnd = 22;
    public const int LiveMetadata = 31;
    public const int NextRequestPolicy = 35;
    public const int FormatInitializationMetadata = 42;
    public const int SabrRedirect = 43;
    public const int SabrError = 44;
    public const int SabrSeek = 45;
    public const int ReloadPlayerResponse = 46;
    public const int SabrContextUpdate = 57;
    public const int StreamProtectionStatus = 58;
    public const int SabrContextSendingPolicy = 59;
    public const int CuepointList = 69;
}

/// <summary>UMP varint read/write (see the framing note above).</summary>
internal static class UmpVarint
{
    public static int Size(byte firstByte)
    {
        if (firstByte < 0x80) return 1;
        if (firstByte < 0xC0) return 2;
        if (firstByte < 0xE0) return 3;
        if (firstByte < 0xF0) return 4;
        if (firstByte < 0xF8) return 5;
        throw new InvalidDataException($"Invalid UMP varint width byte 0x{firstByte:X2}");
    }

    /// <summary>Reads one UMP varint from the stream. Returns -1 on clean EOF.</summary>
    public static long Read(Stream stream)
    {
        int first = stream.ReadByte();
        if (first < 0) return -1;

        int size = Size((byte)first);
        long result;
        int shift;

        if (size == 5)
        {
            result = 0;
            shift = 0;
        }
        else
        {
            shift = 8 - size;
            result = first & ((1 << shift) - 1);
        }

        for (int i = 1; i < size; i++)
        {
            int b = stream.ReadByte();
            if (b < 0) return -1;
            result |= (long)b << shift;
            shift += 8;
        }

        return result;
    }
}

/// <summary>Reads a UMP response stream part by part (each part fully buffered).</summary>
internal sealed class UmpPartReader(Stream stream)
{
    // A single media segment is at most a few hundred KB; anything beyond
    // this is a protocol violation (protects against absurd allocations).
    private const long MaxPartSize = 32 * 1024 * 1024;

    public async Task<UmpPart?> ReadNextAsync(CancellationToken ct)
    {
        long type = await ReadVarintByteByByteAsync(ct).ConfigureAwait(false);
        if (type < 0) return null; // clean end of the response

        long size = await ReadVarintByteByByteAsync(ct).ConfigureAwait(false);
        if (size < 0) throw new EndOfStreamException("EOF while reading UMP part size");
        if (size > MaxPartSize) throw new InvalidDataException($"UMP part too large: {size} bytes");

        byte[] data = new byte[size];
        int read = 0;
        while (read < data.Length)
        {
            int n = await stream.ReadAsync(data.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException($"EOF inside UMP part (expected {size}, got {read})");
            read += n;
        }

        return new UmpPart((int)type, data);
    }

    private async Task<long> ReadVarintByteByByteAsync(CancellationToken ct)
    {
        int first = await ReadByteAsync(stream, ct).ConfigureAwait(false);
        if (first < 0) return -1;

        int size = UmpVarint.Size((byte)first);
        long result;
        int shift;

        if (size == 5)
        {
            result = 0;
            shift = 0;
        }
        else
        {
            shift = 8 - size;
            result = first & ((1 << shift) - 1);
        }

        for (int i = 1; i < size; i++)
        {
            int b = await ReadByteAsync(stream, ct).ConfigureAwait(false);
            if (b < 0) return -1;
            result |= (long)b << shift;
            shift += 8;
        }

        return result;
    }

    /// <summary>Single-byte read (the underlying HTTP stream is buffered).</summary>
    private static async Task<int> ReadByteAsync(Stream s, CancellationToken ct)
    {
        byte[] one = new byte[1];
        int n = await s.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
        return n <= 0 ? -1 : one[0];
    }
}

/// <summary>Minimal protobuf message writer (fields are written explicitly, proto2-style).</summary>
internal sealed class SabrWriter
{
    private readonly MemoryStream _ms = new();

    public void VarintField(int field, long value)
    {
        Tag(field, 0);
        WriteVarint(unchecked((ulong)value));
    }

    public void BoolField(int field, bool value)
    {
        Tag(field, 0);
        WriteVarint(value ? 1UL : 0UL);
    }

    public void BytesField(int field, byte[] value)
    {
        Tag(field, 2);
        WriteVarint((ulong)value.Length);
        _ms.Write(value, 0, value.Length);
    }

    public void StringField(int field, string value) =>
        BytesField(field, Encoding.UTF8.GetBytes(value));

    public void SubMessage(int field, Action<SabrWriter> write)
    {
        SabrWriter sub = new();
        write(sub);
        BytesField(field, sub.ToArray());
    }

    public byte[] ToArray() => _ms.ToArray();

    private void Tag(int field, int wireType) => WriteVarint((ulong)((field << 3) | wireType));

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _ms.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _ms.WriteByte((byte)value);
    }
}

/// <summary>One decoded protobuf entry (a field occurrence).</summary>
internal readonly record struct SabrEntry(int Field, int WireType, ulong Varint, byte[]? Payload)
{
    public long AsInt64 => unchecked((long)Varint);
    public int AsInt32 => unchecked((int)Varint);
    public ulong AsUInt64 => Varint;
    public bool AsBool => Varint != 0;
    public string AsString => Payload is null ? "" : Encoding.UTF8.GetString(Payload);
    public byte[] AsBytes => Payload ?? Array.Empty<byte>();
}

/// <summary>Iterates the entries of one protobuf message (a byte range).</summary>
internal sealed class SabrReader(byte[] data, int offset, int count)
{
    private readonly byte[] _buf = data;
    private int _pos = offset;
    private readonly int _end = offset + count;

    public SabrReader(byte[] data) : this(data, 0, data.Length) { }

    public bool HasNext => _pos < _end;

    public SabrEntry Next()
    {
        ulong tag = ReadVarint();
        int field = (int)(tag >> 3);
        switch (tag & 7)
        {
            case 0:
                return new SabrEntry(field, 0, ReadVarint(), null);
            case 1: // fixed64
                return new SabrEntry(field, 1, 0, ReadRaw(8));
            case 2:
            {
                ulong len = ReadVarint();
                if (len > (ulong)(_end - _pos))
                    throw new EndOfStreamException($"protobuf field {field} length {len} exceeds buffer");
                return new SabrEntry(field, 2, 0, ReadRaw((int)len));
            }
            case 5: // fixed32
                return new SabrEntry(field, 5, 0, ReadRaw(4));
            default:
                throw new NotSupportedException($"protobuf wire type {tag & 7} (field {field})");
        }
    }

    /// <summary>A reader over a length-delimited payload (submessage).</summary>
    public static SabrReader Of(SabrEntry entry) =>
        new(entry.Payload ?? Array.Empty<byte>());

    private ulong ReadVarint()
    {
        if (_pos >= _end) throw new EndOfStreamException("protobuf varint past end");
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            if (_pos >= _end) throw new EndOfStreamException("protobuf varint truncated");
            byte b = _buf[_pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("protobuf varint too long");
        }
    }

    private byte[] ReadRaw(int count)
    {
        if (count < 0 || _end - _pos < count) throw new EndOfStreamException("protobuf payload truncated");
        byte[] result = new byte[count];
        Array.Copy(_buf, _pos, result, 0, count);
        _pos += count;
        return result;
    }
}

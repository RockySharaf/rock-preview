using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;

namespace Anything.Core.Content.Store;

/// <summary>
/// LEB128 varints and length-prefixed UTF-8 strings for the segment and manifest formats. Readers never throw: they return
/// false on a truncated, overlong or out-of-range value, and the caller marks the data damaged.
/// </summary>
internal static class VarInt
{
    /// <summary>The longest varint of a 64-bit value.</summary>
    public const int MaxLength = 10;

    /// <summary>Bytes the value takes.</summary>
    public static int Size(ulong value)
    {
        int n = 1;
        while (value >= 0x80) { value >>= 7; n++; }
        return n;
    }

    /// <summary>Writes the value; returns the bytes written (dest needs <see cref="MaxLength"/> free bytes at most).</summary>
    public static int Write(Span<byte> dest, ulong value)
    {
        int i = 0;
        while (value >= 0x80)
        {
            dest[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        dest[i++] = (byte)value;
        return i;
    }

    /// <summary>Reads a value at pos and moves past it; false when truncated or longer than 64 bits.</summary>
    public static bool TryRead(ReadOnlySpan<byte> s, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        for (int i = pos; i < s.Length; i++)
        {
            byte b = s[i];
            if (shift == 63 && b > 1) return false;
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                pos = i + 1;
                return true;
            }
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }

    /// <summary>Reads a value that must be at most max.</summary>
    public static bool TryRead(ReadOnlySpan<byte> s, ref int pos, long max, out long value)
    {
        if (TryRead(s, ref pos, out ulong v) && v <= (ulong)max)
        {
            value = (long)v;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>Reads a value that must be at most max (an int).</summary>
    public static bool TryRead(ReadOnlySpan<byte> s, ref int pos, int max, out int value)
    {
        if (TryRead(s, ref pos, out ulong v) && v <= (ulong)Math.Max(max, 0))
        {
            value = (int)v;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>Reads a length-prefixed UTF-8 string of at most maxBytes; false when it is truncated or not valid UTF-8.</summary>
    public static bool TryReadString(ReadOnlySpan<byte> s, ref int pos, int maxBytes, out string value)
    {
        value = "";
        int p = pos;
        if (!TryRead(s, ref p, maxBytes, out int length) || length > s.Length - p) return false;
        var bytes = s.Slice(p, length);
        if (!Utf8.IsValid(bytes)) return false;
        value = Encoding.UTF8.GetString(bytes);
        pos = p + length;
        return true;
    }
}

/// <summary>A growable byte buffer on pooled arrays (section and block building); Dispose returns the array.</summary>
internal sealed class ByteBuffer : IDisposable
{
    byte[] _buffer;
    int _length;

    public ByteBuffer(int capacity = 256) => _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(16, capacity));

    /// <summary>Bytes written.</summary>
    public int Length => _length;
    /// <summary>The bytes written so far (valid until the next write).</summary>
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);
    /// <summary>The backing array (first Length bytes used).</summary>
    public byte[] Array => _buffer;

    public void Clear() => _length = 0;

    /// <summary>Room for count more bytes; returns the span to write them into (call Advance after).</summary>
    public Span<byte> Reserve(int count)
    {
        if (_length + count > _buffer.Length)
        {
            long want = Math.Max((long)_buffer.Length * 2, (long)_length + count);
            if (want > System.Array.MaxLength) want = Math.Max((long)_length + count, System.Array.MaxLength);
            var bigger = ArrayPool<byte>.Shared.Rent((int)want);
            _buffer.AsSpan(0, _length).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }
        return _buffer.AsSpan(_length, count);
    }

    public void Advance(int count) => _length += count;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Reserve(bytes.Length));
        _length += bytes.Length;
    }

    public void WriteByte(byte b)
    {
        Reserve(1)[0] = b;
        _length++;
    }

    public void WriteVarInt(ulong value) => _length += VarInt.Write(Reserve(VarInt.MaxLength), value);

    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);
        _length += 2;
    }

    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);
        _length += 4;
    }

    public void WriteInt64(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);
        _length += 8;
    }

    /// <summary>Writes a varint length and the UTF-8 bytes.</summary>
    public void WriteString(string value)
    {
        int n = Encoding.UTF8.GetByteCount(value);
        WriteVarInt((ulong)n);
        _length += Encoding.UTF8.GetBytes(value, Reserve(n));
    }

    public void Dispose()
    {
        var b = _buffer;
        _buffer = [];
        _length = 0;
        if (b.Length > 0) ArrayPool<byte>.Shared.Return(b);
    }
}

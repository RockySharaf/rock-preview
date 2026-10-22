using System.Buffers.Binary;
using System.Numerics;

namespace Anything.Core.Content.Store;

/// <summary>
/// CRC32C (Castagnoli, the hardware BitOperations.Crc32C) over file sections, TOCs and dictionary blocks. A running state
/// starts at <see cref="Start"/>, takes bytes through <see cref="Update"/> and ends with <see cref="Finish"/>.
/// </summary>
internal static class Crc32C
{
    /// <summary>The state before any byte.</summary>
    public const uint Start = ~0u;

    /// <summary>The CRC of the bytes.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(Start, data));

    /// <summary>Feeds bytes into a running state.</summary>
    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        int i = 0;
        for (; i + 8 <= data.Length; i += 8) state = BitOperations.Crc32C(state, BinaryPrimitives.ReadUInt64LittleEndian(data[i..]));
        for (; i < data.Length; i++) state = BitOperations.Crc32C(state, data[i]);
        return state;
    }

    /// <summary>The CRC of a running state.</summary>
    public static uint Finish(uint state) => ~state;
}

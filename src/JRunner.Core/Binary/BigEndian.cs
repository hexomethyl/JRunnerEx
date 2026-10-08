using System.Buffers.Binary;

namespace JRunner.Core.Binary;

/// <summary>
/// Reads, writes, and slices fixed-width big-endian binary fields with explicit bounds checks.
/// </summary>
public static class BigEndian
{
    /// <summary>
    /// The encoded size of an unsigned 16-bit field.
    /// </summary>
    public const int UInt16Size = 2;

    /// <summary>
    /// The encoded size of an unsigned 32-bit field.
    /// </summary>
    public const int UInt32Size = 4;

    /// <summary>
    /// Reads an unsigned 16-bit big-endian value from the start of <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The source bytes containing at least two bytes.</param>
    /// <returns>The decoded value.</returns>
    public static ushort ReadUInt16(ReadOnlySpan<byte> source)
    {
        return ReadUInt16(source, 0);
    }

    /// <summary>
    /// Reads an unsigned 16-bit big-endian value at <paramref name="offset"/>.
    /// </summary>
    /// <param name="source">The source bytes containing the requested field.</param>
    /// <param name="offset">The zero-based offset of the field.</param>
    /// <returns>The decoded value.</returns>
    public static ushort ReadUInt16(ReadOnlySpan<byte> source, int offset)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(SliceExact(source, offset, UInt16Size));
    }

    /// <summary>
    /// Reads an unsigned 32-bit big-endian value from the start of <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The source bytes containing at least four bytes.</param>
    /// <returns>The decoded value.</returns>
    public static uint ReadUInt32(ReadOnlySpan<byte> source)
    {
        return ReadUInt32(source, 0);
    }

    /// <summary>
    /// Reads an unsigned 32-bit big-endian value at <paramref name="offset"/>.
    /// </summary>
    /// <param name="source">The source bytes containing the requested field.</param>
    /// <param name="offset">The zero-based offset of the field.</param>
    /// <returns>The decoded value.</returns>
    public static uint ReadUInt32(ReadOnlySpan<byte> source, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(SliceExact(source, offset, UInt32Size));
    }

    /// <summary>
    /// Writes an unsigned 16-bit big-endian value at the start of <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The destination bytes containing at least two bytes.</param>
    /// <param name="value">The value to encode.</param>
    public static void WriteUInt16(Span<byte> destination, ushort value)
    {
        WriteUInt16(destination, 0, value);
    }

    /// <summary>
    /// Writes an unsigned 16-bit big-endian value at <paramref name="offset"/>.
    /// </summary>
    /// <param name="destination">The destination bytes containing the requested field.</param>
    /// <param name="offset">The zero-based offset of the field.</param>
    /// <param name="value">The value to encode.</param>
    public static void WriteUInt16(Span<byte> destination, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(SliceExact(destination, offset, UInt16Size), value);
    }

    /// <summary>
    /// Writes an unsigned 32-bit big-endian value at the start of <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The destination bytes containing at least four bytes.</param>
    /// <param name="value">The value to encode.</param>
    public static void WriteUInt32(Span<byte> destination, uint value)
    {
        WriteUInt32(destination, 0, value);
    }

    /// <summary>
    /// Writes an unsigned 32-bit big-endian value at <paramref name="offset"/>.
    /// </summary>
    /// <param name="destination">The destination bytes containing the requested field.</param>
    /// <param name="offset">The zero-based offset of the field.</param>
    /// <param name="value">The value to encode.</param>
    public static void WriteUInt32(Span<byte> destination, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(SliceExact(destination, offset, UInt32Size), value);
    }

    /// <summary>
    /// Returns precisely <paramref name="length"/> bytes from <paramref name="source"/> beginning
    /// at <paramref name="offset"/>. The request is rejected rather than silently truncated.
    /// </summary>
    /// <param name="source">The source bytes.</param>
    /// <param name="offset">The zero-based offset at which the slice begins.</param>
    /// <param name="length">The exact number of bytes to include.</param>
    /// <returns>The requested slice.</returns>
    public static ReadOnlySpan<byte> SliceExact(ReadOnlySpan<byte> source, int offset, int length)
    {
        ValidateSlice(source.Length, offset, length);
        return source.Slice(offset, length);
    }

    /// <summary>
    /// Returns precisely <paramref name="length"/> writable bytes from <paramref name="source"/>
    /// beginning at <paramref name="offset"/>. The request is rejected rather than silently truncated.
    /// </summary>
    /// <param name="source">The source bytes.</param>
    /// <param name="offset">The zero-based offset at which the slice begins.</param>
    /// <param name="length">The exact number of bytes to include.</param>
    /// <returns>The requested writable slice.</returns>
    public static Span<byte> SliceExact(Span<byte> source, int offset, int length)
    {
        ValidateSlice(source.Length, offset, length);
        return source.Slice(offset, length);
    }

    private static void ValidateSlice(int sourceLength, int offset, int length)
    {
        if (offset < 0 || offset > sourceLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                "The offset must identify a position within the source.");
        }

        if (length < 0 || length > sourceLength - offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "The requested slice extends beyond the end of the source.");
        }
    }
}

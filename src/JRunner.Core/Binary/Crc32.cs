namespace JRunner.Core.Binary;

/// <summary>
/// Calculates the standard reflected CRC-32 checksum used by legacy J-Runner data.
/// </summary>
public static class Crc32
{
    private const uint InitialValue = uint.MaxValue;
    private const uint FinalXorValue = uint.MaxValue;
    private const int StreamBufferSize = 8192;
    private const int CancellationCheckIntervalMask = 0x0FFF;

    private static readonly uint[] LookupTable = CreateLookupTable();

    /// <summary>
    /// Calculates a CRC-32 checksum using initial and final XOR values of <c>0xFFFFFFFF</c>.
    /// </summary>
    /// <param name="data">The bytes to checksum.</param>
    /// <returns>The CRC-32 checksum.</returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        return Compute(data, CancellationToken.None);
    }

    /// <summary>
    /// Calculates a CRC-32 checksum using initial and final XOR values of <c>0xFFFFFFFF</c>.
    /// </summary>
    /// <param name="data">The bytes to checksum.</param>
    /// <param name="cancellationToken">Cancels a large in-memory checksum calculation.</param>
    /// <returns>The CRC-32 checksum.</returns>
    public static uint Compute(ReadOnlySpan<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ApplyFinalXor(Update(InitialValue, data, cancellationToken));
    }

    /// <summary>
    /// Calculates one CRC-32 checksum across three adjacent byte segments without allocating a
    /// concatenated buffer.
    /// </summary>
    /// <param name="first">The first segment.</param>
    /// <param name="second">The second segment.</param>
    /// <param name="third">The third segment.</param>
    /// <returns>The CRC-32 checksum of the concatenated segment contents.</returns>
    public static uint Compute(
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second,
        ReadOnlySpan<byte> third)
    {
        return Compute(first, second, third, CancellationToken.None);
    }

    /// <summary>
    /// Calculates one CRC-32 checksum across three adjacent byte segments without allocating a
    /// concatenated buffer.
    /// </summary>
    /// <param name="first">The first segment.</param>
    /// <param name="second">The second segment.</param>
    /// <param name="third">The third segment.</param>
    /// <param name="cancellationToken">Cancels a large in-memory checksum calculation.</param>
    /// <returns>The CRC-32 checksum of the concatenated segment contents.</returns>
    public static uint Compute(
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second,
        ReadOnlySpan<byte> third,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint crc = Update(InitialValue, first, cancellationToken);
        crc = Update(crc, second, cancellationToken);
        crc = Update(crc, third, cancellationToken);
        return ApplyFinalXor(crc);
    }

    /// <summary>
    /// Calculates a CRC-32 checksum for a stream. A seekable stream is checksummed from its
    /// beginning through its length and is restored to its original position. A non-seekable
    /// stream is checksummed from its current position through end of stream and is consumed.
    /// </summary>
    /// <param name="stream">The readable stream to checksum.</param>
    /// <param name="cancellationToken">Cancels a large stream checksum calculation.</param>
    /// <returns>The CRC-32 checksum.</returns>
    public static uint Compute(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EnsureReadable(stream);
        cancellationToken.ThrowIfCancellationRequested();

        if (!stream.CanSeek)
        {
            return ComputeToEnd(stream, cancellationToken);
        }

        return Compute(stream, 0, stream.Length, cancellationToken);
    }

    /// <summary>
    /// Calculates a CRC-32 checksum for an exact range of a seekable stream and restores the
    /// stream to its original position, including when reading fails or is cancelled.
    /// </summary>
    /// <param name="stream">The readable, seekable stream to checksum.</param>
    /// <param name="offset">The zero-based stream offset at which the range begins.</param>
    /// <param name="length">The number of bytes to checksum.</param>
    /// <param name="cancellationToken">Cancels a large stream checksum calculation.</param>
    /// <returns>The CRC-32 checksum.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the requested range is outside the stream.
    /// </exception>
    public static uint Compute(
        Stream stream,
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EnsureReadable(stream);

        if (!stream.CanSeek)
        {
            throw new NotSupportedException("Computing a stream range requires a seekable stream.");
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset cannot be negative.");
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length cannot be negative.");
        }

        long streamLength = stream.Length;
        if (offset > streamLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                "The offset must identify a position within the stream.");
        }

        if (length > streamLength - offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "The requested range extends beyond the end of the stream.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        long originalPosition = stream.Position;

        try
        {
            stream.Position = offset;
            return ComputeExactLength(stream, length, cancellationToken);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static uint ComputeToEnd(Stream stream, CancellationToken cancellationToken)
    {
        Span<byte> buffer = stackalloc byte[StreamBufferSize];
        uint crc = InitialValue;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesRead = stream.Read(buffer);
            if (bytesRead == 0)
            {
                return ApplyFinalXor(crc);
            }

            crc = Update(crc, buffer[..bytesRead], cancellationToken);
        }
    }

    private static uint ComputeExactLength(Stream stream, long length, CancellationToken cancellationToken)
    {
        Span<byte> buffer = stackalloc byte[StreamBufferSize];
        uint crc = InitialValue;
        long remaining = length;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(remaining, buffer.Length);
            int bytesRead = stream.Read(buffer[..requested]);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("The stream ended before the requested CRC-32 range was read.");
            }

            crc = Update(crc, buffer[..bytesRead], cancellationToken);
            remaining -= bytesRead;
        }

        return ApplyFinalXor(crc);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data, CancellationToken cancellationToken)
    {
        for (int index = 0; index < data.Length; index++)
        {
            if ((index & CancellationCheckIntervalMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            crc = (crc >> 8) ^ LookupTable[(byte)(crc ^ data[index])];
        }

        return crc;
    }

    private static uint ApplyFinalXor(uint crc)
    {
        return crc ^ FinalXorValue;
    }

    private static void EnsureReadable(Stream stream)
    {
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }
    }

    private static uint[] CreateLookupTable()
    {
        var table = new uint[256];

        for (int index = 0; index < table.Length; index++)
        {
            uint value = (uint)index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0
                    ? value >> 1
                    : (value >> 1) ^ 0xEDB88320u;
            }

            table[index] = value;
        }

        return table;
    }
}

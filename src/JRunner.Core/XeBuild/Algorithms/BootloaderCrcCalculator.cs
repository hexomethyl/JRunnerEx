using System.Buffers.Binary;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;

namespace JRunner.Core.XeBuild.Algorithms;

/// <summary>
/// The result of calculating a bootloader CRC using xeBuild's field-masking rules.
/// </summary>
public sealed record BootloaderCrcResult
{
    internal BootloaderCrcResult(
        uint value,
        int processedLength,
        int clearedOffset,
        int clearedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(processedLength);
        ArgumentOutOfRangeException.ThrowIfNegative(clearedOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(clearedLength);

        if (clearedOffset > processedLength - clearedLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clearedOffset),
                clearedOffset,
                "The cleared range must be contained in the processed bootloader range.");
        }

        Value = value;
        ProcessedLength = processedLength;
        ClearedOffset = clearedOffset;
        ClearedLength = clearedLength;
    }

    /// <summary>
    /// Gets the reflected CRC-32 value after xeBuild's mutable fields are cleared.
    /// </summary>
    public uint Value { get; }

    /// <summary>
    /// Gets the declared number of bootloader bytes covered by the calculation.
    /// </summary>
    public int ProcessedLength { get; }

    /// <summary>
    /// Gets the start of the cleared range, or zero when no type-specific range is cleared.
    /// </summary>
    public int ClearedOffset { get; }

    /// <summary>
    /// Gets the number of type-specific bytes cleared before calculation.
    /// </summary>
    public int ClearedLength { get; }
}

/// <summary>
/// Calculates the CRC-32 values xeBuild uses to validate bootloader files.
/// </summary>
public static class BootloaderCrcCalculator
{
    private const int HeaderLength = 0x10;

    /// <summary>
    /// Calculates the bootloader CRC after masking its mutable header fields and honoring its
    /// big-endian declared size at offset <c>0xC</c>.
    /// </summary>
    /// <remarks>
    /// CB/SB bootloaders mask <c>[0x10, 0x40)</c>; SC, CD/SD, CE/SE, and CG mask
    /// <c>[0x10, 0x20)</c>; CF masks <c>[0x20, 0x230)</c>. Other bootloader identifiers
    /// have no xeBuild-specific masked range.
    /// </remarks>
    /// <exception cref="OperationFailureException">
    /// Thrown when the bootloader header, declared size, or required masked range is truncated.
    /// </exception>
    public static BootloaderCrcResult Calculate(
        ReadOnlySpan<byte> bootloader,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (bootloader.Length < HeaderLength)
        {
            throw InvalidData(
                "truncated-bootloader-data",
                "The bootloader is shorter than its 0x10-byte header.");
        }

        var declaredLengthValue = BinaryPrimitives.ReadUInt32BigEndian(bootloader.Slice(0xC, sizeof(uint)));
        if (declaredLengthValue < HeaderLength)
        {
            throw InvalidData(
                "invalid-bootloader-length",
                "The bootloader declared size is shorter than its 0x10-byte header.");
        }

        if (declaredLengthValue > (uint)bootloader.Length)
        {
            throw InvalidData(
                "truncated-bootloader-data",
                "The bootloader is shorter than its declared size.");
        }

        var declaredLength = checked((int)declaredLengthValue);
        var (clearedOffset, clearedLength) = GetClearedRange(bootloader[1]);
        if (clearedLength > 0 &&
            (bootloader.Length < checked(clearedOffset + clearedLength) ||
             declaredLength < checked(clearedOffset + clearedLength)))
        {
            throw InvalidData(
                "truncated-bootloader-data",
                "The bootloader does not contain the complete mutable range required for CRC calculation.");
        }

        var clearedEnd = checked(clearedOffset + clearedLength);
        uint value;
        if (clearedLength == 0)
        {
            value = Crc32.Compute(
                bootloader.Slice(0, declaredLength),
                ReadOnlySpan<byte>.Empty,
                ReadOnlySpan<byte>.Empty,
                cancellationToken);
        }
        else
        {
            Span<byte> clearedBytes = stackalloc byte[clearedLength];
            clearedBytes.Clear();
            value = Crc32.Compute(
                bootloader.Slice(0, clearedOffset),
                clearedBytes,
                bootloader.Slice(clearedEnd, declaredLength - clearedEnd),
                cancellationToken);
        }

        return new BootloaderCrcResult(value, declaredLength, clearedOffset, clearedLength);
    }

    private static (int Offset, int Length) GetClearedRange(byte bootloaderType)
    {
        return bootloaderType switch
        {
            0x42 => (0x10, 0x30),
            0x43 or 0x44 or 0x45 or 0x47 => (0x10, 0x10),
            0x46 => (0x20, 0x210),
            _ => (0, 0),
        };
    }


    private static OperationFailureException InvalidData(string kind, string message)
    {
        return new OperationFailureException(ExitCode.InvalidData, kind, message);
    }
}

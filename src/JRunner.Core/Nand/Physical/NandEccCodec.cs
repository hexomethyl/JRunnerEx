using System.Buffers.Binary;
using JRunner.Core.Contracts;

namespace JRunner.Core.Nand.Physical;

/// <summary>
/// Converts between logical NAND pages and legacy interleaved data-and-spare pages.
/// </summary>
public static class NandEccCodec
{
    private const int EccSize = sizeof(uint);
    private const uint PreservedSpareBitsMask = 0x3FU;
    private const int LegacyEccCalculationBitCount = 0x1066;

    /// <summary>
    /// Adds legacy spare data and ECC to complete logical pages.
    /// </summary>
    /// <param name="logicalData">Complete <c>0x200</c>-byte logical pages.</param>
    /// <param name="layout">The physical spare-area layout to emit.</param>
    /// <param name="firstPhysicalBlock">The physical block number represented by the first input page.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Token checked before every output page.</param>
    /// <returns>New interleaved <c>0x210</c>-byte physical pages.</returns>
    public static ReadOnlyMemory<byte> AddEcc(
        ReadOnlySpan<byte> logicalData,
        NandPhysicalLayout layout,
        long firstPhysicalBlock = 0,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ValidateFirstPhysicalBlock(firstPhysicalBlock);

        var pageCount = GetLogicalPageCount(logicalData.Length);
        cancellationToken.ThrowIfCancellationRequested();
        var physical = new byte[GetPhysicalLength(pageCount)];
        AddEccCore(
            logicalData,
            physical,
            layout,
            firstPhysicalBlock,
            clearDestinationPages: false,
            progress,
            cancellationToken);
        return physical;
    }

    /// <summary>
    /// Adds legacy spare data and ECC to complete logical pages using a caller-provided destination.
    /// </summary>
    /// <remarks>
    /// Source and destination spans must not overlap because physical pages are larger than logical pages.
    /// </remarks>
    public static void AddEcc(
        ReadOnlySpan<byte> logicalData,
        Span<byte> physicalDestination,
        NandPhysicalLayout layout,
        long firstPhysicalBlock = 0,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ValidateFirstPhysicalBlock(firstPhysicalBlock);

        var pageCount = GetLogicalPageCount(logicalData.Length);
        ValidateDestinationLength(physicalDestination.Length, GetPhysicalLength(pageCount), nameof(physicalDestination));
        if (logicalData.Overlaps(physicalDestination))
        {
            throw new ArgumentException("Logical source and physical destination spans must not overlap.", nameof(physicalDestination));
        }

        AddEccCore(
            logicalData,
            physicalDestination,
            layout,
            firstPhysicalBlock,
            clearDestinationPages: true,
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Removes spare data and ECC from complete interleaved physical pages.
    /// </summary>
    /// <param name="physicalData">Complete <c>0x210</c>-byte physical pages.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Token checked before every input page.</param>
    /// <returns>New logical <c>0x200</c>-byte pages.</returns>
    /// <exception cref="OperationFailureException">Thrown when physical data ends in a partial page.</exception>
    public static ReadOnlyMemory<byte> RemoveEcc(
        ReadOnlySpan<byte> physicalData,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pageCount = GetPhysicalPageCount(physicalData.Length);
        cancellationToken.ThrowIfCancellationRequested();
        var logical = new byte[GetLogicalLength(pageCount)];
        RemoveEccCore(physicalData, logical, progress, cancellationToken);
        return logical;
    }

    /// <summary>
    /// Removes spare data and ECC from complete interleaved physical pages using a caller-provided destination.
    /// </summary>
    /// <remarks>
    /// Source and destination spans must not overlap.
    /// </remarks>
    /// <exception cref="OperationFailureException">Thrown when physical data ends in a partial page.</exception>
    public static void RemoveEcc(
        ReadOnlySpan<byte> physicalData,
        Span<byte> logicalDestination,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pageCount = GetPhysicalPageCount(physicalData.Length);
        ValidateDestinationLength(logicalDestination.Length, GetLogicalLength(pageCount), nameof(logicalDestination));
        if (physicalData.Overlaps(logicalDestination))
        {
            throw new ArgumentException("Physical source and logical destination spans must not overlap.", nameof(logicalDestination));
        }

        RemoveEccCore(physicalData, logicalDestination, progress, cancellationToken);
    }

    /// <summary>
    /// Gets the required interleaved physical length for a complete logical NAND length.
    /// </summary>
    /// <exception cref="OperationFailureException">Thrown when <paramref name="logicalLength"/> ends in a partial logical page.</exception>
    public static int GetPhysicalLengthForLogicalLength(int logicalLength)
    {
        if (logicalLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalLength));
        }

        return GetPhysicalLength(GetLogicalPageCount(logicalLength));
    }

    /// <summary>
    /// Gets the required logical length for a complete interleaved physical NAND length.
    /// </summary>
    /// <exception cref="OperationFailureException">Thrown when <paramref name="physicalLength"/> ends in a partial physical page.</exception>
    public static int GetLogicalLengthForPhysicalLength(int physicalLength)
    {
        if (physicalLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalLength));
        }

        return GetLogicalLength(GetPhysicalPageCount(physicalLength));
    }

    /// <summary>
    /// Recalculates the legacy four-byte ECC value in a physical page.
    /// </summary>
    /// <param name="physicalPage">Exactly one <c>0x210</c>-byte physical page.</param>
    public static void CalculateEcc(Span<byte> physicalPage)
    {
        var ecc = CalculateEccWord(physicalPage);
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(
            physicalPage.Slice(NandPhysicalGeometry.PhysicalPageSize - EccSize, EccSize));
        BinaryPrimitives.WriteUInt32LittleEndian(
            physicalPage.Slice(NandPhysicalGeometry.PhysicalPageSize - EccSize, EccSize),
            ecc | (stored & PreservedSpareBitsMask));
    }

    /// <summary>
    /// Calculates the legacy ECC word without modifying the supplied physical page.
    /// </summary>
    /// <param name="physicalPage">Exactly one <c>0x210</c>-byte physical page.</param>
    /// <returns>The ECC word in the little-endian form stored in the page's final four bytes.</returns>
    public static uint CalculateEccWord(ReadOnlySpan<byte> physicalPage)
    {
        ValidatePhysicalPageLength(physicalPage.Length, nameof(physicalPage));

        var value = 0;
        var inputBits = 0;
        for (var bit = 0; bit < LegacyEccCalculationBitCount; bit++)
        {
            if ((bit & 31) == 0)
            {
                inputBits = ~BinaryPrimitives.ReadInt32LittleEndian(physicalPage.Slice(bit / 8, sizeof(int)));
            }

            // The final six bits are block-type data stored in the low bits of the
            // final ECC word. They participate in the legacy 0x1066-bit calculation.
            value ^= inputBits & 1;
            inputBits >>= 1;
            if ((value & 1) != 0)
            {
                value ^= 0x06954559;
            }

            value >>= 1;
        }

        value = ~value;
        return unchecked((uint)(value << 6));
    }

    /// <summary>
    /// Determines whether the final four bytes of a physical page match the legacy ECC calculation.
    /// </summary>
    public static bool HasValidEcc(ReadOnlySpan<byte> physicalPage)
    {
        ValidatePhysicalPageLength(physicalPage.Length, nameof(physicalPage));
        var actual = BinaryPrimitives.ReadUInt32LittleEndian(
            physicalPage.Slice(NandPhysicalGeometry.PhysicalPageSize - EccSize, EccSize));
        var calculated = CalculateEccWord(physicalPage);
        return (actual & ~PreservedSpareBitsMask) == (calculated & ~PreservedSpareBitsMask);
    }

    private static void AddEccCore(
        ReadOnlySpan<byte> logicalData,
        Span<byte> physicalDestination,
        NandPhysicalLayout layout,
        long firstPhysicalBlock,
        bool clearDestinationPages,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pageCount = logicalData.Length / NandPhysicalGeometry.LogicalPageSize;
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, "adding-ecc", "Adding ECC to physical NAND pages.", completed: 0, pageCount);

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var physicalPage = physicalDestination.Slice(
                pageIndex * NandPhysicalGeometry.PhysicalPageSize,
                NandPhysicalGeometry.PhysicalPageSize);
            if (clearDestinationPages)
            {
                physicalPage.Clear();
            }

            logicalData.Slice(
                pageIndex * NandPhysicalGeometry.LogicalPageSize,
                NandPhysicalGeometry.LogicalPageSize).CopyTo(physicalPage);

            var physicalBlock = checked(firstPhysicalBlock + (pageIndex / layout.Geometry.PagesPerBlock));
            WriteLegacySpare(physicalPage, layout, physicalBlock);
            CalculateEcc(physicalPage);

            var completed = pageIndex + 1;
            if (completed == pageCount || completed % layout.Geometry.PagesPerBlock == 0)
            {
                Report(progress, "adding-ecc", "Adding ECC to physical NAND pages.", completed, pageCount);
            }
        }
    }

    private static void RemoveEccCore(
        ReadOnlySpan<byte> physicalData,
        Span<byte> logicalDestination,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pageCount = physicalData.Length / NandPhysicalGeometry.PhysicalPageSize;
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, "removing-ecc", "Removing ECC from physical NAND pages.", completed: 0, pageCount);

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            physicalData.Slice(
                pageIndex * NandPhysicalGeometry.PhysicalPageSize,
                NandPhysicalGeometry.LogicalPageSize).CopyTo(logicalDestination.Slice(
                    pageIndex * NandPhysicalGeometry.LogicalPageSize,
                    NandPhysicalGeometry.LogicalPageSize));

            var completed = pageIndex + 1;
            if (completed == pageCount || completed % 0x80 == 0)
            {
                Report(progress, "removing-ecc", "Removing ECC from physical NAND pages.", completed, pageCount);
            }
        }
    }

    private static void WriteLegacySpare(Span<byte> physicalPage, NandPhysicalLayout layout, long physicalBlock)
    {
        var blockId = unchecked((ushort)physicalBlock);
        physicalPage[layout.MarkerOffset] = byte.MaxValue;
        physicalPage[layout.BlockIdOffset] = (byte)blockId;
        physicalPage[layout.BlockIdOffset + 1] = (byte)(blockId >> 8);
    }

    private static int GetLogicalPageCount(int logicalLength)
    {
        if (logicalLength % NandPhysicalGeometry.LogicalPageSize != 0)
        {
            throw InvalidData(
                "truncated-logical-nand",
                "Logical NAND data must contain complete 0x200-byte pages.");
        }

        return logicalLength / NandPhysicalGeometry.LogicalPageSize;
    }

    private static int GetPhysicalPageCount(int physicalLength)
    {
        if (physicalLength % NandPhysicalGeometry.PhysicalPageSize != 0)
        {
            throw InvalidData(
                "truncated-physical-nand",
                "Physical NAND data must contain complete 0x210-byte pages.");
        }

        return physicalLength / NandPhysicalGeometry.PhysicalPageSize;
    }

    private static int GetPhysicalLength(int pageCount) => checked(pageCount * NandPhysicalGeometry.PhysicalPageSize);

    private static int GetLogicalLength(int pageCount) => checked(pageCount * NandPhysicalGeometry.LogicalPageSize);

    private static void ValidateFirstPhysicalBlock(long firstPhysicalBlock)
    {
        if (firstPhysicalBlock < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPhysicalBlock), "The first physical block cannot be negative.");
        }
    }

    private static void ValidateDestinationLength(int actualLength, int expectedLength, string parameterName)
    {
        if (actualLength != expectedLength)
        {
            throw new ArgumentException($"The destination must be exactly 0x{expectedLength:X} bytes.", parameterName);
        }
    }

    private static void ValidatePhysicalPageLength(int actualLength, string parameterName)
    {
        if (actualLength != NandPhysicalGeometry.PhysicalPageSize)
        {
            throw new ArgumentException("A physical NAND page must be exactly 0x210 bytes.", parameterName);
        }
    }

    private static OperationFailureException InvalidData(string kind, string message) =>
        new(ExitCode.InvalidData, kind, message);

    private static void Report(
        IProgress<OperationProgress>? progress,
        string kind,
        string message,
        int completed,
        int total)
    {
        progress?.Report(new OperationProgress(kind, message, completed: completed, total: total));
    }
}

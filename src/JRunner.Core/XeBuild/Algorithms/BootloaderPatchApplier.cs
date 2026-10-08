using System.Buffers.Binary;
using JRunner.Core.Contracts;

namespace JRunner.Core.XeBuild.Algorithms;

/// <summary>
/// The immutable result of applying one xeBuild bootloader patch section.
/// </summary>
public sealed record BootloaderPatchResult
{
    private readonly byte[] bootloader;

    internal BootloaderPatchResult(byte[] bootloader, int patchCount)
    {
        ArgumentNullException.ThrowIfNull(bootloader);
        ArgumentOutOfRangeException.ThrowIfNegative(patchCount);

        this.bootloader = bootloader;
        PatchCount = patchCount;
    }

    /// <summary>
    /// Gets a read-only view of the copy-owned patched bootloader whose size field has been
    /// updated and padded to 16 bytes.
    /// </summary>
    public ReadOnlySpan<byte> Bootloader => bootloader;

    /// <summary>
    /// Gets the number of patch records applied before the section terminator.
    /// </summary>
    public int PatchCount { get; }

    /// <summary>
    /// Gets the padded size of <see cref="Bootloader"/>.
    /// </summary>
    public int Length => bootloader.Length;
}

/// <summary>
/// Applies a big-endian xeBuild bootloader patch section without mutating either input buffer.
/// </summary>
public static class BootloaderPatchApplier
{
    private const int BootloaderHeaderLength = 0x10;
    private const int TerminatorLength = sizeof(uint);
    private const int Alignment = 0x10;

    /// <summary>
    /// Applies the patch records in a single, terminator-inclusive xeBuild section to a bootloader.
    /// </summary>
    /// <remarks>
    /// A patch record is a big-endian destination offset, followed by a big-endian count of
    /// 32-bit words and that many data bytes. The section must end exactly at an address of
    /// <c>0xFFFFFFFF</c>. The returned bootloader is extended as necessary, rounded up to a
    /// 16-byte boundary, and has that padded size written big-endian at offset <c>0xC</c>.
    /// </remarks>
    /// <exception cref="OperationFailureException">
    /// Thrown when the bootloader or patch section is malformed, truncated, or requests an
    /// unrepresentable managed-buffer range.
    /// </exception>
    public static BootloaderPatchResult Apply(
        ReadOnlySpan<byte> bootloader,
        ReadOnlySpan<byte> patchSection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (bootloader.Length < BootloaderHeaderLength)
        {
            throw InvalidData(
                "truncated-bootloader-data",
                "The bootloader is shorter than its 0x10-byte header.");
        }

        var plan = BuildPlan(bootloader.Length, patchSection, cancellationToken);
        var paddedLength = GetPaddedLength(plan.RequiredLength);
        var output = new byte[paddedLength];
        CopyWithCancellation(bootloader, output, cancellationToken);

        ApplyRecords(output, patchSection, cancellationToken);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0xC, sizeof(uint)), (uint)paddedLength);

        return new BootloaderPatchResult(output, plan.PatchCount);
    }

    private static PatchPlan BuildPlan(
        int bootloaderLength,
        ReadOnlySpan<byte> patchSection,
        CancellationToken cancellationToken)
    {
        var cursor = 0;
        var patchCount = 0;
        var requiredLength = bootloaderLength;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailable(patchSection, cursor, TerminatorLength, "patch address");
            var address = BinaryPrimitives.ReadUInt32BigEndian(patchSection.Slice(cursor, TerminatorLength));
            cursor += TerminatorLength;

            if (address == uint.MaxValue)
            {
                if (cursor != patchSection.Length)
                {
                    throw InvalidData(
                        "invalid-xebuild-patch-section",
                        "The xeBuild patch section contains data after its terminator.");
                }

                return new PatchPlan(requiredLength, patchCount);
            }

            EnsureAvailable(patchSection, cursor, sizeof(uint), "patch word count");
            var wordCount = BinaryPrimitives.ReadUInt32BigEndian(patchSection.Slice(cursor, sizeof(uint)));
            cursor += sizeof(uint);

            if (wordCount > (uint)(int.MaxValue / sizeof(uint)))
            {
                throw InvalidData(
                    "invalid-xebuild-patch-range",
                    "The xeBuild patch record length exceeds the managed-buffer limit.");
            }

            if (address > (uint)int.MaxValue)
            {
                throw InvalidData(
                    "invalid-xebuild-patch-range",
                    "The xeBuild patch record address exceeds the managed-buffer limit.");
            }

            var byteCount = checked((int)wordCount * sizeof(uint));
            EnsureAvailable(patchSection, cursor, byteCount, "patch data");

            var patchEnd = checked((long)address + byteCount);
            if (patchEnd > Array.MaxLength)
            {
                throw InvalidData(
                    "invalid-xebuild-patch-range",
                    "The xeBuild patch record extends beyond the managed-buffer limit.");
            }

            requiredLength = Math.Max(requiredLength, (int)patchEnd);
            cursor += byteCount;
            patchCount = checked(patchCount + 1);
        }
    }

    private static void ApplyRecords(
        Span<byte> output,
        ReadOnlySpan<byte> patchSection,
        CancellationToken cancellationToken)
    {
        var cursor = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = BinaryPrimitives.ReadUInt32BigEndian(patchSection.Slice(cursor, TerminatorLength));
            cursor += TerminatorLength;
            if (address == uint.MaxValue)
            {
                return;
            }

            var wordCount = BinaryPrimitives.ReadUInt32BigEndian(patchSection.Slice(cursor, sizeof(uint)));
            cursor += sizeof(uint);
            var byteCount = checked((int)wordCount * sizeof(uint));
            CopyWithCancellation(
                patchSection.Slice(cursor, byteCount),
                output.Slice((int)address, byteCount),
                cancellationToken);
            cursor += byteCount;
        }
    }

    private static void CopyWithCancellation(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        CancellationToken cancellationToken)
    {
        const int chunkLength = 64 * 1024;
        var offset = 0;
        while (offset < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(chunkLength, source.Length - offset);
            source.Slice(offset, length).CopyTo(destination.Slice(offset, length));
            offset += length;
        }
    }

    private static int GetPaddedLength(int length)
    {
        var paddedLength = ((long)length + (Alignment - 1)) & ~(long)(Alignment - 1);
        if (paddedLength > Array.MaxLength)
        {
            throw InvalidData(
                "invalid-xebuild-patch-range",
                "The patched bootloader exceeds the managed-buffer limit.");
        }

        return (int)paddedLength;
    }

    private static void EnsureAvailable(
        ReadOnlySpan<byte> patchSection,
        int offset,
        int length,
        string item)
    {
        if (offset > patchSection.Length || patchSection.Length - offset < length)
        {
            throw InvalidData(
                "truncated-xebuild-patch-section",
                $"The xeBuild patch section is truncated while reading {item}.");
        }
    }

    private static OperationFailureException InvalidData(string kind, string message)
    {
        return new OperationFailureException(ExitCode.InvalidData, kind, message);
    }

    private readonly record struct PatchPlan(int RequiredLength, int PatchCount);
}

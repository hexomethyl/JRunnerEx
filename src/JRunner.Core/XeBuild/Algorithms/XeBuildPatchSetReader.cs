using JRunner.Core.Contracts;

namespace JRunner.Core.XeBuild.Algorithms;

/// <summary>
/// Reads the three <c>0xFFFFFFFF</c>-terminated sections of a xeBuild patch payload.
/// </summary>
public static class XeBuildPatchSetReader
{
    private const int TerminatorLength = sizeof(uint);

    /// <summary>
    /// Finds the length of one terminator-inclusive patch section beginning at <paramref name="offset"/>.
    /// </summary>
    /// <remarks>
    /// xeBuild identifies a section boundary by the first four consecutive <c>0xFF</c> bytes,
    /// rather than by a length prefix. The returned length includes those four bytes.
    /// </remarks>
    /// <exception cref="OperationFailureException">
    /// Thrown when the remaining input has no complete section terminator.
    /// </exception>
    public static int GetSectionLength(
        ReadOnlySpan<byte> patchData,
        int offset,
        CancellationToken cancellationToken = default)
    {
        ValidateOffset(patchData.Length, offset);
        return FindSectionLength(patchData, offset, cancellationToken);
    }

    /// <summary>
    /// Reads one terminator-inclusive patch section beginning at <paramref name="offset"/>.
    /// </summary>
    /// <exception cref="OperationFailureException">
    /// Thrown when the remaining input has no complete section terminator.
    /// </exception>
    public static XeBuildPatchSection ReadSection(
        ReadOnlySpan<byte> patchData,
        int offset,
        XeBuildPatchSectionKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined<XeBuildPatchSectionKind>(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The patch section kind is not defined.");
        }

        ValidateOffset(patchData.Length, offset);
        var length = FindSectionLength(patchData, offset, cancellationToken);
        return new XeBuildPatchSection(kind, offset, patchData.Slice(offset, length));
    }

    /// <summary>
    /// Extracts one patch set from a complete xeBuild patch payload.
    /// </summary>
    /// <remarks>
    /// Extracting the 4BL or kernel/hypervisor set verifies the terminator of every preceding set.
    /// It does not require later sets to be present, which permits consumers that only need an
    /// earlier legacy patch set to use a partial payload.
    /// </remarks>
    /// <exception cref="OperationFailureException">
    /// Thrown when the requested set or a preceding set is unterminated.
    /// </exception>
    public static XeBuildPatchSection Extract(
        ReadOnlySpan<byte> patchData,
        XeBuildPatchSectionKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined<XeBuildPatchSectionKind>(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The patch section kind is not defined.");
        }

        var offset = 0;
        for (var sectionIndex = 0; sectionIndex < (int)kind; sectionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            offset = checked(offset + GetSectionLength(patchData, offset, cancellationToken));
        }

        return ReadSection(patchData, offset, kind, cancellationToken);
    }

    /// <summary>
    /// Reads all three patch sets from a complete xeBuild patch payload.
    /// </summary>
    /// <exception cref="OperationFailureException">
    /// Thrown when any required set is unterminated.
    /// </exception>
    public static XeBuildPatchSets ReadAll(
        ReadOnlySpan<byte> patchData,
        CancellationToken cancellationToken = default)
    {
        var offset = 0;
        var twoBl = ReadSection(
            patchData,
            offset,
            XeBuildPatchSectionKind.TwoBl,
            cancellationToken);
        offset = checked(offset + twoBl.Length);

        var fourBl = ReadSection(
            patchData,
            offset,
            XeBuildPatchSectionKind.FourBl,
            cancellationToken);
        offset = checked(offset + fourBl.Length);

        var kernelHypervisor = ReadSection(
            patchData,
            offset,
            XeBuildPatchSectionKind.KernelHypervisor,
            cancellationToken);

        return new XeBuildPatchSets(twoBl, fourBl, kernelHypervisor);
    }

    private static int FindSectionLength(
        ReadOnlySpan<byte> patchData,
        int offset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        for (var index = offset; index <= patchData.Length - TerminatorLength; index++)
        {
            if ((index & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (patchData[index] == byte.MaxValue &&
                patchData[index + 1] == byte.MaxValue &&
                patchData[index + 2] == byte.MaxValue &&
                patchData[index + 3] == byte.MaxValue)
            {
                return checked((index - offset) + TerminatorLength);
            }
        }

        throw InvalidData(
            "unterminated-xebuild-patch-section",
            "The xeBuild patch section is missing its 0xFFFFFFFF terminator.");
    }

    private static void ValidateOffset(int dataLength, int offset)
    {
        if (offset < 0 || offset > dataLength)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset must identify a position in the patch payload.");
        }
    }

    private static OperationFailureException InvalidData(string kind, string message)
    {
        return new OperationFailureException(ExitCode.InvalidData, kind, message);
    }
}

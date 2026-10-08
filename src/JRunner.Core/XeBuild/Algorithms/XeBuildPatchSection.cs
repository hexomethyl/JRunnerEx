namespace JRunner.Core.XeBuild.Algorithms;

/// <summary>
/// Identifies one of the three terminated patch sets in a xeBuild patch payload.
/// </summary>
public enum XeBuildPatchSectionKind
{
    /// <summary>
    /// The CB, CB_B, or SB patch set.
    /// </summary>
    TwoBl = 0,

    /// <summary>
    /// The CD or SD patch set.
    /// </summary>
    FourBl = 1,

    /// <summary>
    /// The kernel and hypervisor patch set.
    /// </summary>
    KernelHypervisor = 2,
}

/// <summary>
/// An immutable, terminator-inclusive section from a xeBuild patch payload.
/// </summary>
public sealed record XeBuildPatchSection
{
    private readonly byte[] data;

    internal XeBuildPatchSection(XeBuildPatchSectionKind kind, int offset, ReadOnlySpan<byte> data)
    {
        if (!Enum.IsDefined<XeBuildPatchSectionKind>(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The patch section kind is not defined.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (data.Length < sizeof(uint))
        {
            throw new ArgumentException("A patch section must include its four-byte terminator.", nameof(data));
        }

        Kind = kind;
        Offset = offset;
        this.data = data.ToArray();
    }

    /// <summary>
    /// Gets the semantic role of this section in the enclosing patch payload.
    /// </summary>
    public XeBuildPatchSectionKind Kind { get; }

    /// <summary>
    /// Gets the zero-based byte offset of this section in the enclosing patch payload.
    /// </summary>
    public int Offset { get; }

    /// <summary>
    /// Gets a read-only view of the section bytes, including the <c>0xFFFFFFFF</c> terminator.
    /// </summary>
    public ReadOnlySpan<byte> Data => data;

    /// <summary>
    /// Gets the number of bytes in <see cref="Data"/>.
    /// </summary>
    public int Length => data.Length;
}

/// <summary>
/// The three patch sets carried by a complete xeBuild patch payload.
/// </summary>
public sealed record XeBuildPatchSets
{
    /// <summary>
    /// Initializes an immutable set of xeBuild patch sections.
    /// </summary>
    public XeBuildPatchSets(
        XeBuildPatchSection twoBl,
        XeBuildPatchSection fourBl,
        XeBuildPatchSection kernelHypervisor)
    {
        ArgumentNullException.ThrowIfNull(twoBl);
        ArgumentNullException.ThrowIfNull(fourBl);
        ArgumentNullException.ThrowIfNull(kernelHypervisor);

        if (twoBl.Kind != XeBuildPatchSectionKind.TwoBl)
        {
            throw new ArgumentException("The 2BL section has the wrong kind.", nameof(twoBl));
        }

        if (fourBl.Kind != XeBuildPatchSectionKind.FourBl)
        {
            throw new ArgumentException("The 4BL section has the wrong kind.", nameof(fourBl));
        }

        if (kernelHypervisor.Kind != XeBuildPatchSectionKind.KernelHypervisor)
        {
            throw new ArgumentException("The kernel/hypervisor section has the wrong kind.", nameof(kernelHypervisor));
        }

        TwoBl = twoBl;
        FourBl = fourBl;
        KernelHypervisor = kernelHypervisor;
    }

    /// <summary>
    /// Gets the CB, CB_B, or SB patch set.
    /// </summary>
    public XeBuildPatchSection TwoBl { get; }

    /// <summary>
    /// Gets the CD or SD patch set.
    /// </summary>
    public XeBuildPatchSection FourBl { get; }

    /// <summary>
    /// Gets the kernel and hypervisor patch set.
    /// </summary>
    public XeBuildPatchSection KernelHypervisor { get; }
}

namespace JRunner.Core.Nand.Inspection.Models;

/// <summary>
/// Describes whether the legacy virtual-fuse marker could be inspected safely.
/// </summary>
public enum NandVirtualFuseEvidenceStatus
{
    /// <summary>
    /// No marker inspection result was supplied or the marker locations could not be inspected
    /// safely. This is the default value so omitted evidence cannot assert Glitch2m.
    /// </summary>
    Unavailable = 0,

    /// <summary>A recognized virtual-fuse marker was found.</summary>
    Present,

    /// <summary>All applicable legacy marker locations were inspected and no marker was found.</summary>
    Absent,
}

/// <summary>
/// Safe virtual-fuse evidence used by the legacy Glitch2m selection rule.
/// </summary>
/// <remarks>
/// The inspector compares only the public eight-byte virtual-fuse-line marker. It deliberately does
/// not read or expose the virtual CPU key stored later in the same record.
/// </remarks>
public sealed record NandVirtualFuseEvidence
{
    /// <summary>
    /// Creates safe virtual-fuse marker evidence.
    /// </summary>
    /// <param name="status">The completed marker-inspection status.</param>
    public NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "The virtual-fuse evidence status is not supported.");
        }

        Status = status;
    }

    /// <summary>
    /// Gets the result of inspecting legacy virtual-fuse locations.
    /// </summary>
    public NandVirtualFuseEvidenceStatus Status { get; }

    /// <summary>
    /// Gets whether a virtual-fuse marker was positively found.
    /// </summary>
    public bool IsPresent => Status == NandVirtualFuseEvidenceStatus.Present;
}

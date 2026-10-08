using JRunner.Core.Nand.Physical;

namespace JRunner.Core.Nand.Models;

/// <summary>
/// Immutable metadata for one canonical console entry from legacy <c>variables.ctypes</c>.
/// </summary>
public sealed record ConsoleDefinition
{
    /// <summary>
    /// Creates canonical console metadata.
    /// </summary>
    public ConsoleDefinition(
        ConsoleId id,
        string canonicalName,
        string xeBuildName,
        string iniName,
        int nandSizeMegabytes,
        NandLogicalSize logicalNandSize,
        NandLegacyLayout? layout)
    {
        if ((int)id is < ConsoleCatalog.FirstLegacyId or > ConsoleCatalog.LastLegacyId)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "The console identifier is not supported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(xeBuildName);
        ArgumentException.ThrowIfNullOrWhiteSpace(iniName);

        if (nandSizeMegabytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nandSizeMegabytes), "The legacy NAND size cannot be negative.");
        }

        if (logicalNandSize is not (
            NandLogicalSize.S0 or
            NandLogicalSize.S16 or
            NandLogicalSize.S64 or
            NandLogicalSize.S256 or
            NandLogicalSize.S512))
        {
            throw new ArgumentOutOfRangeException(nameof(logicalNandSize), logicalNandSize, "The NAND size classification is not supported.");
        }

        if (layout.HasValue &&
            layout.Value is not (
                NandLegacyLayout.Layout0 or
                NandLegacyLayout.Layout1 or
                NandLegacyLayout.Layout2))
        {
            throw new ArgumentOutOfRangeException(nameof(layout), layout, "The NAND layout is not supported.");
        }

        Id = id;
        CanonicalName = canonicalName;
        XeBuildName = xeBuildName;
        IniName = iniName;
        NandSizeMegabytes = nandSizeMegabytes;
        LogicalNandSize = logicalNandSize;
        Layout = layout;
    }

    /// <summary>
    /// Gets the stable legacy console identifier.
    /// </summary>
    public ConsoleId Id { get; }

    /// <summary>
    /// Gets the numeric legacy console identifier.
    /// </summary>
    public int LegacyId => (int)Id;

    /// <summary>
    /// Gets the canonical human-readable name carried by the legacy <c>Text</c> field.
    /// </summary>
    public string CanonicalName { get; }

    /// <summary>
    /// Gets the exact board name passed to XeBuild.
    /// </summary>
    public string XeBuildName { get; }

    /// <summary>
    /// Gets the exact board prefix used by XeBuild INI bootloader labels.
    /// </summary>
    public string IniName { get; }

    /// <summary>
    /// Gets the legacy numeric <c>Nandsize</c> field in mebibytes. This deliberately preserves
    /// the legacy 64 MB values for the Winchester 16 MB and 4 GB entries.
    /// </summary>
    public int NandSizeMegabytes { get; }

    /// <summary>
    /// Gets the legacy logical NAND size classification.
    /// </summary>
    public NandLogicalSize LogicalNandSize { get; }

    /// <summary>
    /// Gets the logical NAND capacity in bytes, or zero for the legacy <see cref="NandLogicalSize.S0"/> classification.
    /// </summary>
    public long LogicalNandByteLength => LogicalNandSize.GetByteLength();

    /// <summary>
    /// Gets the legacy spare layout when the console has NAND spare data; otherwise <see langword="null"/> for the legacy eMMC entries.
    /// </summary>
    public NandLegacyLayout? Layout { get; }

    /// <summary>
    /// Gets the legacy numeric layout identifier, preserving <c>-1</c> for entries without a NAND spare layout.
    /// </summary>
    public int LegacyLayoutId => Layout is { } layout ? (int)layout : -1;
}

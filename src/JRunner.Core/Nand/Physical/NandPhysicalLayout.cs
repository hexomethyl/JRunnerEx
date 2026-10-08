namespace JRunner.Core.Nand.Physical;

/// <summary>
/// Identifies the spare-area conventions used by legacy J-Runner NAND layouts.
/// </summary>
public enum NandLegacyLayout
{
    /// <summary>
    /// Xenon, Zephyr, and Falcon small-block layout.
    /// </summary>
    Layout0 = 0,

    /// <summary>
    /// Jasper 16 MB and Trinity small-block layout.
    /// </summary>
    Layout1 = 1,

    /// <summary>
    /// Jasper 256 MB and 512 MB large-block layout.
    /// </summary>
    Layout2 = 2,
}

/// <summary>
/// Immutable description of a legacy physical NAND spare-area layout.
/// </summary>
public sealed record NandPhysicalLayout
{
    private NandPhysicalLayout(
        NandLegacyLayout legacyLayout,
        NandPhysicalGeometry geometry,
        int markerOffset,
        int blockIdOffset)
    {
        if (markerOffset is < NandPhysicalGeometry.LogicalPageSize or >= NandPhysicalGeometry.PhysicalPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(markerOffset));
        }

        if (blockIdOffset < NandPhysicalGeometry.LogicalPageSize ||
            blockIdOffset + sizeof(ushort) > NandPhysicalGeometry.PhysicalPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(blockIdOffset));
        }

        LegacyLayout = legacyLayout;
        Geometry = geometry;
        MarkerOffset = markerOffset;
        BlockIdOffset = blockIdOffset;
    }

    /// <summary>
    /// Gets legacy layout 0: marker at <c>0x205</c>, block ID at <c>0x200</c>, and small blocks.
    /// </summary>
    public static NandPhysicalLayout Layout0 { get; } = new(
        NandLegacyLayout.Layout0,
        NandPhysicalGeometry.SmallBlock,
        markerOffset: 0x205,
        blockIdOffset: 0x200);

    /// <summary>
    /// Gets legacy layout 1: marker at <c>0x205</c>, block ID at <c>0x201</c>, and small blocks.
    /// </summary>
    public static NandPhysicalLayout Layout1 { get; } = new(
        NandLegacyLayout.Layout1,
        NandPhysicalGeometry.SmallBlock,
        markerOffset: 0x205,
        blockIdOffset: 0x201);

    /// <summary>
    /// Gets legacy layout 2: marker at <c>0x200</c>, block ID at <c>0x201</c>, and large blocks.
    /// </summary>
    public static NandPhysicalLayout Layout2 { get; } = new(
        NandLegacyLayout.Layout2,
        NandPhysicalGeometry.LargeBlock,
        markerOffset: 0x200,
        blockIdOffset: 0x201);

    /// <summary>
    /// Gets the legacy numeric layout identifier.
    /// </summary>
    public NandLegacyLayout LegacyLayout { get; }

    /// <summary>
    /// Gets the erase-block geometry required by this layout.
    /// </summary>
    public NandPhysicalGeometry Geometry { get; }

    /// <summary>
    /// Gets the marker byte offset within a physical <c>0x210</c>-byte page.
    /// </summary>
    public int MarkerOffset { get; }

    /// <summary>
    /// Gets the low byte of the two-byte little-endian physical block ID within a page.
    /// </summary>
    public int BlockIdOffset { get; }

    /// <summary>
    /// Gets the marker byte offset within the <c>0x10</c>-byte spare area.
    /// </summary>
    public int MarkerSpareOffset => MarkerOffset - NandPhysicalGeometry.LogicalPageSize;

    /// <summary>
    /// Gets the block ID offset within the <c>0x10</c>-byte spare area.
    /// </summary>
    public int BlockIdSpareOffset => BlockIdOffset - NandPhysicalGeometry.LogicalPageSize;

    /// <summary>
    /// Resolves a legacy numeric layout identifier.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="legacyLayout"/> is unsupported.</exception>
    public static NandPhysicalLayout FromLegacyLayout(NandLegacyLayout legacyLayout) => legacyLayout switch
    {
        NandLegacyLayout.Layout0 => Layout0,
        NandLegacyLayout.Layout1 => Layout1,
        NandLegacyLayout.Layout2 => Layout2,
        _ => throw new ArgumentOutOfRangeException(nameof(legacyLayout), legacyLayout, "The NAND spare layout is not supported."),
    };

    /// <summary>
    /// Resolves a legacy numeric layout identifier.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="legacyLayout"/> is unsupported.</exception>
    public static NandPhysicalLayout FromLegacyLayout(int legacyLayout)
    {
        if (!Enum.IsDefined(typeof(NandLegacyLayout), legacyLayout))
        {
            throw new ArgumentOutOfRangeException(nameof(legacyLayout), legacyLayout, "The NAND spare layout is not supported.");
        }

        return FromLegacyLayout((NandLegacyLayout)legacyLayout);
    }
}

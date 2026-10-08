namespace JRunner.Core.Nand.Physical;

/// <summary>
/// Describes the fixed page and erase-block dimensions used by Xbox 360 NAND images.
/// </summary>
public sealed record NandPhysicalGeometry
{
    private NandPhysicalGeometry(string name, int pagesPerBlock)
    {
        Name = name;
        PagesPerBlock = pagesPerBlock;
    }

    /// <summary>
    /// Gets the number of logical data bytes in one NAND page.
    /// </summary>
    public const int LogicalPageSize = 0x200;

    /// <summary>
    /// Gets the number of spare bytes stored after each logical NAND page.
    /// </summary>
    public const int SpareSize = 0x10;

    /// <summary>
    /// Gets the number of bytes in an interleaved data-and-spare NAND page.
    /// </summary>
    public const int PhysicalPageSize = LogicalPageSize + SpareSize;

    /// <summary>
    /// Gets the legacy small-block NAND geometry: 32 physical pages per block.
    /// </summary>
    public static NandPhysicalGeometry SmallBlock { get; } = new("small-block", 0x20);

    /// <summary>
    /// Gets the legacy large-block NAND geometry: 256 physical pages per block.
    /// </summary>
    public static NandPhysicalGeometry LargeBlock { get; } = new("large-block", 0x100);

    /// <summary>
    /// Gets a stable name for the geometry.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the count of physical pages in one erase block.
    /// </summary>
    public int PagesPerBlock { get; }

    /// <summary>
    /// Gets the number of logical data bytes in one erase block.
    /// </summary>
    public int LogicalBlockSize => checked(PagesPerBlock * LogicalPageSize);

    /// <summary>
    /// Gets the number of data-and-spare bytes in one erase block.
    /// </summary>
    public int PhysicalBlockSize => checked(PagesPerBlock * PhysicalPageSize);

    /// <summary>
    /// Gets whether the geometry uses the legacy 256-page large block arrangement.
    /// </summary>
    public bool IsLargeBlock => PagesPerBlock == LargeBlock.PagesPerBlock;
}

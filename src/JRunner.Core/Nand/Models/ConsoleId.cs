namespace JRunner.Core.Nand.Models;

/// <summary>
/// Identifies one of the canonical legacy console definitions from <c>variables.ctypes</c>.
/// The numeric values intentionally match the legacy identifiers.
/// </summary>
public enum ConsoleId
{
    /// <summary>Trinity with a 16 MB NAND.</summary>
    Trinity16Mb = 1,

    /// <summary>Falcon with a 16 MB NAND.</summary>
    Falcon16Mb = 2,

    /// <summary>Zephyr with a 16 MB NAND.</summary>
    Zephyr16Mb = 3,

    /// <summary>Jasper with a 16 MB NAND.</summary>
    Jasper16Mb = 4,

    /// <summary>Jasper using the small-block (XSB) NAND layout.</summary>
    JasperXsb = 5,

    /// <summary>Jasper using the big-block NAND layout.</summary>
    JasperBigBlock = 6,

    /// <summary>Xenon with a 64 MB NAND.</summary>
    Xenon64Mb = 7,

    /// <summary>Xenon with a 16 MB NAND.</summary>
    Xenon16Mb = 8,

    /// <summary>Corona using the big-block NAND layout.</summary>
    CoronaBigBlock = 9,

    /// <summary>Corona with a 16 MB NAND.</summary>
    Corona16Mb = 10,

    /// <summary>Corona using 4 GB eMMC storage.</summary>
    Corona4Gb = 11,

    /// <summary>Trinity using the big-block NAND layout.</summary>
    TrinityBigBlock = 12,

    /// <summary>Zephyr with a 64 MB NAND.</summary>
    Zephyr64Mb = 13,

    /// <summary>Falcon with a 64 MB NAND.</summary>
    Falcon64Mb = 14,

    /// <summary>Winchester with a 16 MB NAND.</summary>
    Winchester16Mb = 15,

    /// <summary>Winchester using 4 GB eMMC storage.</summary>
    Winchester4Gb = 16,

    /// <summary>Winchester using the big-block NAND layout.</summary>
    WinchesterBigBlock = 17,
}

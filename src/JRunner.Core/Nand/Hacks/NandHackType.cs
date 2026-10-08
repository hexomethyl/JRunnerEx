namespace JRunner.Core.Nand.Hacks;

/// <summary>
/// The hack type indicated by parsed NAND bootloader evidence.
/// </summary>
public enum NandHackType
{
    /// <summary>
    /// No recognized legacy CB-table entry or CB_X-specific hack evidence is available.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The legacy table prefers JTAG.
    /// </summary>
    Jtag = 1,

    /// <summary>
    /// The legacy table prefers first-generation glitch.
    /// </summary>
    Glitch = 2,

    /// <summary>
    /// The legacy table prefers second-generation glitch.
    /// </summary>
    Glitch2 = 3,

    /// <summary>
    /// The legacy table prefers DevGL.
    /// </summary>
    DevGl = 4,

    /// <summary>
    /// A positive CB_X build indicates RGH3.
    /// </summary>
    Rgh3 = 5,

    /// <summary>
    /// The legacy CB_X build <c>42069</c> indicates RGH 1.3.
    /// </summary>
    Rgh13 = 6,

    /// <summary>
    /// A second-generation glitch image whose inspected virtual-fuse marker triggers the legacy
    /// Glitch2m selection rule.
    /// </summary>
    Glitch2m = 7,
}

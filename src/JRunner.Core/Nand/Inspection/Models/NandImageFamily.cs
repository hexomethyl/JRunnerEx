namespace JRunner.Core.Nand.Inspection.Models;

/// <summary>
/// Identifies the image family an output must directly prove, independently of legacy hack compatibility.
/// </summary>
public enum NandImageFamily
{
    /// <summary>A retail image.</summary>
    Retail,

    /// <summary>A JTAG image.</summary>
    Jtag,

    /// <summary>A Glitch image.</summary>
    Glitch,

    /// <summary>A Glitch2 image.</summary>
    Glitch2,

    /// <summary>A Glitch2m image.</summary>
    Glitch2m,

    /// <summary>A DevGL image.</summary>
    DevGl,

    /// <summary>A development-kit image.</summary>
    Devkit,

    /// <summary>A test-kit image.</summary>
    Testkit,

    /// <summary>An RGH3 image.</summary>
    Rgh3,

    /// <summary>An RGH 1.3 image.</summary>
    Rgh13,
}

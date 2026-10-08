using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Physical;
using Xunit;

namespace JRunner.Core.Tests.Nand.Models;

public sealed class ConsoleCatalogTests
{
    [Fact]
    public void Catalog_preserves_every_canonical_legacy_console_mapping()
    {
        Assert.Equal(ConsoleCatalog.LastLegacyId - ConsoleCatalog.FirstLegacyId + 1, ConsoleCatalog.Count);
        Assert.Equal(17, ConsoleCatalog.All.Length);

        foreach (var expected in ExpectedConsoles)
        {
            var actual = ConsoleCatalog.Get(expected.Id);

            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal((int)expected.Id, actual.LegacyId);
            Assert.Equal(expected.CanonicalName, actual.CanonicalName);
            Assert.Equal(expected.XeBuildName, actual.XeBuildName);
            Assert.Equal(expected.IniName, actual.IniName);
            Assert.Equal(expected.NandSizeMegabytes, actual.NandSizeMegabytes);
            Assert.Equal(expected.LogicalNandSize, actual.LogicalNandSize);
            Assert.Equal(expected.Layout, actual.Layout);
            Assert.Equal(expected.Layout is { } layout ? (int)layout : -1, actual.LegacyLayoutId);
            Assert.Equal(expected.LogicalNandSize.GetByteLength(), actual.LogicalNandByteLength);

            Assert.True(ConsoleCatalog.TryGet((int)expected.Id, out var byNumericId));
            Assert.Same(actual, byNumericId);
            Assert.True(ConsoleCatalog.TryGetByCanonicalName(expected.CanonicalName.ToLowerInvariant(), out var byName));
            Assert.Same(actual, byName);
        }
    }

    [Fact]
    public void Catalog_rejects_unknown_console_identifiers_and_names()
    {
        Assert.False(ConsoleCatalog.TryGet(0, out var beforeRange));
        Assert.Null(beforeRange);
        Assert.False(ConsoleCatalog.TryGet(18, out var afterRange));
        Assert.Null(afterRange);
        Assert.False(ConsoleCatalog.TryGetByCanonicalName("not-a-console", out var byUnknownName));
        Assert.Null(byUnknownName);
        Assert.False(ConsoleCatalog.TryGetByCanonicalName(null, out var byNullName));
        Assert.Null(byNullName);

        Assert.Throws<ArgumentOutOfRangeException>(() => ConsoleCatalog.Get(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConsoleCatalog.Get((ConsoleId)18));
    }

    [Fact]
    public void Logical_size_classifications_preserve_legacy_values_and_capacities()
    {
        Assert.Equal(0x0000, (int)NandLogicalSize.S0);
        Assert.Equal(0x0400, (int)NandLogicalSize.S16);
        Assert.Equal(0x1000, (int)NandLogicalSize.S64);
        Assert.Equal(0x4000, (int)NandLogicalSize.S256);
        Assert.Equal(0x8000, (int)NandLogicalSize.S512);

        Assert.Equal(0L, NandLogicalSize.S0.GetByteLength());
        Assert.Equal(16L * 1024 * 1024, NandLogicalSize.S16.GetByteLength());
        Assert.Equal(64L * 1024 * 1024, NandLogicalSize.S64.GetByteLength());
        Assert.Equal(256L * 1024 * 1024, NandLogicalSize.S256.GetByteLength());
        Assert.Equal(512L * 1024 * 1024, NandLogicalSize.S512.GetByteLength());
    }

    private static IReadOnlyList<ExpectedConsole> ExpectedConsoles { get; } =
    [
        new(ConsoleId.Trinity16Mb, "Trinity 16MB", "trinity", "trinity", 16, NandLogicalSize.S16, NandLegacyLayout.Layout1),
        new(ConsoleId.Falcon16Mb, "Falcon 16MB", "falcon", "falcon", 16, NandLogicalSize.S16, NandLegacyLayout.Layout0),
        new(ConsoleId.Zephyr16Mb, "Zephyr 16MB", "zephyr", "zephyr", 16, NandLogicalSize.S16, NandLegacyLayout.Layout0),
        new(ConsoleId.Jasper16Mb, "Jasper 16MB", "jasper", "jasper", 16, NandLogicalSize.S16, NandLegacyLayout.Layout1),
        new(ConsoleId.JasperXsb, "Jasper XSB", "jaspersb", "jasper", 16, NandLogicalSize.S16, NandLegacyLayout.Layout0),
        new(ConsoleId.JasperBigBlock, "Jasper BB", "jasperbb", "jasper", 64, NandLogicalSize.S64, NandLegacyLayout.Layout2),
        new(ConsoleId.Xenon64Mb, "Xenon 64MB", "xenon", "xenon", 64, NandLogicalSize.S64, NandLegacyLayout.Layout0),
        new(ConsoleId.Xenon16Mb, "Xenon 16MB", "xenon", "xenon", 16, NandLogicalSize.S16, NandLegacyLayout.Layout0),
        new(ConsoleId.CoronaBigBlock, "Corona BB", "coronabb", "corona", 64, NandLogicalSize.S64, NandLegacyLayout.Layout2),
        new(ConsoleId.Corona16Mb, "Corona 16MB", "corona", "corona", 16, NandLogicalSize.S16, NandLegacyLayout.Layout1),
        new(ConsoleId.Corona4Gb, "Corona 4GB", "corona4g", "corona", 0, NandLogicalSize.S0, null),
        new(ConsoleId.TrinityBigBlock, "Trinity BB", "trinitybb", "trinity", 64, NandLogicalSize.S64, NandLegacyLayout.Layout2),
        new(ConsoleId.Zephyr64Mb, "Zephyr 64MB", "zephyr", "zephyr", 64, NandLogicalSize.S64, NandLegacyLayout.Layout0),
        new(ConsoleId.Falcon64Mb, "Falcon 64MB", "falcon", "falcon", 64, NandLogicalSize.S64, NandLegacyLayout.Layout0),
        new(ConsoleId.Winchester16Mb, "Winchester 16MB", "winchester", "winchester", 64, NandLogicalSize.S16, NandLegacyLayout.Layout1),
        new(ConsoleId.Winchester4Gb, "Winchester 4GB", "winchester4g", "winchester", 64, NandLogicalSize.S0, null),
        new(ConsoleId.WinchesterBigBlock, "Winchester BB", "winchesterbb", "winchester", 64, NandLogicalSize.S64, NandLegacyLayout.Layout2),
    ];

    private sealed record ExpectedConsole(
        ConsoleId Id,
        string CanonicalName,
        string XeBuildName,
        string IniName,
        int NandSizeMegabytes,
        NandLogicalSize LogicalNandSize,
        NandLegacyLayout? Layout);
}

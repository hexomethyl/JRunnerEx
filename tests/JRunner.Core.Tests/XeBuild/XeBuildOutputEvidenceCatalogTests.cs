using System.Security.Cryptography;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Security;
using JRunner.Core.XeBuild;
using JRunner.Core.XeBuild.Preparation;
using Xunit;

namespace JRunner.Core.Tests.XeBuild;

public sealed class XeBuildOutputEvidenceCatalogTests
{
    [Theory]
    [InlineData("retail", XeBuildHackType.Retail, NandImageFamily.Retail)]
    [InlineData("glitch", XeBuildHackType.Glitch, NandImageFamily.Glitch)]
    [InlineData("jtag", XeBuildHackType.Jtag, NandImageFamily.Jtag)]
    [InlineData("glitch2", XeBuildHackType.Glitch2, NandImageFamily.Glitch2)]
    [InlineData("glitch2m", XeBuildHackType.Glitch2m, NandImageFamily.Glitch2m)]
    [InlineData("devgl", XeBuildHackType.DevGl, NandImageFamily.DevGl)]
    [InlineData("devgl16", XeBuildHackType.DevGl16, NandImageFamily.DevGl)]
    [InlineData("devkit", XeBuildHackType.Devkit, NandImageFamily.Devkit)]
    [InlineData("devkit16", XeBuildHackType.Devkit16, NandImageFamily.Devkit)]
    [InlineData("testkit", XeBuildHackType.Testkit, NandImageFamily.Testkit)]
    [InlineData("testkit16", XeBuildHackType.Testkit16, NandImageFamily.Testkit)]
    public void Every_canonical_type_parses_but_requires_direct_evidence_unavailable_without_rgh3(
        string canonicalName,
        XeBuildHackType hackType,
        NandImageFamily requiredFamily)
    {
        Assert.True(XeBuildHackTypeCatalog.TryGetByCanonicalName(canonicalName, out XeBuildHackType parsed));
        Assert.Equal(hackType, parsed);
        var target = new XeBuildBuildTarget("Falcon 16MB", 17559, canonicalName.ToUpperInvariant());
        XeBuildOutputEvidenceCapability capability = XeBuildOutputEvidenceCatalog.Get(target.HackType);

        Assert.Equal(canonicalName, target.TypeCanonicalName);
        Assert.Equal(hackType, capability.HackType);
        Assert.Equal(requiredFamily, capability.RequiredFamily);
        Assert.False(capability.IsAvailable);
        Assert.Null(capability.ReviewedManifest);
        Assert.Same(capability, XeBuildOutputEvidenceCatalog.Get(hackType));
        AssertUnavailable(target);
    }

    [Fact]
    public void Canonical_syntax_exposes_all_eleven_types_independently_of_execution_availability()
    {
        Assert.Equal(11, XeBuildHackTypeCatalog.All.Length);
        Assert.Equal(Enum.GetValues<XeBuildHackType>(), XeBuildHackTypeCatalog.All);
        Assert.All(XeBuildHackTypeCatalog.All, hackType =>
        {
            Assert.False(XeBuildOutputEvidenceCatalog.Get(hackType).IsAvailable);
            Assert.Null(XeBuildOutputEvidenceCatalog.Get(hackType).ReviewedManifest);
        });
    }

    [Theory]
    [MemberData(nameof(CanonicalTypes))]
    public void Rgh3_enables_only_Glitch2_and_Glitch2m_with_the_real_reviewed_output_manifest(XeBuildHackType hackType)
    {
        var target = new XeBuildBuildTarget(
            "Falcon 16MB",
            17559,
            XeBuildHackTypeCatalog.GetCanonicalName(hackType),
            new XeBuildBuildOptions(rgh3: true));
        XeBuildOutputEvidenceCapability capability = XeBuildOutputEvidenceCatalog.Get(hackType, rgh3: true);

        Assert.Equal(hackType, capability.HackType);
        Assert.Equal(NandImageFamily.Rgh3, capability.RequiredFamily);
        Assert.Same(capability, XeBuildOutputEvidenceCatalog.Get(hackType, rgh3: true));
        if (hackType is XeBuildHackType.Glitch2 or XeBuildHackType.Glitch2m)
        {
            Assert.True(capability.IsAvailable);
            NandDirectOutputEvidenceManifest manifest = Assert.IsType<NandDirectOutputEvidenceManifest>(capability.ReviewedManifest);
            Assert.Same(NandRgh3OutputEvidence.ReviewedManifest, manifest);
            Assert.NotEmpty(manifest.Fingerprints);
            Assert.All(manifest.Fingerprints, fingerprint =>
            {
                Assert.Equal(NandImageFamily.Rgh3, fingerprint.Family);
                Assert.Equal(NandBootloaderStageKind.CB_X, fingerprint.Stage);
            });
            XeBuildOutputEvidenceCatalog.EnsureAvailable(target);
        }
        else
        {
            Assert.False(capability.IsAvailable);
            Assert.Null(capability.ReviewedManifest);
            AssertUnavailable(target);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(12)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Unknown_enum_values_are_rejected_even_when_rgh3_is_requested(int unknown)
    {
        ArgumentOutOfRangeException normal = Assert.Throws<ArgumentOutOfRangeException>(() =>
            XeBuildOutputEvidenceCatalog.Get((XeBuildHackType)unknown));
        ArgumentOutOfRangeException rgh3 = Assert.Throws<ArgumentOutOfRangeException>(() =>
            XeBuildOutputEvidenceCatalog.Get((XeBuildHackType)unknown, rgh3: true));

        Assert.Equal("hackType", normal.ParamName);
        Assert.Equal("hackType", rgh3.ParamName);
    }

    [Fact]
    public void Dependency_closure_planning_remains_usable_without_a_reviewed_corpus()
    {
        var target = new XeBuildBuildTarget("Trinity 16MB", 17559, "glitch2");
        var request = new XeBuildRequest(
            "not-opened-support",
            new XeBuildSourceContext(
                "not-opened-input.bin",
                0x01000000,
                SHA256.HashData(Array.Empty<byte>()),
                ConsoleId.Trinity16Mb),
            CpuKey.Parse("00112233445566778899AABBCCDDEEFF"),
            "not-reserved-output.bin",
            target);
        string[] supportFiles =
        [
            "xeBuild/xeBuild.exe",
            "xeBuild/options.ini",
            "xeBuild/common/dependency.bin",
            "xeBuild/17559/_glitch2.ini",
        ];

        XeBuildPreparedPlan plan = XeBuildPreparationService.Prepare(request, new XeBuildSupportIndex(supportFiles));

        Assert.Equal(ConsoleId.Trinity16Mb, plan.Console.Id);
        Assert.Equal(17559, plan.DashboardVersion);
        Assert.Equal(XeBuildHackType.Glitch2, plan.RequestedHackType);
        Assert.Equal(supportFiles, plan.RequiredSupportFiles);
        AssertUnavailable(target);
    }

    public static IEnumerable<object[]> CanonicalTypes() =>
        XeBuildHackTypeCatalog.All.Select(static hackType => new object[] { hackType });

    private static void AssertUnavailable(XeBuildBuildTarget target)
    {
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            XeBuildOutputEvidenceCatalog.EnsureAvailable(target));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal(4, (int)failure.Code);
        Assert.Equal("xebuild-output-evidence-unavailable", failure.Kind);
        Assert.Contains(target.TypeCanonicalName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("reviewed decrypted-stage output fingerprint/signature corpus", failure.Message, StringComparison.Ordinal);
        Assert.Contains("reviewed JRunnerEx source update", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Installing support payloads or Wine alone cannot resolve", failure.Message, StringComparison.Ordinal);
        if (target.Options.Rgh3)
        {
            Assert.Contains("with RGH3", failure.Message, StringComparison.Ordinal);
        }
    }
}

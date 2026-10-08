using JRunner.Core.Contracts;
using JRunner.Core.XeBuild.Algorithms;
using Xunit;

namespace JRunner.Core.Tests.XeBuild.Algorithms;

public sealed class XeBuildPatchSetReaderTests
{
    [Fact]
    public void GetSectionLength_includes_an_unaligned_terminator()
    {
        byte[] patchData = [0x12, 0x34, 0xFF, 0xFF, 0xFF, 0xFF, 0x56];

        var length = XeBuildPatchSetReader.GetSectionLength(patchData, 0);

        Assert.Equal(6, length);
    }

    [Fact]
    public void ReadAll_extracts_each_legacy_patch_set_with_its_terminator()
    {
        byte[] twoBl = [0x21, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] fourBl = [0x43, 0x44, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] kernelHypervisor = [0x65, 0x66, 0x67, 0xFF, 0xFF, 0xFF, 0xFF];
        var patchData = twoBl.Concat(fourBl).Concat(kernelHypervisor).ToArray();

        var sections = XeBuildPatchSetReader.ReadAll(patchData);
        patchData[0] = 0x99;


        Assert.Equal(twoBl, sections.TwoBl.Data.ToArray());
        Assert.Equal(fourBl, sections.FourBl.Data.ToArray());
        Assert.Equal(kernelHypervisor, sections.KernelHypervisor.Data.ToArray());
        Assert.Equal(0, sections.TwoBl.Offset);
        Assert.Equal(twoBl.Length, sections.FourBl.Offset);
        Assert.Equal(twoBl.Length + fourBl.Length, sections.KernelHypervisor.Offset);
    }

    [Fact]
    public void Extract_4bl_requires_preceding_section_but_not_a_later_section()
    {
        byte[] twoBl = [0x01, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] fourBl = [0x02, 0x03, 0xFF, 0xFF, 0xFF, 0xFF];
        var partialPatchData = twoBl.Concat(fourBl).ToArray();

        var section = XeBuildPatchSetReader.Extract(partialPatchData, XeBuildPatchSectionKind.FourBl);

        Assert.Equal(fourBl, section.Data.ToArray());
        Assert.Equal(twoBl.Length, section.Offset);
    }

    [Fact]
    public void ReadAll_rejects_an_unterminated_required_section()
    {
        byte[] patchData =
        [
            0x21, 0xFF, 0xFF, 0xFF, 0xFF,
            0x43, 0xFF, 0xFF, 0xFF, 0xFF,
            0x65, 0x66, 0x67,
        ];

        var exception = Assert.Throws<OperationFailureException>(
            () => XeBuildPatchSetReader.ReadAll(patchData));

        Assert.Equal("unterminated-xebuild-patch-section", exception.Kind);
        Assert.Equal(ExitCode.InvalidData, exception.Code);
    }

    [Fact]
    public void GetSectionLength_rejects_an_offset_outside_the_payload()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => XeBuildPatchSetReader.GetSectionLength(
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
                5));
    }
}

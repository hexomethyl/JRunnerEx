using System.Runtime.InteropServices;
using System.Security.Cryptography;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.XeBuild;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class XeBuildSourceFileTests
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectoryMode = PrivateFileMode | UnixFileMode.UserExecute;
    private const UnixFileMode StickyWritableDirectoryMode = PrivateDirectoryMode |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.StickyBit;
    private static readonly byte[] OriginalContent = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];

    [Theory]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.OtherWrite)]
    [InlineData(UnixFileMode.GroupWrite | UnixFileMode.StickyBit)]
    [InlineData(UnixFileMode.OtherWrite | UnixFileMode.StickyBit)]
    public async Task Group_or_other_writable_regular_source_is_rejected_without_changing_it(UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        await WriteSourceAsync(inputPath, OriginalContent);
        UnixFileMode unsafeMode = PrivateFileMode | unsafeBits;
        File.SetUnixFileMode(inputPath, unsafeMode);
        try
        {
            AssertUnsafeOpen(inputPath);

            Assert.Equal(OriginalContent, await File.ReadAllBytesAsync(inputPath));
            Assert.Equal(unsafeMode, File.GetUnixFileMode(inputPath));
            Assert.Equal(new[] { inputPath }, Directory.GetFileSystemEntries(temporary.Path));
        }
        finally
        {
            File.SetUnixFileMode(inputPath, PrivateFileMode);
        }
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite, false)]
    [InlineData(UnixFileMode.OtherWrite, false)]
    [InlineData(UnixFileMode.GroupWrite, true)]
    [InlineData(UnixFileMode.OtherWrite, true)]
    public async Task Writable_non_sticky_parent_or_ancestor_is_rejected(
        UnixFileMode unsafeBits,
        bool unsafeAncestor)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string ancestor = Path.Combine(temporary.Path, "ancestor");
        string parent = Path.Combine(ancestor, "parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(ancestor, PrivateDirectoryMode);
        File.SetUnixFileMode(parent, PrivateDirectoryMode);
        string inputPath = Path.Combine(parent, "input.bin");
        await WriteSourceAsync(inputPath, OriginalContent);
        string unsafeDirectory = unsafeAncestor ? ancestor : parent;
        UnixFileMode unsafeMode = PrivateDirectoryMode | unsafeBits;
        File.SetUnixFileMode(unsafeDirectory, unsafeMode);
        try
        {
            AssertUnsafeOpen(inputPath);

            Assert.Equal(OriginalContent, await File.ReadAllBytesAsync(inputPath));
            Assert.Equal(unsafeMode, File.GetUnixFileMode(unsafeDirectory));
            Assert.Equal(new[] { inputPath }, Directory.GetFileSystemEntries(parent));
        }
        finally
        {
            File.SetUnixFileMode(unsafeDirectory, PrivateDirectoryMode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Symlink_source_or_ancestor_is_not_followed(bool ancestorLink)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string parent = Path.Combine(temporary.Path, "actual-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, PrivateDirectoryMode);
        string inputPath = Path.Combine(parent, "input.bin");
        await WriteSourceAsync(inputPath, OriginalContent);
        string link = Path.Combine(temporary.Path, "source-link");
        string selectedPath;
        if (ancestorLink)
        {
            Directory.CreateSymbolicLink(link, parent);
            selectedPath = Path.Combine(link, "input.bin");
        }
        else
        {
            File.CreateSymbolicLink(link, inputPath);
            selectedPath = link;
        }

        AssertUnsafeOpen(selectedPath);

        Assert.Equal(OriginalContent, await File.ReadAllBytesAsync(inputPath));
        Assert.Equal(PrivateFileMode, File.GetUnixFileMode(inputPath));
        Assert.Equal(ancestorLink ? parent : inputPath, new FileInfo(link).LinkTarget);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("ancestor")]
    [InlineData("sticky-directory")]
    [InlineData("sticky-child-source")]
    [InlineData("sticky-child-directory")]
    public async Task Foreign_owned_source_or_path_component_is_rejected_when_root_can_create_the_fixture(
        string foreignOwnershipKind)
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string ancestor = Path.Combine(temporary.Path, "ancestor");
        string parent = Path.Combine(ancestor, "parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(ancestor, PrivateDirectoryMode);
        File.SetUnixFileMode(parent, PrivateDirectoryMode);
        string inputPath = Path.Combine(parent, "input.bin");
        await WriteSourceAsync(inputPath, OriginalContent);
        string foreignOwnedPath;
        if (foreignOwnershipKind == "ancestor")
        {
            foreignOwnedPath = ancestor;
        }
        else if (foreignOwnershipKind == "sticky-directory")
        {
            File.SetUnixFileMode(parent, StickyWritableDirectoryMode);
            foreignOwnedPath = parent;
        }
        else if (foreignOwnershipKind == "sticky-child-source")
        {
            File.SetUnixFileMode(parent, StickyWritableDirectoryMode);
            foreignOwnedPath = inputPath;
        }
        else if (foreignOwnershipKind == "sticky-child-directory")
        {
            File.SetUnixFileMode(ancestor, StickyWritableDirectoryMode);
            foreignOwnedPath = parent;
        }
        else
        {
            foreignOwnedPath = inputPath;
        }

        UnixFileMode originalMode = File.GetUnixFileMode(foreignOwnedPath);
        Assert.Equal(0, ChangeOwner(foreignOwnedPath, 65534, uint.MaxValue));
        try
        {
            AssertUnsafeOpen(inputPath);

            Assert.Equal(OriginalContent, await File.ReadAllBytesAsync(inputPath));
            Assert.Equal(originalMode, File.GetUnixFileMode(foreignOwnedPath));
            Assert.Equal(new[] { inputPath }, Directory.GetFileSystemEntries(parent));
        }
        finally
        {
            Assert.Equal(0, ChangeOwner(foreignOwnedPath, 0, uint.MaxValue));
            File.SetUnixFileMode(ancestor, PrivateDirectoryMode);
            File.SetUnixFileMode(parent, PrivateDirectoryMode);
            File.SetUnixFileMode(inputPath, PrivateFileMode);
        }
    }

    [Fact]
    public async Task Owner_only_regular_source_in_private_ancestry_is_opened_and_keeps_its_descriptor_snapshot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string parent = Path.Combine(temporary.Path, "parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, PrivateDirectoryMode);
        string inputPath = Path.Combine(parent, "input.bin");
        await WriteSourceAsync(inputPath, OriginalContent);

        await using XeBuildSourceFile source = XeBuildSourceFile.Open(Path.Combine(parent, ".", "input.bin"));

        Assert.Equal(inputPath, source.FullPath);
        Assert.Equal(OriginalContent.LongLength, source.ByteLength);
        Assert.True(source.Stream.CanRead);
        Assert.True(source.Stream.CanSeek);
        Assert.False(source.Stream.SafeFileHandle.IsInvalid);
        Assert.Equal(SHA256.HashData(OriginalContent), await SHA256.HashDataAsync(source.Stream));
        source.EnsureUnchanged();
        Assert.Equal(PrivateFileMode, File.GetUnixFileMode(inputPath));
        Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(parent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trusted_owned_sticky_writable_boundary_accepts_a_trusted_owned_file_or_child_directory(bool childDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string stickyParent = Path.Combine(temporary.Path, "sticky-parent");
        Directory.CreateDirectory(stickyParent);
        File.SetUnixFileMode(stickyParent, StickyWritableDirectoryMode);
        try
        {
            string sourceParent = stickyParent;
            if (childDirectory)
            {
                sourceParent = Path.Combine(stickyParent, "trusted-child");
                Directory.CreateDirectory(sourceParent);
                File.SetUnixFileMode(sourceParent, PrivateDirectoryMode);
            }

            string inputPath = Path.Combine(sourceParent, "input.bin");
            await WriteSourceAsync(inputPath, OriginalContent);
            await using XeBuildSourceFile source = XeBuildSourceFile.Open(inputPath);

            Assert.Equal(SHA256.HashData(OriginalContent), await SHA256.HashDataAsync(source.Stream));
            source.EnsureUnchanged();
            Assert.Equal(StickyWritableDirectoryMode, File.GetUnixFileMode(stickyParent));
            Assert.Equal(PrivateFileMode, File.GetUnixFileMode(inputPath));
        }
        finally
        {
            File.SetUnixFileMode(stickyParent, PrivateDirectoryMode);
        }
    }

    [Fact]
    public async Task Owner_only_regular_source_directly_in_tmp_is_accepted_under_its_sticky_boundary()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string inputPath = Path.Combine("/tmp", $"jrunner-source-file-direct-{Guid.NewGuid():N}.bin");
        try
        {
            await WriteSourceAsync(inputPath, OriginalContent);
            Assert.NotEqual((UnixFileMode)0, File.GetUnixFileMode("/tmp") & UnixFileMode.StickyBit);
            await using XeBuildSourceFile source = XeBuildSourceFile.Open(inputPath);

            Assert.Equal(inputPath, source.FullPath);
            Assert.Equal(OriginalContent.LongLength, source.ByteLength);
            Assert.Equal(SHA256.HashData(OriginalContent), await SHA256.HashDataAsync(source.Stream));
            source.EnsureUnchanged();
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Fact]
    public void Directory_is_not_a_regular_source()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();

        AssertUnsafeOpen(temporary.Path);
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path));
    }

    [Fact]
    public async Task Named_pipe_is_rejected_without_waiting_for_a_writer()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "source.fifo");
        Assert.Equal(0, MakeFifo(inputPath, 0x180));
        File.SetUnixFileMode(inputPath, PrivateFileMode);

        await Task.Run(() => AssertUnsafeOpen(inputPath)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PrivateFileMode, File.GetUnixFileMode(inputPath));
    }

    [Fact]
    public async Task Same_length_in_place_write_is_rejected_by_the_held_descriptor_snapshot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        await WriteSourceAsync(inputPath, OriginalContent);
        await using XeBuildSourceFile source = XeBuildSourceFile.Open(inputPath);
        source.EnsureUnchanged();

        // Same-UID mutation is a deterministic recheck probe, not a same-UID adversary guarantee.
        await using (FileStream mutation = new(inputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            mutation.Position = OriginalContent.Length - 1;
            mutation.WriteByte((byte)(OriginalContent[^1] ^ 0xFF));
            mutation.Flush(flushToDisk: true);
        }

        Assert.Equal(source.ByteLength, new FileInfo(inputPath).Length);
        AssertChanged(source);
        Assert.Equal(PrivateFileMode, File.GetUnixFileMode(inputPath));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Size_change_is_rejected_by_the_held_descriptor_snapshot(int lengthDelta)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        await WriteSourceAsync(inputPath, OriginalContent);
        await using XeBuildSourceFile source = XeBuildSourceFile.Open(inputPath);
        await using (FileStream mutation = new(inputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            mutation.SetLength(OriginalContent.Length + lengthDelta);
            mutation.Flush(flushToDisk: true);
        }

        Assert.Equal(OriginalContent.LongLength, source.ByteLength);
        Assert.Equal(OriginalContent.LongLength + lengthDelta, new FileInfo(inputPath).Length);
        AssertChanged(source);
    }

    [Fact]
    public async Task Safe_mode_metadata_change_is_rejected_without_any_content_or_size_change()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        await WriteSourceAsync(inputPath, OriginalContent);
        await using XeBuildSourceFile source = XeBuildSourceFile.Open(inputPath);
        File.SetUnixFileMode(inputPath, UnixFileMode.UserRead);
        try
        {
            AssertChanged(source);
            Assert.Equal(source.ByteLength, new FileInfo(inputPath).Length);
            Assert.Equal(SHA256.HashData(OriginalContent), await SHA256.HashDataAsync(source.Stream));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(inputPath));
        }
        finally
        {
            File.SetUnixFileMode(inputPath, PrivateFileMode);
        }
    }

    [Fact]
    public async Task Full_digest_stays_bound_to_the_original_open_descriptor_after_same_length_path_replacement()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        string replacementPath = Path.Combine(temporary.Path, "replacement.bin");
        byte[] replacementContent = [0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11];
        await WriteSourceAsync(inputPath, OriginalContent);
        await WriteSourceAsync(replacementPath, replacementContent);
        await using XeBuildSourceFile source = XeBuildSourceFile.Open(inputPath);
        var inspectedContent = new byte[OriginalContent.Length];
        await source.Stream.ReadExactlyAsync(inspectedContent);
        Assert.Equal(OriginalContent, inspectedContent);
        source.Stream.Position = 0;

        File.Move(replacementPath, inputPath, overwrite: true);
        byte[] descriptorSha256 = await SHA256.HashDataAsync(source.Stream);
        var context = new XeBuildSourceContext(source.FullPath, source.ByteLength, descriptorSha256);

        Assert.Equal(SHA256.HashData(OriginalContent), descriptorSha256);
        Assert.True(context.MatchesContentSha256(SHA256.HashData(OriginalContent)));
        Assert.False(context.MatchesContentSha256(SHA256.HashData(replacementContent)));
        Assert.Equal(source.ByteLength, new FileInfo(inputPath).Length);
        Assert.Equal(replacementContent, await File.ReadAllBytesAsync(inputPath));
        AssertChanged(source);
        Assert.False(File.Exists(replacementPath));
    }

    [Fact]
    public async Task Disposing_the_source_owns_and_closes_the_stream_and_descriptor()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        await WriteSourceAsync(inputPath, OriginalContent);
        await using XeBuildSourceFile source = XeBuildSourceFile.Open(inputPath);
        FileStream stream = source.Stream;
        var descriptor = stream.SafeFileHandle;

        await source.DisposeAsync();

        Assert.True(descriptor.IsClosed);
        Assert.False(stream.CanRead);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }

    [Fact]
    public void Non_Linux_source_open_fails_closed_before_even_a_missing_path_is_opened()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        string inputPath = Path.Combine(Path.GetTempPath(), $"jrunner-source-unavailable-{Guid.NewGuid():N}", "input.bin");
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() => XeBuildSourceFile.Open(inputPath));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("xebuild-input-safety-unavailable", failure.Kind);
        Assert.False(Directory.Exists(Path.GetDirectoryName(inputPath)));
        Assert.DoesNotContain(inputPath, failure.Message, StringComparison.Ordinal);
    }

    private static async Task WriteSourceAsync(string path, byte[] content)
    {
        await File.WriteAllBytesAsync(path, content);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }

    private static void AssertUnsafeOpen(string inputPath)
    {
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() => XeBuildSourceFile.Open(inputPath));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("xebuild-input-unsafe", failure.Kind);
        Assert.DoesNotContain(inputPath, failure.Message, StringComparison.Ordinal);
    }

    private static void AssertChanged(XeBuildSourceFile source)
    {
        OperationFailureException failure = Assert.Throws<OperationFailureException>(source.EnsureUnchanged);

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("xebuild-input-changed", failure.Kind);
        Assert.DoesNotContain(source.FullPath, failure.Message, StringComparison.Ordinal);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, uint mode);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner(string path, uint owner, uint group);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine("/tmp", $"jrunner-source-file-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(Path, PrivateDirectoryMode);
            }
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

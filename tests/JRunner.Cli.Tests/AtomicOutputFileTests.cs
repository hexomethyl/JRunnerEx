using System.Runtime.InteropServices;
using System.Text;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class AtomicOutputFileTests
{
    [Fact]
    public async Task Existing_destination_requires_force_and_survives_an_uncommitted_output()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var destinationPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");

        var rejection = Assert.Throws<OperationFailureException>(
            () => AtomicOutputFile.Create(destinationPath, force: false));
        Assert.Equal(ExitCode.Usage, rejection.Code);
        Assert.Equal("destination-exists", rejection.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));

        string temporaryPath;
        await using (var output = AtomicOutputFile.Create(destinationPath, force: true))
        {
            temporaryPath = output.TemporaryPath;
            await WritePayloadAsync(output, "replacement");
        }

        Assert.False(File.Exists(temporaryPath));
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));

        await using (var output = AtomicOutputFile.Create(destinationPath, force: true))
        {
            await WritePayloadAsync(output, "replacement");
            await output.CompleteAsync();
        }

        Assert.Equal("replacement", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public void Missing_destination_directory_is_a_usage_failure()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var destinationPath = Path.Combine(temporaryDirectory.Path, "missing", "output.bin");

        var failure = Assert.Throws<OperationFailureException>(
            () => AtomicOutputFile.Create(destinationPath, force: false));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("destination-directory-missing", failure.Kind);
    }

    [Fact]
    public async Task Cancellation_removes_the_temporary_file_without_replacing_the_destination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var destinationPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");

        string temporaryPath;
        using var cancellationSource = new CancellationTokenSource();
        await using (var output = AtomicOutputFile.Create(destinationPath, force: true))
        {
            temporaryPath = output.TemporaryPath;
            await WritePayloadAsync(output, "replacement");
            cancellationSource.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => output.CompleteAsync(cancellationSource.Token).AsTask());
        }

        Assert.False(File.Exists(temporaryPath));
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task A_closed_writer_still_publishes_only_its_original_file()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await using var output = AtomicOutputFile.Create(destinationPath, force: false);
        await WritePayloadAsync(output, "completed");
        output.Stream.Dispose();

        await output.CompleteAsync();

        Assert.Equal("completed", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(output.TemporaryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replaced_or_symlinked_temporary_file_is_not_published_after_the_writer_closes(bool useSymlink)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        string protectedPath = temporaryDirectory.File("protected.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await File.WriteAllTextAsync(protectedPath, "protected");
        await using var output = AtomicOutputFile.Create(destinationPath, force: true);
        await WritePayloadAsync(output, "completed");
        output.Stream.Dispose();
        File.Delete(output.TemporaryPath);
        if (useSymlink)
        {
            File.CreateSymbolicLink(output.TemporaryPath, protectedPath);
        }
        else
        {
            await File.WriteAllTextAsync(output.TemporaryPath, "substitute");
        }

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.CompleteAsync().AsTask());

        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.Equal("output-path-changed", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal("protected", await File.ReadAllTextAsync(protectedPath));
        Assert.False(File.Exists(output.TemporaryPath));
        Assert.Null(new FileInfo(output.TemporaryPath).LinkTarget);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Directory_or_ancestor_replacement_cleans_only_the_bound_directory(bool replaceAncestor, bool useSymlink)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string parentPath = temporaryDirectory.File("parent");
        string outputDirectory = Path.Combine(parentPath, "output");
        CreatePrivateDirectory(parentPath);
        CreatePrivateDirectory(outputDirectory);
        string destinationPath = Path.Combine(outputDirectory, "output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await using var output = AtomicOutputFile.Create(destinationPath, force: true);
        await WritePayloadAsync(output, "completed");

        string changedPath = replaceAncestor ? parentPath : outputDirectory;
        string heldPath = temporaryDirectory.File("held");
        Directory.Move(changedPath, heldPath);
        string heldOutputDirectory = replaceAncestor ? Path.Combine(heldPath, "output") : heldPath;
        string replacementPath = useSymlink ? temporaryDirectory.File("impostor") : changedPath;
        CreatePrivateDirectory(replacementPath);
        string replacementOutputDirectory = replaceAncestor ? Path.Combine(replacementPath, "output") : replacementPath;
        if (replaceAncestor)
        {
            CreatePrivateDirectory(replacementOutputDirectory);
        }
        if (useSymlink)
        {
            Directory.CreateSymbolicLink(changedPath, replacementPath);
        }
        string impostorDestination = Path.Combine(replacementOutputDirectory, "output.bin");
        string impostorTemporary = Path.Combine(replacementOutputDirectory, Path.GetFileName(output.TemporaryPath));
        await File.WriteAllTextAsync(impostorDestination, "unrelated destination");
        await File.WriteAllTextAsync(impostorTemporary, "unrelated temporary");

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.CompleteAsync().AsTask());

        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.Equal("output-path-changed", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(heldOutputDirectory, "output.bin")));
        Assert.False(File.Exists(Path.Combine(heldOutputDirectory, Path.GetFileName(output.TemporaryPath))));
        Assert.Equal("unrelated destination", await File.ReadAllTextAsync(impostorDestination));
        Assert.Equal("unrelated temporary", await File.ReadAllTextAsync(impostorTemporary));
    }

    [Fact]
    public async Task A_destination_created_after_preflight_wins_without_force()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await using var output = AtomicOutputFile.Create(destinationPath, force: false);
        await WritePayloadAsync(output, "completed");
        await File.WriteAllTextAsync(destinationPath, "competitor");

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.CompleteAsync().AsTask());

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("destination-exists", failure.Kind);
        Assert.Equal("competitor", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(output.TemporaryPath));
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite, false)]
    [InlineData(UnixFileMode.OtherWrite, false)]
    [InlineData(UnixFileMode.GroupWrite, true)]
    [InlineData(UnixFileMode.OtherWrite, true)]
    public void Writable_output_directory_or_nonsticky_ancestor_requires_a_private_directory(UnixFileMode unsafeMode, bool ancestor)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string childPath = temporaryDirectory.File("child");
        CreatePrivateDirectory(childPath);
        File.SetUnixFileMode(ancestor ? temporaryDirectory.Path : childPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | unsafeMode);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(
            () => AtomicOutputFile.Create(Path.Combine(childPath, "output.bin"), force: false));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("output-directory-unsafe", failure.Kind);
        Assert.Contains("private", failure.Message);
        Assert.Empty(Directory.EnumerateFiles(childPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_trusted_sticky_tmp_supports_owned_managed_outputs(bool force)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        string destinationPath = Path.Combine("/tmp", $"jrunner-managed-output-{Guid.NewGuid():N}.bin");
        try
        {
            if (force)
            {
                await File.WriteAllTextAsync(destinationPath, "original");
            }
            await using var output = AtomicOutputFile.Create(destinationPath, force);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output.TemporaryPath));
            await WritePayloadAsync(output, "completed");
            await output.CompleteAsync();
            Assert.Equal("completed", await File.ReadAllTextAsync(destinationPath));
            Assert.False(File.Exists(output.TemporaryPath));
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Direct_sticky_tmp_never_publishes_a_managed_temporary_substitution(bool force, bool useSymlink)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string protectedPath = temporaryDirectory.File("protected.bin");
        await File.WriteAllTextAsync(protectedPath, "protected");
        string destinationPath = Path.Combine("/tmp", $"jrunner-managed-output-{Guid.NewGuid():N}.bin");
        try
        {
            if (force)
            {
                await File.WriteAllTextAsync(destinationPath, "original");
            }
            await using var output = AtomicOutputFile.Create(destinationPath, force);
            await WritePayloadAsync(output, "completed");
            output.Stream.Dispose();
            File.Delete(output.TemporaryPath);
            if (useSymlink)
            {
                File.CreateSymbolicLink(output.TemporaryPath, protectedPath);
            }
            else
            {
                await File.WriteAllTextAsync(output.TemporaryPath, "substitute");
            }

            OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
                () => output.CompleteAsync().AsTask());

            Assert.Equal(ExitCode.InputOutput, failure.Code);
            Assert.Equal("output-path-changed", failure.Kind);
            Assert.False(File.Exists(output.TemporaryPath));
            Assert.Null(new FileInfo(output.TemporaryPath).LinkTarget);
            Assert.Equal("protected", await File.ReadAllTextAsync(protectedPath));
            if (force)
            {
                Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
            }
            else
            {
                Assert.False(File.Exists(destinationPath));
            }
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public async Task An_existing_owned_private_child_of_sticky_tmp_remains_supported()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string privatePath = Path.Combine("/tmp", $"jrunner-private-output-{Guid.NewGuid():N}");
        CreatePrivateDirectory(privatePath);
        try
        {
            string destinationPath = Path.Combine(privatePath, "output.bin");
            await using var output = AtomicOutputFile.Create(destinationPath, force: false);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output.TemporaryPath));
            await WritePayloadAsync(output, "completed");
            await output.CompleteAsync();
            Assert.Equal("completed", await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            Directory.Delete(privatePath, recursive: true);
        }
    }

    [Fact]
    public async Task Making_the_directory_unsafe_after_create_rejects_completion_without_replacing_the_destination()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await using var output = AtomicOutputFile.Create(destinationPath, force: true);
        await WritePayloadAsync(output, "completed");
        File.SetUnixFileMode(temporaryDirectory.Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.CompleteAsync().AsTask());

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("output-directory-unsafe", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(output.TemporaryPath));
    }

    [Fact]
    public async Task Disposal_cannot_fail_after_success_even_if_the_directory_becomes_unsafe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await using var output = AtomicOutputFile.Create(destinationPath, force: false);
        await WritePayloadAsync(output, "completed");
        await output.CompleteAsync();
        File.SetUnixFileMode(temporaryDirectory.Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);

        output.Dispose();
        await output.DisposeAsync();

        Assert.Equal("completed", await File.ReadAllTextAsync(destinationPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_directory_or_ancestor_owned_by_another_uid_is_unsafe(bool ancestor)
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string childPath = temporaryDirectory.File("child");
        CreatePrivateDirectory(childPath);
        Assert.Equal(0, ChangeOwner(ancestor ? temporaryDirectory.Path : childPath, userId: 65534, groupId: uint.MaxValue));

        OperationFailureException failure = Assert.Throws<OperationFailureException>(
            () => AtomicOutputFile.Create(Path.Combine(childPath, "output.bin"), force: false));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("output-directory-unsafe", failure.Kind);
        Assert.Empty(Directory.EnumerateFiles(childPath));
    }

    [Fact]
    public async Task A_static_symlink_destination_remains_a_workspace_path_failure()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        string protectedPath = temporaryDirectory.File("protected.bin");
        await File.WriteAllTextAsync(protectedPath, "protected");
        File.CreateSymbolicLink(destinationPath, protectedPath);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(
            () => AtomicOutputFile.Create(destinationPath, force: true));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("workspace-path-unsafe", failure.Kind);
        Assert.Equal("protected", await File.ReadAllTextAsync(protectedPath));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Nonwriting_destination_failures_precede_unsafe_directory_permissions(bool directoryDestination, bool force)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        if (directoryDestination)
        {
            CreatePrivateDirectory(destinationPath);
        }
        else
        {
            await File.WriteAllTextAsync(destinationPath, "original");
        }
        File.SetUnixFileMode(temporaryDirectory.Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(
            () => AtomicOutputFile.Create(destinationPath, force));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal(directoryDestination ? "invalid-destination" : force ? "output-directory-unsafe" : "destination-exists", failure.Kind);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
        if (!directoryDestination)
        {
            Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_destination_without_force_precedes_foreign_directory_ownership(bool force)
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        Assert.Equal(0, ChangeOwner(temporaryDirectory.Path, userId: 65534, groupId: uint.MaxValue));

        OperationFailureException failure = Assert.Throws<OperationFailureException>(
            () => AtomicOutputFile.Create(destinationPath, force));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal(force ? "output-directory-unsafe" : "destination-exists", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    private static async Task WritePayloadAsync(AtomicOutputFile output, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await output.Stream.WriteAsync(bytes.AsMemory());
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner(string path, uint userId, uint groupId);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-cli-tests-{Guid.NewGuid():N}");
            CreatePrivateDirectory(Path);
        }

        public string Path { get; }

        internal string File(string name)
        {
            return System.IO.Path.Combine(Path, name);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

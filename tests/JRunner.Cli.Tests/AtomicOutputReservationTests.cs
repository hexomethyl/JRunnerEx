using System.Runtime.InteropServices;
using System.Text;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class AtomicOutputReservationTests
{
    [Fact]
    public async Task Existing_destination_requires_force_and_the_external_writer_only_publishes_on_completion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");

        OperationFailureException rejection = Assert.Throws<OperationFailureException>(
            () => AtomicOutputReservation.Create(destinationPath, force: false));
        Assert.Equal(ExitCode.Usage, rejection.Code);
        Assert.Equal("destination-exists", rejection.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));

        string uncommittedTemporaryPath;
        await using (var output = AtomicOutputReservation.Create(destinationPath, force: true))
        {
            uncommittedTemporaryPath = output.TemporaryPath;
            await File.WriteAllTextAsync(output.TemporaryPath, "replacement");
        }

        Assert.False(File.Exists(uncommittedTemporaryPath));
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));

        await using (var output = AtomicOutputReservation.Create(destinationPath, force: true))
        {
            await File.WriteAllTextAsync(output.TemporaryPath, "replacement");
            await output.CompleteAsync();
        }

        Assert.Equal("replacement", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public void Missing_destination_directory_is_a_usage_failure()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = Path.Combine(temporaryDirectory.Path, "missing", "output.bin");

        OperationFailureException failure = Assert.Throws<OperationFailureException>(
            () => AtomicOutputReservation.Create(destinationPath, force: false));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("destination-directory-missing", failure.Kind);
    }

    [Fact]
    public async Task Cancellation_removes_external_output_without_replacing_the_destination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        using var cancellationSource = new CancellationTokenSource();

        string temporaryPath;
        await using (var output = AtomicOutputReservation.Create(destinationPath, force: true))
        {
            temporaryPath = output.TemporaryPath;
            await File.WriteAllTextAsync(output.TemporaryPath, "replacement");
            cancellationSource.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => output.CompleteAsync(cancellationSource.Token).AsTask());
        }

        Assert.False(File.Exists(temporaryPath));
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task Empty_or_missing_external_output_is_never_published()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string emptyDestinationPath = temporaryDirectory.File("empty.bin");
        string missingDestinationPath = temporaryDirectory.File("missing.bin");

        string emptyTemporaryPath;
        await using (var output = AtomicOutputReservation.Create(emptyDestinationPath, force: false))
        {
            emptyTemporaryPath = output.TemporaryPath;
            OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
                () => output.CompleteAsync().AsTask());
            Assert.Equal(ExitCode.ExternalProcess, exception.Code);
            Assert.Equal("external-output-empty", exception.Kind);
        }

        Assert.False(File.Exists(emptyTemporaryPath));
        Assert.False(File.Exists(emptyDestinationPath));

        string missingTemporaryPath;
        await using (var output = AtomicOutputReservation.Create(missingDestinationPath, force: false))
        {
            missingTemporaryPath = output.TemporaryPath;
            File.Delete(output.TemporaryPath);

            OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
                () => output.CompleteAsync().AsTask());
            Assert.Equal(ExitCode.ExternalProcess, exception.Code);
            Assert.Equal("external-output-missing", exception.Kind);
        }

        Assert.False(File.Exists(missingTemporaryPath));
        Assert.False(File.Exists(missingDestinationPath));
    }

    [Fact]
    public async Task Symlinked_destination_ancestor_is_rejected_before_force_can_replace_its_target()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string protectedDirectory = temporaryDirectory.File("protected");
        Directory.CreateDirectory(protectedDirectory);
        string protectedFile = Path.Combine(protectedDirectory, "xeBuild.exe");
        await File.WriteAllTextAsync(protectedFile, "immutable");
        string linkedDirectory = temporaryDirectory.File("linked");
        Directory.CreateSymbolicLink(linkedDirectory, protectedDirectory);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(
            () => AtomicOutputReservation.Create(Path.Combine(linkedDirectory, "xeBuild.exe"), force: true));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("workspace-path-unsafe", failure.Kind);
        Assert.Equal("immutable", await File.ReadAllTextAsync(protectedFile));
        Assert.Empty(Directory.EnumerateFiles(protectedDirectory, ".*.tmp"));
    }

    [Fact]
    public async Task Validation_receives_the_hashed_stream_at_zero_and_holds_it_until_completion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: false);
        string stagingPath = Path.GetDirectoryName(output.TemporaryPath)!;
        Assert.Equal(temporaryDirectory.Path, Path.GetDirectoryName(stagingPath));
        Assert.NotEqual(temporaryDirectory.Path, stagingPath);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(stagingPath));
        }
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        Stream? validatedStream = null;

        long validatedLength = await output.ValidateAsync(async (stream, token) =>
        {
            validatedStream = stream;
            Assert.Equal(0, stream.Position);
            Assert.False(stream.CanWrite);
            await ReadExpectedAsync(stream, "expected", token);
        });

        Assert.Equal(8, validatedLength);
        Assert.NotNull(validatedStream);
        Assert.True(validatedStream.CanRead);
        await output.CompleteAsync();
        Assert.False(validatedStream.CanRead);
        Assert.Equal("expected", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(output.TemporaryPath));
        Assert.False(Directory.Exists(stagingPath));
        Assert.Empty(Directory.EnumerateDirectories(temporaryDirectory.Path, ".jrunner-output-*"));
    }

    [Theory]
    [InlineData("rewrite", false)]
    [InlineData("rewrite", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    [InlineData("symlink", false)]
    [InlineData("symlink", true)]
    public async Task Mutation_during_validation_never_publishes_unvalidated_bytes(string mutation, bool force)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        string protectedPath = temporaryDirectory.File("protected.bin");
        if (force)
        {
            await File.WriteAllTextAsync(destinationPath, "original");
        }
        await File.WriteAllTextAsync(protectedPath, "modified");
        await using var output = AtomicOutputReservation.Create(destinationPath, force);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        Stream? validatedStream = null;

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.ValidateAsync(async (stream, token) =>
            {
                validatedStream = stream;
                await ReadExpectedAsync(stream, "expected", token);
                await MutateAsync(output.TemporaryPath, mutation, protectedPath);
            }));

        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal("external-output-changed", failure.Kind);
        Assert.NotNull(validatedStream);
        Assert.False(validatedStream.CanRead);
        Assert.False(File.Exists(output.TemporaryPath));
        Assert.Null(new FileInfo(output.TemporaryPath).LinkTarget);
        Assert.Equal("modified", await File.ReadAllTextAsync(protectedPath));
        if (force)
        {
            Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        }
        else
        {
            Assert.False(File.Exists(destinationPath));
        }
    }

    [Theory]
    [InlineData("rewrite")]
    [InlineData("replace")]
    [InlineData("symlink")]
    public async Task Mutation_after_validation_never_replaces_the_force_destination(string mutation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        string protectedPath = temporaryDirectory.File("protected.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await File.WriteAllTextAsync(protectedPath, "modified");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        await output.ValidateAsync((stream, token) => ReadExpectedAsync(stream, "expected", token));
        await MutateAsync(output.TemporaryPath, mutation, protectedPath);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.CompleteAsync().AsTask());

        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal("external-output-changed", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal("modified", await File.ReadAllTextAsync(protectedPath));
        Assert.False(File.Exists(output.TemporaryPath));
        Assert.Null(new FileInfo(output.TemporaryPath).LinkTarget);
    }

    [Fact]
    public async Task Validation_failure_preserves_its_failure_and_releases_the_stream_and_temporary_file()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        var expectedFailure = new OperationFailureException(ExitCode.InvalidData, "invalid-image", "Invalid image.");
        Stream? validatedStream = null;

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.ValidateAsync((stream, _) =>
            {
                validatedStream = stream;
                throw expectedFailure;
            }));

        Assert.Same(expectedFailure, failure);
        Assert.NotNull(validatedStream);
        Assert.False(validatedStream.CanRead);
        Assert.False(File.Exists(output.TemporaryPath));
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        output.EnsurePathSafe();
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.CompleteAsync().AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validation_cancellation_removes_the_output_and_preserves_the_destination(bool duringCallback)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        using var cancellation = new CancellationTokenSource();
        if (!duringCallback)
        {
            cancellation.Cancel();
        }
        bool callbackInvoked = false;
        Stream? validatedStream = null;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => output.ValidateAsync(async (stream, token) =>
            {
                callbackInvoked = true;
                validatedStream = stream;
                await ReadExpectedAsync(stream, "expected", token);
                cancellation.Cancel();
            }, cancellation.Token));

        Assert.Equal(duringCallback, callbackInvoked);
        if (validatedStream is not null)
        {
            Assert.False(validatedStream.CanRead);
        }
        Assert.False(File.Exists(output.TemporaryPath));
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task Completion_cancellation_after_validation_releases_the_held_output_without_replacing_the_destination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        Stream? validatedStream = null;
        await output.ValidateAsync((stream, token) =>
        {
            validatedStream = stream;
            return ReadExpectedAsync(stream, "expected", token);
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => output.CompleteAsync(cancellation.Token).AsTask());

        Assert.NotNull(validatedStream);
        Assert.False(validatedStream.CanRead);
        Assert.False(File.Exists(output.TemporaryPath));
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task Prevalidation_repair_opens_the_regular_output_through_the_bound_directory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: false);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        using (FileStream stream = output.OpenOutput(FileAccess.ReadWrite))
        {
            Assert.Equal(8, stream.Length);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("repaired"));
        }

        Assert.Equal(8, await output.ValidateAsync((stream, token) => ReadExpectedAsync(stream, "repaired", token)));
        await output.CompleteAsync();
        Assert.Equal("repaired", await File.ReadAllTextAsync(destinationPath));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("validate")]
    [InlineData("complete")]
    public async Task An_initial_symlinked_external_output_is_invalid_without_touching_its_target(string operation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        string protectedPath = temporaryDirectory.File("protected.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await File.WriteAllTextAsync(protectedPath, "modified");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        File.Delete(output.TemporaryPath);
        File.CreateSymbolicLink(output.TemporaryPath, protectedPath);
        OperationFailureException failure;
        if (operation == "open")
        {
            failure = Assert.Throws<OperationFailureException>(() =>
            {
                using FileStream stream = output.OpenOutput(FileAccess.ReadWrite);
            });
        }
        else if (operation == "validate")
        {
            failure = await Assert.ThrowsAsync<OperationFailureException>(
                () => output.ValidateAsync((_, _) => Task.CompletedTask));
        }
        else
        {
            failure = await Assert.ThrowsAsync<OperationFailureException>(() => output.CompleteAsync().AsTask());
        }

        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal("external-output-invalid", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal("modified", await File.ReadAllTextAsync(protectedPath));
        Assert.False(File.Exists(output.TemporaryPath));
        Assert.Null(new FileInfo(output.TemporaryPath).LinkTarget);
    }

    [Theory]
    [InlineData(FileAccess.Read)]
    [InlineData(FileAccess.ReadWrite)]
    public void Fifo_output_is_rejected_before_opening_a_managed_stream(FileAccess access)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        using var output = AtomicOutputReservation.Create(destinationPath, force: false);
        File.Delete(output.TemporaryPath);
        Assert.Equal(0, MakeFifo(output.TemporaryPath, 0x180));

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
        {
            using FileStream stream = output.OpenOutput(access);
        });

        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal("external-output-invalid", failure.Kind);
        Assert.False(File.Exists(destinationPath));
        Assert.False(File.Exists(output.TemporaryPath));
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.OtherWrite)]
    public async Task External_output_with_other_writers_is_invalid(UnixFileMode unsafeMode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output.TemporaryPath));
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        File.SetUnixFileMode(output.TemporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | unsafeMode);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.ValidateAsync((_, _) => Task.CompletedTask));

        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal("external-output-invalid", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(output.TemporaryPath));
    }

    [Fact]
    public async Task A_hardlinked_external_output_is_invalid_and_cleanup_does_not_remove_its_alias()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        string aliasPath = temporaryDirectory.File("alias.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        Assert.Equal(0, Link(output.TemporaryPath, aliasPath));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.ValidateAsync((_, _) => Task.CompletedTask));

        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal("external-output-invalid", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal("expected", await File.ReadAllTextAsync(aliasPath));
        Assert.False(File.Exists(output.TemporaryPath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Directory_or_ancestor_replacement_never_redirects_publication_or_cleanup(bool replaceAncestor, bool useSymlink)
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
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        string stagingName = Path.GetFileName(Path.GetDirectoryName(output.TemporaryPath)!);

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
        string impostorStaging = Path.Combine(replacementOutputDirectory, stagingName);
        CreatePrivateDirectory(impostorStaging);
        string impostorTemporary = Path.Combine(impostorStaging, Path.GetFileName(output.TemporaryPath));
        await File.WriteAllTextAsync(impostorDestination, "unrelated destination");
        await File.WriteAllTextAsync(impostorTemporary, "unrelated temporary");

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.CompleteAsync().AsTask());

        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.Equal("output-path-changed", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(heldOutputDirectory, "output.bin")));
        Assert.False(File.Exists(Path.Combine(heldOutputDirectory, stagingName, Path.GetFileName(output.TemporaryPath))));
        output.Dispose();
        Assert.False(Directory.Exists(Path.Combine(heldOutputDirectory, stagingName)));
        Assert.Equal("unrelated destination", await File.ReadAllTextAsync(impostorDestination));
        Assert.Equal("unrelated temporary", await File.ReadAllTextAsync(impostorTemporary));
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.OtherWrite)]
    public void Writable_destination_directories_are_rejected_without_creating_a_reservation(UnixFileMode unsafeMode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        File.SetUnixFileMode(temporaryDirectory.Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | unsafeMode);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(
            () => AtomicOutputReservation.Create(temporaryDirectory.File("output.bin"), force: false));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("output-directory-unsafe", failure.Kind);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path));
    }

    [Fact]
    public async Task Trust_is_rechecked_after_validation_without_losing_the_original_destination()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        await output.ValidateAsync((stream, token) => ReadExpectedAsync(stream, "expected", token));
        File.SetUnixFileMode(temporaryDirectory.Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.CompleteAsync().AsTask());

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("output-directory-unsafe", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(output.TemporaryPath));
    }

    [Fact]
    public async Task A_destination_created_after_validation_is_preserved_without_force()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: false);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        await output.ValidateAsync((stream, token) => ReadExpectedAsync(stream, "expected", token));
        await File.WriteAllTextAsync(destinationPath, "competitor");

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.CompleteAsync().AsTask());

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("destination-exists", failure.Kind);
        Assert.Equal("competitor", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(output.TemporaryPath));
    }

    [Fact]
    public async Task A_semantic_failure_cannot_hide_mutation_of_the_validated_file()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.ValidateAsync(async (stream, token) =>
            {
                await ReadExpectedAsync(stream, "expected", token);
                await File.WriteAllTextAsync(output.TemporaryPath, "modified", token);
                throw new OperationFailureException(ExitCode.InvalidData, "invalid-image", "Invalid image.");
            }));

        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal("external-output-changed", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(output.TemporaryPath));
        output.EnsurePathSafe();
    }

    [Fact]
    public async Task Closing_the_validation_stream_cannot_release_its_identity_and_publish_a_replacement()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.ValidateAsync((stream, _) =>
            {
                stream.Dispose();
                return Task.CompletedTask;
            }));

        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal("external-output-changed", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(output.TemporaryPath));
    }

    [Fact]
    public async Task A_foreign_owned_external_output_is_invalid()
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: true);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        Assert.Equal(0, ChangeOwner(output.TemporaryPath, userId: 65534, groupId: uint.MaxValue));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => output.ValidateAsync((_, _) => Task.CompletedTask));

        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal("external-output-invalid", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(output.TemporaryPath));
    }

    [Fact]
    public async Task Disposal_after_publication_never_rechecks_an_unsafe_directory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: false);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        await output.ValidateAsync((stream, token) => ReadExpectedAsync(stream, "expected", token));
        await output.CompleteAsync();
        File.SetUnixFileMode(temporaryDirectory.Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);

        output.Dispose();
        await output.DisposeAsync();

        Assert.Equal("expected", await File.ReadAllTextAsync(destinationPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_sticky_tmp_uses_a_private_stage_and_preserves_force_semantics(bool force)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        string destinationPath = Path.Combine("/tmp", $"jrunner-reserved-output-{Guid.NewGuid():N}.bin");
        try
        {
            if (force)
            {
                await File.WriteAllTextAsync(destinationPath, "original");
            }
            await using var output = AtomicOutputReservation.Create(destinationPath, force);
            string stagingPath = Path.GetDirectoryName(output.TemporaryPath)!;
            Assert.Equal("/tmp", Path.GetDirectoryName(stagingPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(stagingPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output.TemporaryPath));
            await File.WriteAllTextAsync(output.TemporaryPath, "expected");
            await output.ValidateAsync((stream, token) => ReadExpectedAsync(stream, "expected", token));
            await output.CompleteAsync();

            Assert.Equal("expected", await File.ReadAllTextAsync(destinationPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destinationPath));
            Assert.False(File.Exists(output.TemporaryPath));
            Assert.False(Directory.Exists(stagingPath));
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Theory]
    [InlineData("rewrite", false)]
    [InlineData("rewrite", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    [InlineData("symlink", false)]
    [InlineData("symlink", true)]
    public async Task Direct_sticky_tmp_never_publishes_a_validated_temporary_substitution(string mutation, bool force)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string protectedPath = temporaryDirectory.File("protected.bin");
        await File.WriteAllTextAsync(protectedPath, "modified");
        string destinationPath = Path.Combine("/tmp", $"jrunner-reserved-output-{Guid.NewGuid():N}.bin");
        try
        {
            if (force)
            {
                await File.WriteAllTextAsync(destinationPath, "original");
            }
            string stagingPath;
            await using (var output = AtomicOutputReservation.Create(destinationPath, force))
            {
                stagingPath = Path.GetDirectoryName(output.TemporaryPath)!;
                await File.WriteAllTextAsync(output.TemporaryPath, "expected");
                await output.ValidateAsync((stream, token) => ReadExpectedAsync(stream, "expected", token));
                await MutateAsync(output.TemporaryPath, mutation, protectedPath);

                OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
                    () => output.CompleteAsync().AsTask());

                Assert.Equal(ExitCode.ExternalProcess, failure.Code);
                Assert.Equal("external-output-changed", failure.Kind);
                Assert.False(File.Exists(output.TemporaryPath));
                Assert.Null(new FileInfo(output.TemporaryPath).LinkTarget);
                Assert.Equal("modified", await File.ReadAllTextAsync(protectedPath));
                if (force)
                {
                    Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
                }
                else
                {
                    Assert.False(File.Exists(destinationPath));
                }
            }
            Assert.False(Directory.Exists(stagingPath));
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
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(
            () => AtomicOutputReservation.Create(destinationPath, force));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal(directoryDestination ? "invalid-destination" : force ? "output-directory-unsafe" : "destination-exists", failure.Kind);
        Assert.Empty(Directory.EnumerateDirectories(temporaryDirectory.Path, ".jrunner-output-*"));
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
            () => AtomicOutputReservation.Create(destinationPath, force));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal(force ? "output-directory-unsafe" : "destination-exists", failure.Kind);
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.Empty(Directory.EnumerateDirectories(temporaryDirectory.Path, ".jrunner-output-*"));
    }

    [Fact]
    public async Task Stage_contents_are_removed_without_following_links_before_the_final_publication()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        string protectedDirectory = temporaryDirectory.File("protected");
        CreatePrivateDirectory(protectedDirectory);
        string protectedPath = Path.Combine(protectedDirectory, "protected.bin");
        await File.WriteAllTextAsync(protectedPath, "protected");
        await using var output = AtomicOutputReservation.Create(destinationPath, force: false);
        string stagingPath = Path.GetDirectoryName(output.TemporaryPath)!;
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        // External recreation can use 0644 inside the private stage; publication must use 0600.
        File.SetUnixFileMode(output.TemporaryPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.CreateSymbolicLink(output.TemporaryPath + ".log", protectedPath);
        Directory.CreateSymbolicLink(Path.Combine(stagingPath, "linked-directory"), protectedDirectory);
        string nestedPath = Path.Combine(stagingPath, "nested");
        CreatePrivateDirectory(nestedPath);
        await File.WriteAllTextAsync(Path.Combine(nestedPath, "sidecar.txt"), "sidecar");
        await output.ValidateAsync((stream, token) => ReadExpectedAsync(stream, "expected", token));

        await output.CompleteAsync();

        Assert.Equal("expected", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destinationPath));
        Assert.Equal("protected", await File.ReadAllTextAsync(protectedPath));
        Assert.False(Directory.Exists(stagingPath));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
        Assert.Empty(Directory.EnumerateDirectories(temporaryDirectory.Path, ".jrunner-output-*"));
    }

    [Theory]
    [InlineData("dispose")]
    [InlineData("validation")]
    [InlineData("cancel")]
    public async Task Failure_or_abandonment_removes_stage_sidecars_without_following_their_targets(string failureMode)
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
        string stagingPath;
        await using (var output = AtomicOutputReservation.Create(destinationPath, force: true))
        {
            stagingPath = Path.GetDirectoryName(output.TemporaryPath)!;
            await File.WriteAllTextAsync(output.TemporaryPath, "expected");
            await File.WriteAllTextAsync(output.TemporaryPath + ".log", "diagnostic");
            File.CreateSymbolicLink(output.TemporaryPath + ".ini", protectedPath);
            string nestedPath = Path.Combine(stagingPath, "nested");
            CreatePrivateDirectory(nestedPath);
            await File.WriteAllTextAsync(Path.Combine(nestedPath, "sidecar.txt"), "sidecar");
            if (failureMode == "validation")
            {
                await Assert.ThrowsAsync<OperationFailureException>(() => output.ValidateAsync((_, _) =>
                    throw new OperationFailureException(ExitCode.InvalidData, "invalid-image", "Invalid image.")));
                output.EnsurePathSafe();
                Assert.True(File.Exists(output.TemporaryPath + ".log"));
            }
            else if (failureMode == "cancel")
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => output.CompleteAsync(cancellation.Token).AsTask());
                output.EnsurePathSafe();
            }
        }

        Assert.False(Directory.Exists(stagingPath));
        Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal("protected", await File.ReadAllTextAsync(protectedPath));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
        Assert.Empty(Directory.EnumerateDirectories(temporaryDirectory.Path, ".jrunner-output-*"));
    }

    [Fact]
    public async Task Sidecar_file_and_directory_cleanup_are_bound_and_do_not_follow_links()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string protectedPath = temporaryDirectory.File("protected.bin");
        await File.WriteAllTextAsync(protectedPath, "protected");
        await using var output = AtomicOutputReservation.Create(temporaryDirectory.File("output.bin"), force: false);
        await File.WriteAllTextAsync(output.TemporaryPath, "expected");
        File.CreateSymbolicLink(output.TemporaryPath + ".log", protectedPath);
        string sidecarDirectory = output.TemporaryPath + ".logs";
        CreatePrivateDirectory(sidecarDirectory);
        await File.WriteAllTextAsync(Path.Combine(sidecarDirectory, "diagnostic.txt"), "diagnostic");
        File.CreateSymbolicLink(Path.Combine(sidecarDirectory, "linked-file"), protectedPath);

        output.DeleteSidecarFile(Path.GetFileName(output.TemporaryPath + ".log"));
        output.DeleteSidecarFile(Path.GetFileName(sidecarDirectory));
        Assert.Throws<ArgumentException>(() => output.DeleteSidecarFile(Path.GetFileName(output.TemporaryPath)));

        Assert.Null(new FileInfo(output.TemporaryPath + ".log").LinkTarget);
        Assert.False(File.Exists(output.TemporaryPath + ".log"));
        Assert.False(Directory.Exists(sidecarDirectory));
        Assert.Equal("protected", await File.ReadAllTextAsync(protectedPath));
        Assert.Equal("expected", await File.ReadAllTextAsync(output.TemporaryPath));
    }

    [Theory]
    [InlineData((UnixFileMode)0)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite | UnixFileMode.StickyBit)]
    public async Task Staging_directory_must_remain_private_and_failed_cleanup_restores_owner_access(UnixFileMode unsafeMode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        string stagingPath;
        await using (var output = AtomicOutputReservation.Create(destinationPath, force: true))
        {
            stagingPath = Path.GetDirectoryName(output.TemporaryPath)!;
            await File.WriteAllTextAsync(output.TemporaryPath, "expected");
            await output.ValidateAsync((stream, token) => ReadExpectedAsync(stream, "expected", token));
            File.SetUnixFileMode(stagingPath, unsafeMode);

            OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
                () => output.CompleteAsync().AsTask());

            Assert.Equal(ExitCode.Usage, failure.Code);
            Assert.Equal("output-directory-unsafe", failure.Kind);
            Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
        }
        Assert.False(Directory.Exists(stagingPath));
    }

    [Fact]
    public async Task Stage_cleanup_failure_is_reported_before_the_force_destination_can_be_replaced()
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() == 0)
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string destinationPath = temporaryDirectory.File("output.bin");
        await File.WriteAllTextAsync(destinationPath, "original");
        string stagingPath;
        await using (var output = AtomicOutputReservation.Create(destinationPath, force: true))
        {
            stagingPath = Path.GetDirectoryName(output.TemporaryPath)!;
            await File.WriteAllTextAsync(output.TemporaryPath, "expected");
            string blockedPath = Path.Combine(stagingPath, "blocked");
            CreatePrivateDirectory(blockedPath);
            await File.WriteAllTextAsync(Path.Combine(blockedPath, "sidecar.txt"), "sidecar");
            await output.ValidateAsync((stream, token) => ReadExpectedAsync(stream, "expected", token));
            File.SetUnixFileMode(blockedPath, (UnixFileMode)0);
            try
            {
                await Assert.ThrowsAsync<IOException>(() => output.CompleteAsync().AsTask());
                Assert.Equal("original", await File.ReadAllTextAsync(destinationPath));
                Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
            }
            finally
            {
                File.SetUnixFileMode(blockedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        Assert.False(Directory.Exists(stagingPath));
    }

    private static async Task ReadExpectedAsync(Stream stream, string expected, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(expected)];
        await stream.ReadExactlyAsync(bytes.AsMemory(), cancellationToken);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.Equal(-1, stream.ReadByte());
    }

    private static async Task MutateAsync(string path, string mutation, string protectedPath)
    {
        DateTime modificationTime = File.GetLastWriteTimeUtc(path);
        if (mutation == "rewrite")
        {
            await File.WriteAllTextAsync(path, "modified");
            File.SetLastWriteTimeUtc(path, modificationTime);
        }
        else if (mutation == "replace")
        {
            File.Delete(path);
            // Identical bytes and restored mtime still cannot substitute for the held inode.
            await File.WriteAllTextAsync(path, "expected");
            File.SetLastWriteTimeUtc(path, modificationTime);
        }
        else
        {
            File.Delete(path);
            File.CreateSymbolicLink(path, protectedPath);
        }
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

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string source, string destination);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, uint mode);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner(string path, uint userId, uint groupId);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-output-reservation-tests-{Guid.NewGuid():N}");
            CreatePrivateDirectory(Path);
        }

        internal string Path { get; }

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

using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Xunit;

namespace JRunner.Cli.Tests.Support;

public sealed class SupportRootResolverTests
{
    [Fact]
    public void Explicit_support_root_overrides_environment_without_creating_it()
    {
        string root = Path.Combine(Path.GetTempPath(), $"jrunner-support-root-{Guid.NewGuid():N}");

        SupportRoot resolved = SupportRootResolver.Resolve(
            root,
            static variable => variable switch
            {
                SupportRootResolver.SupportRootEnvironmentVariable => "/ignored/support",
                SupportRootResolver.XdgDataHomeEnvironmentVariable => "/ignored/xdg",
                SupportRootResolver.HomeEnvironmentVariable => "/ignored/home",
                _ => null,
            });

        Assert.Equal(SupportRootSource.Explicit, resolved.Source);
        Assert.Equal(Path.GetFullPath(root), resolved.DirectoryPath);
        Assert.False(Directory.Exists(resolved.DirectoryPath));
    }

    [Fact]
    public void Environment_override_precedes_xdg_data_home_without_creating_the_root()
    {
        string overrideRoot = Path.Combine(Path.GetTempPath(), $"jrunner-support-override-{Guid.NewGuid():N}");

        SupportRoot resolved = SupportRootResolver.Resolve(
            explicitSupportRoot: null,
            variable => variable switch
            {
                SupportRootResolver.SupportRootEnvironmentVariable => overrideRoot,
                SupportRootResolver.XdgDataHomeEnvironmentVariable => "/ignored/xdg",
                SupportRootResolver.HomeEnvironmentVariable => "/ignored/home",
                _ => null,
            });

        Assert.Equal(SupportRootSource.EnvironmentOverride, resolved.Source);
        Assert.Equal(Path.GetFullPath(overrideRoot), resolved.DirectoryPath);
        Assert.False(Directory.Exists(resolved.DirectoryPath));
    }

    [Fact]
    public void Absolute_xdg_data_home_selects_the_application_support_root()
    {
        string xdgDataHome = Path.Combine(Path.GetTempPath(), $"jrunner-xdg-{Guid.NewGuid():N}");

        SupportRoot resolved = SupportRootResolver.Resolve(
            explicitSupportRoot: null,
            variable => variable switch
            {
                SupportRootResolver.XdgDataHomeEnvironmentVariable => xdgDataHome,
                SupportRootResolver.HomeEnvironmentVariable => "/ignored/home",
                _ => null,
            });

        Assert.Equal(SupportRootSource.XdgDataHome, resolved.Source);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(xdgDataHome), "jrunner", "support"),
            resolved.DirectoryPath);
    }

    [Fact]
    public void Relative_xdg_data_home_is_ignored_in_favor_of_the_home_fallback()
    {
        string home = Path.Combine(Path.GetTempPath(), $"jrunner-home-{Guid.NewGuid():N}");

        SupportRoot resolved = SupportRootResolver.Resolve(
            explicitSupportRoot: null,
            variable => variable switch
            {
                SupportRootResolver.XdgDataHomeEnvironmentVariable => "relative-data-root",
                SupportRootResolver.HomeEnvironmentVariable => home,
                _ => null,
            });

        Assert.Equal(SupportRootSource.HomeFallback, resolved.Source);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(home), ".local", "share", "jrunner", "support"),
            resolved.DirectoryPath);
    }

    [Fact]
    public void Missing_absolute_xdg_and_home_paths_return_a_typed_failure()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => SupportRootResolver.Resolve(
            explicitSupportRoot: null,
            static _ => null));

        Assert.Equal(ExitCode.MissingPrerequisite, exception.Code);
        Assert.Equal("support-root-unavailable", exception.Kind);
    }

    [Fact]
    public void Empty_environment_override_returns_a_usage_failure()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => SupportRootResolver.Resolve(
            explicitSupportRoot: null,
            variable => variable switch
            {
                SupportRootResolver.SupportRootEnvironmentVariable => string.Empty,
                SupportRootResolver.XdgDataHomeEnvironmentVariable => "/ignored/xdg",
                SupportRootResolver.HomeEnvironmentVariable => "/ignored/home",
                _ => null,
            }));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("invalid-support-root", exception.Kind);
    }

    [Fact]
    public void Empty_explicit_support_root_returns_a_usage_failure()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => SupportRootResolver.Resolve(
            string.Empty,
            static _ => null));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("invalid-support-root", exception.Kind);
    }
}

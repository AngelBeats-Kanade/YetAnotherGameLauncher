using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class UpdatePlannerTests
{
    [Fact]
    public void Plan_NoLocalVersion_FullSync()
    {
        var plan = UpdatePlanner.Plan(null, "3.6.0", ["3.4.0", "3.5.0"]);

        Assert.Equal(UpdateStrategy.FullSync, plan.Strategy);
        Assert.Equal("", plan.FromVersion);
        Assert.Equal("3.6.0", plan.ToVersion);
    }

    [Fact]
    public void Plan_LocalVersionMatchesPatch_Incremental()
    {
        var plan = UpdatePlanner.Plan("3.5.0", "3.6.0", ["3.4.0", "3.5.0"]);

        Assert.Equal(UpdateStrategy.Incremental, plan.Strategy);
        Assert.Equal("3.5.0", plan.FromVersion);
        Assert.Equal("3.6.0", plan.ToVersion);
    }

    [Fact]
    public void Plan_LocalVersionNotInPatches_FullSync()
    {
        var plan = UpdatePlanner.Plan("2.0.0", "3.6.0", ["3.4.0", "3.5.0"]);

        Assert.Equal(UpdateStrategy.FullSync, plan.Strategy);
        Assert.Equal("2.0.0", plan.FromVersion);
    }

    [Fact]
    public void Plan_MatchingIsExactStringComparison()
    {
        // "3.5" 与 "3.5.0" 是不同的版本串，不视为可差分
        var plan = UpdatePlanner.Plan("3.5", "3.6.0", ["3.5.0"]);

        Assert.Equal(UpdateStrategy.FullSync, plan.Strategy);
    }

    [Fact]
    public void Plan_EmptyPatches_FullSync()
    {
        var plan = UpdatePlanner.Plan("3.5.0", "3.6.0", []);

        Assert.Equal(UpdateStrategy.FullSync, plan.Strategy);
    }
}

public class VersionComparisonTests
{
    [Theory]
    [InlineData("3.5.0", "3.6.0", true)]
    [InlineData("3.6.0", "3.5.0", false)]
    [InlineData("3.6.0", "3.6.0", false)]
    [InlineData("3.9.0", "3.10.0", true)]
    [InlineData("1.0.0.9", "1.0.1", true)]
    [InlineData("1.0.0", "1.0.0.1", true)]
    public void IsNewer_NumericVersions(string local, string remote, bool expected)
    {
        Assert.Equal(expected, VersionComparison.IsNewer(remote, local));
    }

    [Fact]
    public void IsNewer_NonNumericVersions_FallBackToStringComparison()
    {
        Assert.True(VersionComparison.IsNewer("abc", "abd"));
        Assert.False(VersionComparison.IsNewer("abc", "abc"));
    }

    [Fact]
    public void IsNewer_NothingInstalled_ReturnsFalse()
    {
        Assert.False(VersionComparison.IsNewer("3.6.0", null));
        Assert.False(VersionComparison.IsNewer("3.6.0", ""));
    }
}

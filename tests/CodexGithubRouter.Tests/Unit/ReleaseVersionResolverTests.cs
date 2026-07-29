using ReleaseVersionTool;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class ReleaseVersionResolverTests
{
    [Theory]
    [InlineData("major", "", "1.0.0")]
    [InlineData("minor", "", "0.3.0")]
    [InlineData("build", "", "0.2.1")]
    [InlineData("minor", " alpha.2 ", "0.3.0-alpha.2")]
    public void Resolves_requested_increment(string part, string suffix, string expected)
    {
        var release = ReleaseVersionResolver.Resolve(new[] { "v0.2.0" }, part, suffix);
        Assert.Equal(expected, release.Version);
        Assert.Equal("v" + expected, release.Tag);
        Assert.Equal(expected.Contains('-'), release.IsPrerelease);
    }

    [Fact]
    public void Selects_latest_semver_and_removes_existing_prerelease_before_increment()
    {
        var release = ReleaseVersionResolver.Resolve(new[] { "nonsense", "v0.0.2-alpha", "v0.0.1", "v0.0.2-alpha.10", "v0.0.2-alpha.2" }, "minor", "alpha");
        Assert.Equal("0.1.0-alpha", release.Version);
    }

    [Theory]
    [InlineData("-alpha")]
    [InlineData("alpha..2")]
    [InlineData("alpha_2")]
    [InlineData("01")]
    public void Rejects_invalid_suffix(string suffix) =>
        Assert.Throws<ArgumentException>(() => ReleaseVersionResolver.Resolve(new[] { "v0.0.2" }, "build", suffix));

    [Fact]
    public void Requires_baseline_tag() =>
        Assert.Throws<InvalidOperationException>(() => ReleaseVersionResolver.Resolve(new[] { "release-1.2.3", "v1.2" }, "build", null));

    [Fact]
    public void Rejects_duplicate_target_tag() =>
        Assert.Throws<InvalidOperationException>(() => ReleaseVersionResolver.EnsureTargetDoesNotExist(new[] { "v0.0.3" }, "v0.0.3"));
}

using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// A preview server pins the agents it installs to its own release; any other build leaves the
/// installers on GitHub's latest stable release and its commands unchanged.
/// </summary>
public class AgentReleasePinTests
{
    [Theory]
    [InlineData("2.9.1-preview1", "v2.9.1-preview1")]
    [InlineData("2.9.1-preview1+abc1234", "v2.9.1-preview1")]
    [InlineData("2.9.1", null)]
    [InlineData("2.9.1+abc1234", null)]
    [InlineData("2.9.1-preview1.3+abc1234", null)]
    [InlineData("0.0.0-alpha.0.12", null)]
    [InlineData("0.0.0-preview1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PrereleaseTag_OnlyForAnExactPrereleaseTag(string? informational, string? expected)
    {
        AppVersionInfo.PrereleaseTagOf(informational).Should().Be(expected);
    }

    [Theory]
    [InlineData("2.9.0-preview8", true, "2.9.0-preview8")]
    [InlineData("2.9.0-preview8", false, "2.9.0")]
    [InlineData("2.9.0", true, "2.9.0")]
    [InlineData("2.9.0", false, "2.9.0")]
    public void AgentVersion_IsShownInFullOnlyOnAPreviewInstall(string version, bool previewInstall, string expected)
    {
        AppVersionInfo.AgentVersionForDisplay(version, previewInstall).Should().Be(expected);
    }

    [Fact]
    public void GatewayCommands_CarryTheTagOnAPreviewServer()
    {
        GatewayAgentCommands.Install("https://optimizer.example.com", "noa_abc", "v2.9.1-preview1")
            .Should().EndWith("--release \"v2.9.1-preview1\"");
        GatewayAgentCommands.Upgrade("https://optimizer.example.com", "v2.9.1-preview1")
            .Should().EndWith(" --release \"v2.9.1-preview1\"");
    }

    [Theory]
    [InlineData("v2.9.0-preview8", "2.9.0-preview8")]
    [InlineData(null, "latest")]
    public void DockerUpgrade_PointsTheImageAtTheRelease_OrBackAtLatest(string? releaseTag, string imageTag)
    {
        GatewayAgentCommands.DockerUpgrade(releaseTag).Should().Be(
            "cd /opt/network-optimizer-agent"
            + $" && sed -i 's#\\(image: ghcr.io/ozark-connect/agent\\):.*#\\1:{imageTag}#' docker-compose.yml"
            + " && docker compose pull && docker compose up -d");
    }

    [Fact]
    public void GatewayCommands_AreUnchangedWithoutATag()
    {
        GatewayAgentCommands.Install("https://optimizer.example.com", "noa_abc", null).Should().NotContain("--release");
        GatewayAgentCommands.Upgrade("https://optimizer.example.com", null).Should().NotContain("--release");
        GatewayAgentCommands.ReleaseArgument(null).Should().BeEmpty();
    }
}

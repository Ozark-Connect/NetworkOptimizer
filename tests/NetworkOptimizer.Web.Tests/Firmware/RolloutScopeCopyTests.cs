using FluentAssertions;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// A rollout can be console-only, so alert copy that leads with a device count read as "0 devices
/// across 0 waves" on a real rollout. A UXG-class gateway is an ordinary device step and sets no
/// console flag, so its rollouts must keep reading exactly as they did.
/// </summary>
public class RolloutScopeCopyTests
{
    private static RolloutPlanDocument Doc(bool network = false, bool os = false) => new()
    {
        IncludesUniFiNetworkUpdate = network,
        IncludesUniFiOsUpdate = os,
    };

    [Fact]
    public void AConsoleOnlyRollout_NamesTheConsoleInsteadOfCountingNothing()
    {
        RolloutScopeCopy.Scope(Doc(os: true), devices: 0, waves: 0).Should().Be("UniFi OS");
        RolloutScopeCopy.Scope(Doc(network: true), 0, 0).Should().Be("the UniFi Network application");
        RolloutScopeCopy.Scope(Doc(network: true, os: true), 0, 0)
            .Should().Be("the UniFi Network application and UniFi OS");
    }

    [Fact]
    public void ADeviceOnlyRollout_ReadsExactlyAsItDidBefore()
    {
        // A UXG gateway lands here: an ordinary device step, no console flag.
        RolloutScopeCopy.Scope(Doc(), devices: 1, waves: 1).Should().Be("1 device across 1 wave");
        RolloutScopeCopy.Scope(Doc(), devices: 3, waves: 2).Should().Be("3 devices across 2 waves");
        RolloutScopeCopy.Subject(Doc(), devices: 1).Should().Be("1 device");
        RolloutScopeCopy.Subject(Doc(), devices: 3).Should().Be("3 devices");
    }

    [Fact]
    public void ARolloutCoveringBoth_NamesBoth()
    {
        RolloutScopeCopy.Scope(Doc(os: true), devices: 3, waves: 2)
            .Should().Be("3 devices across 2 waves, plus UniFi OS");
        RolloutScopeCopy.Subject(Doc(network: true, os: true), devices: 2)
            .Should().Be("2 devices and the UniFi Network application and UniFi OS");
    }

    [Fact]
    public void ARolloutCoveringNothing_StillCounts_RatherThanSayingNothingAtAll()
    {
        RolloutScopeCopy.Scope(Doc(), devices: 0, waves: 0).Should().Be("0 devices across 0 waves");
        RolloutScopeCopy.ConsoleSurfaces(Doc()).Should().BeNull();
        RolloutScopeCopy.IncludesConsole(Doc()).Should().BeFalse();
    }

    [Fact]
    public void SentenceCapitalizesWithoutTouchingTheRest()
    {
        RolloutScopeCopy.Sentence("the UniFi Network application").Should().Be("The UniFi Network application");
        RolloutScopeCopy.Sentence("UniFi OS").Should().Be("UniFi OS");
        RolloutScopeCopy.Sentence("").Should().BeEmpty();
    }
}

using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The "Run It for Me" shared command builder - the one source for both the displayed
/// gateway one-liners and the SSH run, so what these assert is exactly what executes.
/// </summary>
public class GatewayAgentCommandsTests
{
    [Fact]
    public void Install_IncludesServerAndToken()
    {
        var command = GatewayAgentCommands.Install("https://optimizer.example.com", "noa_abc123");

        command.Should().Contain("scripts/agent/install-agent-gateway.sh");
        command.Should().Contain("--server \"https://optimizer.example.com\"");
        command.Should().Contain("--token \"noa_abc123\"");
    }

    [Fact]
    public void Install_TrimsTrailingSlashFromServerUrl()
    {
        var command = GatewayAgentCommands.Install("https://optimizer.example.com/", "noa_abc123");

        command.Should().Contain("--server \"https://optimizer.example.com\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Install_UnsetServerUrl_UsesPlaceholder(string? serverUrl)
    {
        var command = GatewayAgentCommands.Install(serverUrl, "noa_abc123");

        command.Should().Contain($"--server \"{GatewayAgentCommands.PlaceholderServerUrl}\"");
    }

    [Fact]
    public void Install_IsMonitoringOnlyAndRootRun()
    {
        var command = GatewayAgentCommands.Install("https://optimizer.example.com", "noa_abc123");

        // The gateway installer must never host the LAN speed test, and UniFi gateways SSH in
        // as root, so neither flag nor sudo may creep in.
        command.Should().NotContain("--lan-speed-test");
        command.Should().NotContain("sudo");
        command.Should().NotContain("--insecure");
    }

    [Fact]
    public void Upgrade_HasServerButNoToken()
    {
        var command = GatewayAgentCommands.Upgrade("https://optimizer.example.com");

        command.Should().Contain("scripts/agent/install-agent-gateway.sh");
        command.Should().Contain("--server \"https://optimizer.example.com\"");
        command.Should().NotContain("--token");
    }
}

/// <summary>
/// The availability gate's configuration half (the dial is exercised live) and the per-site
/// run bookkeeping: one run at a time, a second start refused rather than queued.
/// </summary>
public class GatewayAgentInstallGateTests
{
    private static GatewaySshSettings ConfiguredSettings() => new()
    {
        Enabled = true,
        Host = "192.0.2.1",
        Username = "root",
        Password = "encrypted",
    };

    [Fact]
    public void IsCandidate_AllConditionsMet_True()
    {
        GatewayAgentInstallService.IsCandidate(
            serverUrlConfigured: true, routedViaAgent: false, ConfiguredSettings())
            .Should().BeTrue();
    }

    [Fact]
    public void IsCandidate_NoServerUrl_False()
    {
        GatewayAgentInstallService.IsCandidate(
            serverUrlConfigured: false, routedViaAgent: false, ConfiguredSettings())
            .Should().BeFalse();
    }

    [Fact]
    public void IsCandidate_RoutedViaAgent_False()
    {
        // Tunnel-relayed SSH is out of v1: the run would ride the tunnel of the very agent
        // the installer restarts.
        GatewayAgentInstallService.IsCandidate(
            serverUrlConfigured: true, routedViaAgent: true, ConfiguredSettings())
            .Should().BeFalse();
    }

    [Fact]
    public void IsCandidate_SshNotConfigured_False()
    {
        GatewayAgentInstallService.IsCandidate(true, false, null).Should().BeFalse();

        var disabled = ConfiguredSettings();
        disabled.Enabled = false;
        GatewayAgentInstallService.IsCandidate(true, false, disabled).Should().BeFalse();

        var noHost = ConfiguredSettings();
        noHost.Host = null;
        GatewayAgentInstallService.IsCandidate(true, false, noHost).Should().BeFalse();

        var noCredentials = ConfiguredSettings();
        noCredentials.Password = null;
        GatewayAgentInstallService.IsCandidate(true, false, noCredentials).Should().BeFalse();
    }

    [Fact]
    public void IsCandidate_StoredKeyCountsAsCredentials()
    {
        var settings = ConfiguredSettings();
        settings.Password = null;
        settings.HasStoredKey = true;

        GatewayAgentInstallService.IsCandidate(true, false, settings).Should().BeTrue();
    }

    [Fact]
    public void StartRun_SecondStartWhileRunning_IsRefused()
    {
        var state = new GatewayAgentInstallState();
        state.StartRun("site-a", isUpgrade: false, "busy");

        var refusal = () => state.StartRun("site-a", isUpgrade: true, "busy");

        refusal.Should().Throw<InvalidOperationException>().WithMessage("busy");
    }

    [Fact]
    public void StartRun_OtherSiteOrFinishedRun_IsAllowed()
    {
        var state = new GatewayAgentInstallState();
        var first = state.StartRun("site-a", isUpgrade: false, "busy");

        // Another site is independent.
        state.StartRun("site-b", isUpgrade: false, "busy");

        // A finished run frees the site.
        first.Complete(GatewayAgentInstallStatus.Failed, 1, null);
        var second = state.StartRun("site-a", isUpgrade: true, "busy");

        state.GetRun("site-a").Should().BeSameAs(second);
    }

    [Fact]
    public void Run_TranscriptAccumulatesAndUpdatesFire()
    {
        var run = new GatewayAgentInstallRun("site-a", isUpgrade: false);
        var updates = 0;
        run.Updated += () => updates++;

        run.Append("==> Downloading agent binary\n");
        run.Append("  ✓ agent (linux-arm64)\n");
        run.Complete(GatewayAgentInstallStatus.Succeeded, 0, null);

        run.Transcript.Should().Be("==> Downloading agent binary\n  ✓ agent (linux-arm64)\n");
        run.Status.Should().Be(GatewayAgentInstallStatus.Succeeded);
        run.ExitCode.Should().Be(0);
        updates.Should().Be(3);
    }
}

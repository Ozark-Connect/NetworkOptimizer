using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;
using static NetworkOptimizer.Web.Tests.Firmware.RolloutFixtures;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// An SSH install the device closes mid-command (it does that once it starts to flash) is held, not followed by a
/// second command: the step watch confirms it, or the Console gets the same link once <c>SshDropHold</c> passes.
/// Every path that sent a Console command before still sends it; only the timing of the unconfirmed case moves.
/// </summary>
public class SshDroppedSessionTests
{
    private const int Online = (int)UniFiDeviceState.Connected;
    private const int Offline = (int)UniFiDeviceState.Disconnected;
    private const string Dropped ="Connection failed: An established connection was aborted by the server.";
    private const string PinnedUrl = "https://example.test/pinned.bin";

    private static async Task<(RolloutHarness Harness, FirmwareRolloutPlan Plan)> PinnedAsync(string sshResult)
    {
        var harness = new RolloutHarness();
        harness.Commands.SshResult = FirmwareCommandResult.Failed(sshResult);
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.TargetImages.Add(new PlanTargetImage { Mac = ApMac, Version = ToVersion, Url = PinnedUrl, Pinned = true });
        var plan = await harness.SeedRunningPlanAsync(document, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        return (harness, plan);
    }

    [Theory]
    [InlineData((int)UniFiDeviceState.Provisioning)]
    [InlineData((int)UniFiDeviceState.Upgrading)]
    [InlineData((int)UniFiDeviceState.Disconnected)]
    public async Task ADroppedSessionTheDeviceThenActsOn_SendsNoSecondCommand(int state)
    {
        var (harness, plan) = await PinnedAsync(Dropped);
        using var _ = harness;

        await harness.TickAsync();
        harness.Observer.Set(ApMac, state, FromVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(10));
        await harness.TickAsync(harness.Orchestrator.SshDropHold + TimeSpan.FromMinutes(1));

        harness.Commands.ExternalCommands.Should().BeEmpty("the device showed the upgrade the SSH command started");
        harness.Commands.UpgradeCommands.Should().BeEmpty();
        harness.Commands.SshCommands.Should().ContainSingle();
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().NotBe(FirmwareRolloutStepState.Failed);
    }

    [Fact]
    public async Task ADroppedSessionFollowedByTheTargetVersion_SendsNoSecondCommand()
    {
        // A short cycle can fall entirely between two passes.
        var (harness, plan) = await PinnedAsync(Dropped);
        using var _ = harness;

        await harness.TickAsync();
        harness.Observer.Set(ApMac, Online, ToVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(10));
        await harness.TickAsync(harness.Orchestrator.SshDropHold + TimeSpan.FromMinutes(1));

        harness.Commands.ExternalCommands.Should().BeEmpty();
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().NotBe(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task ADroppedSessionTheDeviceNeverActsOn_SendsTheConsoleTheSameLinkAfterTheHold()
    {
        var (harness, plan) = await PinnedAsync(Dropped);
        using var _ = harness;

        await harness.TickAsync();
        await harness.TickAsync(harness.Orchestrator.SshDropHold - TimeSpan.FromSeconds(10));
        harness.Commands.ExternalCommands.Should().BeEmpty("the hold is still running");

        await harness.TickAsync(TimeSpan.FromSeconds(20));
        harness.Commands.ExternalCommands.Should().ContainSingle().Which.Item2.Should().Be(PinnedUrl);
        harness.Commands.UpgradeCommands.Should().BeEmpty("a build chosen by hand never falls back to the Console's own");
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task ADroppedSessionTheConsoleThenRefuses_FailsTheStepAsBefore()
    {
        var (harness, plan) = await PinnedAsync(Dropped);
        using var _ = harness;
        harness.Commands.ExternalResult = FirmwareCommandResult.Failed("404");

        await harness.TickAsync();
        await harness.TickAsync(harness.Orchestrator.SshDropHold + TimeSpan.FromSeconds(10));

        var step = await harness.StepAsync(plan.Id, ApMac);
        step.State.Should().Be(FirmwareRolloutStepState.Failed);
        step.Error.Should().StartWith("The upgrade could not be started");
        harness.Commands.SshCommands.Should().ContainSingle("SSH already had its try with this link");
    }

    [Theory]
    [InlineData("Connection failed: No connection could be made because the target machine actively refused it.")]
    [InlineData("Authentication failed: Permission denied (password).")]
    [InlineData("Connection failed: could not reach 192.0.2.10:22 via the site's agent - open timed out")]
    public async Task AnSshInstallThatNeverConnected_SendsTheConsoleAtOnce(string failure)
    {
        var (harness, _) = await PinnedAsync(failure);
        using var __ = harness;

        await harness.TickAsync();

        harness.Commands.ExternalCommands.Should().ContainSingle("nothing reached the device, so nothing is held")
            .Which.Item2.Should().Be(PinnedUrl);
    }

    [Fact]
    public async Task AnSshRetryTheDeviceClosesThenActsOn_NoLongerFailsTheStep()
    {
        // The Console command stalls; the SSH retry's session drops as the device starts to flash.
        using var harness = new RolloutHarness();
        harness.Commands.Catalog.Add(new UniFiFirmwareCatalogEntry
        {
            BaseModel = "U6PRO", Version = ToVersion, Url = "https://fw-download.example.net/u6pro.bin",
        });
        harness.Commands.SshResult = FirmwareCommandResult.Failed(Dropped);
        var plan = await harness.SeedRunningPlanAsync(Document(Wave(1, PlanStep(ApMac))), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await harness.TickAsync(FirmwareRolloutOrchestrator.CommandGraceWindow + TimeSpan.FromSeconds(10));
        harness.Commands.SshCommands.Should().ContainSingle();
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);

        harness.Observer.Set(ApMac, Offline, FromVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(10));
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Down);
    }

    [Fact]
    public async Task AnSshRetryTheDeviceClosesButNeverActsOn_FailsAfterTheGraceWindow()
    {
        using var harness = new RolloutHarness();
        harness.Commands.Catalog.Add(new UniFiFirmwareCatalogEntry
        {
            BaseModel = "U6PRO", Version = ToVersion, Url = "https://fw-download.example.net/u6pro.bin",
        });
        harness.Commands.SshResult = FirmwareCommandResult.Failed(Dropped);
        var plan = await harness.SeedRunningPlanAsync(Document(Wave(1, PlanStep(ApMac))), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await harness.TickAsync(FirmwareRolloutOrchestrator.CommandGraceWindow + TimeSpan.FromSeconds(10));
        await harness.TickAsync(FirmwareRolloutOrchestrator.CommandGraceWindow + TimeSpan.FromSeconds(10));

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Failed);
        harness.Commands.SshCommands.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Connection failed: An established connection was aborted by the server.", true)]
    [InlineData("Connection failed: No connection could be made because the target machine actively refused it.", false)]
    [InlineData("Connection failed: The connection to the remote server was closed before a valid SSH identification string was received.", false)]
    [InlineData("Authentication failed: Permission denied (password).", false)]
    [InlineData(null, false)]
    public void OnlyAnEstablishedSessionTheDeviceClosed_IsADrop(string? message, bool dropped) =>
        NetworkOptimizer.Web.Services.Ssh.SshFailureSummary.IsSessionDropped(message).Should().Be(dropped);
}

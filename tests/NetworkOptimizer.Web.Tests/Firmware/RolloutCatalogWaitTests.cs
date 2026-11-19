using System.Diagnostics;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The catalog restage wait, with its timing passed explicitly. Every other test skips the wait
/// (<see cref="TestTimingSetup"/>).
/// </summary>
public class RolloutCatalogWaitTests
{
    private static readonly IReadOnlyList<UniFiFirmwareCatalogEntry> Before = [Entry("U6M", "6.6.1")];
    private static readonly IReadOnlyList<UniFiFirmwareCatalogEntry> After = [Entry("U6M", "6.7.10")];

    private static UniFiFirmwareCatalogEntry Entry(string model, string version) =>
        new() { BaseModel = model, Version = version };

    private static Task<IReadOnlyList<UniFiFirmwareCatalogEntry>> WaitAsync(
        FakeFirmwareCommandClient commands, TimeSpan wait, TimeSpan poll, CancellationToken cancellationToken = default) =>
        RolloutPlanComposer.WaitForCatalogAsync(
            commands, Before, "beta", wait, poll, TimeProvider.System, logger: null, cancellationToken);

    [Fact]
    public async Task ARestagedFirstReadReturnsAtOnce()
    {
        var commands = new FakeFirmwareCommandClient { CatalogOnCall = _ => After };

        var catalog = await WaitAsync(commands, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        catalog.Should().BeSameAs(After);
        commands.CheckForUpdatesCalls.Should().Be(1);
    }

    [Fact]
    public async Task KeepsPollingUntilTheCatalogChanges()
    {
        var commands = new FakeFirmwareCommandClient { CatalogOnCall = n => n < 3 ? Before : After };

        var catalog = await WaitAsync(commands, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1));

        catalog.Should().BeSameAs(After);
        commands.CheckForUpdatesCalls.Should().Be(3);
    }

    [Fact]
    public async Task AnEmptyCatalogIsNeverARestage()
    {
        // Empty is this app's "could not read it" answer, so it must not end the wait early.
        var commands = new FakeFirmwareCommandClient { CatalogOnCall = n => n < 3 ? [] : After };

        var catalog = await WaitAsync(commands, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1));

        catalog.Should().BeSameAs(After);
        commands.CheckForUpdatesCalls.Should().Be(3);
    }

    [Fact]
    public async Task GivesUpAtTheDeadlineWithTheLastCatalogRead()
    {
        var commands = new FakeFirmwareCommandClient { CatalogOnCall = _ => Before };
        var wait = TimeSpan.FromMilliseconds(100);
        var clock = Stopwatch.StartNew();

        var catalog = await WaitAsync(commands, wait, TimeSpan.FromMilliseconds(10));

        clock.Elapsed.Should().BeGreaterThanOrEqualTo(wait);
        catalog.Should().BeSameAs(Before);
        commands.CheckForUpdatesCalls.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task AZeroWaitReadsOnceAndDoesNotPoll()
    {
        var commands = new FakeFirmwareCommandClient { CatalogOnCall = _ => Before };

        var catalog = await WaitAsync(commands, TimeSpan.Zero, TimeSpan.FromSeconds(30));

        catalog.Should().BeSameAs(Before);
        commands.CheckForUpdatesCalls.Should().Be(1);
    }

    [Fact]
    public async Task CancellationStopsTheWait()
    {
        var commands = new FakeFirmwareCommandClient { CatalogOnCall = _ => Before };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => WaitAsync(commands, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

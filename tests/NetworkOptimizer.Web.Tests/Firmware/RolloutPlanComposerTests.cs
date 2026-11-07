using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// Shared UniFi Network builds: recorded per package platform, offered to a console only with a
/// package some console of its platform downloaded, and never installed from a version-derived URL.
/// </summary>
public class RolloutPlanComposerTests
{
    private const string Arm64 = "uos-deb13-arm64";
    private const string Amd64 = "uos-deb13-amd64";
    private const string Arm64Url = "https://fw-download.example.test/data/unifi-native/aaaa-uos-deb13-arm64-9.1.0-1-1-00000000.deb";

    private sealed class InMemoryFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly string _name = Guid.NewGuid().ToString();

        public NetworkOptimizerDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseInMemoryDatabase(_name).Options);
    }

    private readonly SharedFirmwareCatalogRepository _catalog =
        new(new InMemoryFactory(), NullLogger<SharedFirmwareCatalogRepository>.Instance);

    private readonly FakeFirmwareCommandClient _commands = new();
    private readonly FakeRolloutPlanningSource _planning = new();

    /// <summary>An arm64 Cloud Gateway on 9.0.0 that has not noticed any update itself.</summary>
    private void ConsoleOffersNothing(string? platform = Arm64)
    {
        _commands.ConsoleInfo!.NetworkApplication!.UpdateAvailable = null;
        _commands.NetworkPackages = platform == null ? null : new ConsoleNetworkPackages(platform, []);
    }

    private Task<RolloutPlanInputs> GatherAsync() =>
        RolloutPlanComposer.GatherAsync(_planning, [], _commands, sharedCatalog: _catalog);

    private Task AddPackageAsync(string platform, string version, string url) =>
        _catalog.UpsertNetworkAppPackagesAsync(
            [new SharedNetworkAppPackage { Platform = platform, Version = version, Url = url }]);

    [Fact]
    public async Task AStandaloneServersOfferIsNeverAdoptedByAnArm64Console()
    {
        ConsoleOffersNothing();
        await _catalog.UpsertNetworkAppBuildAsync(SharedNetworkAppBuild.StandalonePlatform, "release", "9.1.0", null);
        await AddPackageAsync(Amd64, "9.1.0", "https://fw-download.example.test/data/unifi/bbbb-uos-deb13-amd64-9.1.0-1-1-00000000.deb");

        var inputs = await GatherAsync();

        inputs.Console!.NetworkApplication!.UpdateAvailable.Should().BeNull();
        inputs.NetworkAppPackageUrl.Should().BeNull();
    }

    [Fact]
    public async Task AnArm64BuildWithAnArm64PackageIsAdoptedWithThatPackagesUrl()
    {
        ConsoleOffersNothing();
        await _catalog.UpsertNetworkAppBuildAsync(Arm64, "release", "9.1.0", null);
        await AddPackageAsync(Arm64, "9.1.0", Arm64Url);

        var inputs = await GatherAsync();

        inputs.Console!.NetworkApplication!.UpdateAvailable.Should().Be("9.1.0");
        inputs.NetworkAppPackageUrl.Should().Be(Arm64Url);
    }

    [Fact]
    public async Task AnArm64BuildWithNoPackageIsNotAdopted()
    {
        ConsoleOffersNothing();
        await _catalog.UpsertNetworkAppBuildAsync(Arm64, "release", "9.1.0", null);

        var inputs = await GatherAsync();

        inputs.Console!.NetworkApplication!.UpdateAvailable.Should().BeNull();
        inputs.NetworkAppPackageUrl.Should().BeNull();
    }

    [Fact]
    public async Task ARowRecordedWithoutAPlatformIsNeverAdopted()
    {
        ConsoleOffersNothing();
        await _catalog.UpsertNetworkAppBuildAsync(string.Empty, "release", "9.1.0", null);
        await AddPackageAsync(Arm64, "9.1.0", Arm64Url);

        var inputs = await GatherAsync();

        inputs.Console!.NetworkApplication!.UpdateAvailable.Should().BeNull();
    }

    [Fact]
    public async Task AConsoleWhosePlatformIsUnknownAdoptsNothing()
    {
        ConsoleOffersNothing(platform: null);
        await _catalog.UpsertNetworkAppBuildAsync(Arm64, "release", "9.1.0", null);
        await AddPackageAsync(Arm64, "9.1.0", Arm64Url);

        var inputs = await GatherAsync();

        inputs.Console!.NetworkApplication!.UpdateAvailable.Should().BeNull();
    }

    [Fact]
    public async Task RecordsTheConsolesOfferUnderItsPackagePlatform()
    {
        _commands.NetworkPackages = new ConsoleNetworkPackages(Arm64, []);

        await GatherAsync();

        var row = (await _catalog.ListNetworkAppBuildsAsync()).Should().ContainSingle().Subject;
        row.Platform.Should().Be(Arm64);
        row.Version.Should().Be("9.1.0");
    }

    [Fact]
    public async Task RecordsNothingWhenTheConsolesPlatformIsUnknown()
    {
        _commands.NetworkPackages = null;

        await GatherAsync();

        (await _catalog.ListNetworkAppBuildsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TheConsolesOwnOfferWithNoPackagePlansNoUrlAndSaysSo()
    {
        _commands.NetworkPackages = new ConsoleNetworkPackages(Arm64, []);

        var inputs = await GatherAsync();
        var plan = RolloutPlanComposer.Plan(inputs, new FirmwareRolloutSettings { IncludeUniFiNetwork = true });

        plan.Document.IncludesUniFiNetworkUpdate.Should().BeTrue();
        plan.Document.NetworkAppUpdate.Url.Should().BeNull("a URL derived from the version 404s for most builds");
        plan.Document.Notes.Should().Contain(n => n.Contains("relies on the Console's own update"));
    }

    [Fact]
    public async Task TheConsolesOwnOfferWithAKnownPackagePlansItsRealUrl()
    {
        _commands.NetworkPackages = new ConsoleNetworkPackages(Arm64, []);
        await AddPackageAsync(Arm64, "9.1.0", Arm64Url);

        var inputs = await GatherAsync();
        var plan = RolloutPlanComposer.Plan(inputs, new FirmwareRolloutSettings { IncludeUniFiNetwork = true });

        plan.Document.NetworkAppUpdate.Url.Should().Be(Arm64Url);
        plan.Document.Notes.Should().NotContain(n => n.Contains("relies on the Console's own update"));
    }
}

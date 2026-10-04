using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Web.Services.Firmware;
using NetworkOptimizer.Web.Services.Gates;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// Adding a firmware link by hand: what it is filed as, under which code, and that nothing the
/// rules cannot place reaches the shared catalog.
/// </summary>
public class SharedFirmwareCatalogServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private sealed class InMemoryFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly string _name = Guid.NewGuid().ToString();

        public NetworkOptimizerDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseInMemoryDatabase(_name).Options);
    }

    private readonly InMemoryFactory _db = new();
    private readonly SharedFirmwareCatalogRepository _catalog;
    private readonly AuditContext _audit = new();
    private readonly SharedFirmwareCatalogService _service;

    public SharedFirmwareCatalogServiceTests()
    {
        _catalog = new SharedFirmwareCatalogRepository(_db, NullLogger<SharedFirmwareCatalogRepository>.Instance);
        _service = new SharedFirmwareCatalogService(
            _catalog, _audit, new FakeTimeProvider(Now), NullLogger<SharedFirmwareCatalogService>.Instance);
    }

    [Fact]
    public async Task AddsAUniFiOsImageOnEarlyAccess_StampedWithTheAddTime()
    {
        var result = await _service.AddFirmwareUrlAsync(
            "https://fw-download.ubnt.com/data/unifi-dream/6a7a-UCGF-6.0.11-847f967b-d464-428f-8b51-96dbc35b0f8b.bin");

        result.Succeeded.Should().BeTrue();
        result.Kind.Should().Be(FirmwareUrlKind.UniFiOs);
        result.Target.Should().Be("UCGF");
        var row = (await _catalog.ListUniFiOsBuildsAsync()).Should().ContainSingle().Subject;
        row.Channel.Should().Be(FirmwareChannels.Beta);
        row.Version.Should().Be("6.0.11");
        row.PublishedUtc.Should().Be(Now, "a URL carries no publish date, and an unknown one would skip the release-age gate");
    }

    [Fact]
    public async Task AddsADeviceImageUnderTheModelInItsFileName()
    {
        var result = await _service.AddFirmwareUrlAsync(
            "https://fw-download.ubnt.com/data/unifi-firmware/b4a0-UAPA6A5-8.7.11-367ab339-3771-4c2d-84e3-b94266eac0a6.bin");

        result.Kind.Should().Be(FirmwareUrlKind.Device);
        result.MatchedExisting.Should().BeFalse();
        var row = (await _catalog.ListDeviceBuildsAsync()).Should().ContainSingle().Subject;
        row.Model.Should().Be("UAPA6A5");
        row.Channel.Should().Be(FirmwareChannels.Beta);
    }

    [Fact]
    public async Task TakesTheModelFromAKnownBuildWhenTheFileNameCarriesAnotherCode()
    {
        // The console files this switch-class gateway as UXGPRO, but its images are named UXGPROV2.
        await _catalog.UpsertDeviceBuildsAsync(
        [
            new SharedFirmwareBuild
            {
                Model = "UXGPRO", Channel = FirmwareChannels.Release, Version = "5.1.26.33914",
                Url = "https://fw-download.ubnt.com/data/unifi-firmware/99f0-UXGPROV2-5.1.26-f0594436-4f08-4d3d-a852-e9004f3e7d43.bin",
            },
        ]);

        var result = await _service.AddFirmwareUrlAsync(
            "https://fw-download.ubnt.com/data/unifi-firmware/1c2d-UXGPROV2-5.1.30-0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9.bin");

        result.Target.Should().Be("UXGPRO");
        result.MatchedExisting.Should().BeTrue();
    }

    [Fact]
    public async Task PlacesAnUnknownLayoutByAKnownBuildOnTheSamePath()
    {
        await _catalog.UpsertUniFiOsBuildsAsync(
        [
            new SharedUniFiOsBuild
            {
                Platform = "ABC1", Channel = FirmwareChannels.Release, Version = "v1.0.0+aaa",
                Url = "https://fw-download.ubnt.com/data/unifi-other/0a0a-ABC1-1.0.0-00112233.bin",
            },
        ]);

        var result = await _service.AddFirmwareUrlAsync(
            "https://fw-download.ubnt.com/data/unifi-other/0b0b-ABC1-1.1.0-44556677.bin");

        result.Kind.Should().Be(FirmwareUrlKind.UniFiOs);
        result.Target.Should().Be("ABC1");
        (await _catalog.ListUniFiOsBuildsAsync()).Should().Contain(b => b.Version == "1.1.0" && b.Channel == FirmwareChannels.Beta);
    }

    [Fact]
    public async Task RefusesAnUnknownLayoutNothingInTheCatalogExplains()
    {
        var result = await _service.AddFirmwareUrlAsync(
            "https://fw-download.ubnt.com/data/unifi-other/0b0b-ABC1-1.1.0-44556677.bin");

        result.Succeeded.Should().BeFalse();
        (await _catalog.ListDeviceBuildsAsync()).Should().BeEmpty();
        (await _catalog.ListUniFiOsBuildsAsync()).Should().BeEmpty();
        _audit.Drain().Suppressed.Should().BeTrue("a refused link changed nothing");
    }

    [Fact]
    public async Task AddsTheNetworkApplicationOnEarlyAccess()
    {
        var result = await _service.AddFirmwareUrlAsync("https://dl.ui.com/unifi/10.6.97/unifi-native_sysvinit.deb");

        result.Kind.Should().Be(FirmwareUrlKind.NetworkApp);
        (await _catalog.FindNewerNetworkAppBuildAsync(FirmwareChannels.Beta, "10.5.0"))!.Version.Should().Be("10.6.97");
    }

    [Fact]
    public async Task RecordsWhatWasAddedForTheAuditLog()
    {
        await _service.AddFirmwareUrlAsync(
            "https://fw-download.ubnt.com/data/unifi-dream/6a7a-UCGF-6.0.11-847f967b-d464-428f-8b51-96dbc35b0f8b.bin");

        var drained = _audit.Drain();
        drained.Suppressed.Should().BeFalse();
        drained.TargetId.Should().Be("UCGF");
        drained.Details.Should().NotBeNull();
    }
}

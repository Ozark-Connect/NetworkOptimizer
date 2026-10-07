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

    /// <summary>Serves bodies by URL; anything else is a 404. Records every request.</summary>
    private sealed class FakeDownloads : HttpMessageHandler, IHttpClientFactory
    {
        public Dictionary<string, byte[]> Bodies { get; } = new();
        public List<string> Requested { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(Bodies.TryGetValue(url, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private readonly InMemoryFactory _db = new();
    private readonly SharedFirmwareCatalogRepository _catalog;
    private readonly AuditContext _audit = new();
    private readonly FakeDownloads _downloads = new();
    private readonly SharedFirmwareCatalogService _service;

    public SharedFirmwareCatalogServiceTests()
    {
        _catalog = new SharedFirmwareCatalogRepository(_db, NullLogger<SharedFirmwareCatalogRepository>.Instance);
        _service = new SharedFirmwareCatalogService(
            _catalog, _audit, new FakeTimeProvider(Now), _downloads, NullLogger<SharedFirmwareCatalogService>.Instance);
    }

    // --- One image, a family of models ---------------------------------------------------------

    private const string FamilyMd5 = "653094fe0cd20643c9c481a87544cdca";
    private const string FamilyLink = "https://dl.ui.com/unifi/firmware/U7PRO/8.8.8.20113/BZ.ipq53xx_8.8.8+20113.260910.1347.bin";

    /// <summary>Two catalog models sharing one image, as the Consoles offered them, plus an unrelated one.</summary>
    private Task SeedFamilyAsync(string version = "8.8.8.20113") => _catalog.UpsertDeviceBuildsAsync(
    [
        new SharedFirmwareBuild { Model = "U7PRO", Channel = FirmwareChannels.Beta, Version = version, Md5Sum = FamilyMd5, Url = $"https://fw-download.ubnt.com/data/unifi-firmware/0001-U7PRO-{version}-a.bin" },
        new SharedFirmwareBuild { Model = "UAPA6A4", Channel = FirmwareChannels.Beta, Version = version, Md5Sum = FamilyMd5, Url = $"https://fw-download.ubnt.com/data/unifi-firmware/0002-UAPA6A4-{version}-b.bin" },
        new SharedFirmwareBuild { Model = "U6ENT", Channel = FirmwareChannels.Beta, Version = version, Md5Sum = "02b4c862b9cd9e62add15814a7f74294", Url = $"https://fw-download.ubnt.com/data/unifi-firmware/0003-U6ENT-{version}-c.bin" },
    ]);

    [Fact]
    public async Task AFamilyLink_CoversEveryModelThatSharesItsImage()
    {
        await SeedFamilyAsync();
        _downloads.Bodies[FamilyLink + ".md5sum"] = System.Text.Encoding.ASCII.GetBytes($"{FamilyMd5}  BZ.ipq53xx_8.8.8+20113.260910.1347.bin\n");

        var result = await _service.AddFirmwareUrlAsync(FamilyLink);

        result.Succeeded.Should().BeTrue();
        result.Models.Should().BeEquivalentTo(["U7PRO", "UAPA6A4"]);
        result.Target.Should().Be("U7PRO");
        // Same bytes, so each model installs from its own catalog URL: the host networks already allow.
        result.ModelUrls.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["U7PRO"] = "https://fw-download.ubnt.com/data/unifi-firmware/0001-U7PRO-8.8.8.20113-a.bin",
            ["UAPA6A4"] = "https://fw-download.ubnt.com/data/unifi-firmware/0002-UAPA6A4-8.8.8.20113-b.bin",
        });
        result.ToPin()!.UrlFor("UAPA6A4").Should().Be("https://fw-download.ubnt.com/data/unifi-firmware/0002-UAPA6A4-8.8.8.20113-b.bin");
        _downloads.Requested.Should().ContainSingle("the published .md5sum is enough; the image is never downloaded");
    }

    [Fact]
    public async Task AFamilyLinkWithoutAPublishedMd5_HashesTheImage()
    {
        await SeedFamilyAsync();
        var image = System.Text.Encoding.ASCII.GetBytes("image bytes");
        var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(image)).ToLowerInvariant();
        await _catalog.UpsertDeviceBuildsAsync(
            [new SharedFirmwareBuild { Model = "UKPW", Channel = FirmwareChannels.Beta, Version = "8.8.8.20113", Md5Sum = md5, Url = "https://fw-download.ubnt.com/data/unifi-firmware/0004-UKPW-8.8.8-d.bin" }]);
        const string link = "https://dl.ui.com/unifi/firmware/G7LR/8.8.8.20113/BZ2.ipq53xx_8.8.8+20113.260910.1353.bin";
        _downloads.Bodies[link] = image;

        var result = await _service.AddFirmwareUrlAsync(link);

        result.Models.Should().BeEquivalentTo(["UKPW"], "G7LR is a folder, not a model; the md5 says which model it is");
    }

    [Fact]
    public async Task AnUnseenBuild_TakesTheFolderModelsKnownFamily()
    {
        // No Console has been offered 8.8.9 yet, so nothing matches its md5.
        await SeedFamilyAsync();
        const string link = "https://dl.ui.com/unifi/firmware/U7PRO/8.8.9.20138/BZ.ipq53xx_8.8.9+20138.bin";

        var result = await _service.AddFirmwareUrlAsync(link);

        result.Models.Should().BeEquivalentTo(["U7PRO", "UAPA6A4"]);
        result.Version.Should().Be("8.8.9.20138");
        result.ModelUrls!.Values.Should().AllBe(link, "no Console has this build, so the pasted link is the only source");
    }

    [Fact]
    public async Task AnUnseenBuildInAFolderThatIsNotAModel_IsRefused()
    {
        await SeedFamilyAsync();

        var result = await _service.AddFirmwareUrlAsync(
            "https://dl.ui.com/unifi/firmware/USMULTUS8/7.6.2.17186/US.MULT.US8_7.6.2+17186.260909.1319.bin");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("USMULTUS8");
        _audit.Drain().Suppressed.Should().BeTrue();
    }

    [Fact]
    public async Task ALinkAlreadyInTheCatalog_TakesItsMd5FromThere()
    {
        await SeedFamilyAsync();

        var result = await _service.AddFirmwareUrlAsync("https://fw-download.ubnt.com/data/unifi-firmware/0002-UAPA6A4-8.8.8.20113-b.bin");

        result.Models.Should().BeEquivalentTo(["U7PRO", "UAPA6A4"]);
        _downloads.Requested.Should().BeEmpty();
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
    public async Task ADownloadLinkNothingInTheCatalogExplains_IsRefused()
    {
        // A dl.ui.com folder is not necessarily a model code, so with no catalog to place it, it is not guessed.
        var result = await _service.AddFirmwareUrlAsync(
            "https://dl.ui.com/unifi/firmware/U7PRO/8.8.8.20113/BZ.ipq53xx_8.8.8+20113.260910.1347.bin");

        result.Succeeded.Should().BeFalse();
        (await _catalog.ListDeviceBuildsAsync()).Should().BeEmpty();
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
        result.DisplayName.Should().Be("UniFi Network 10.6.97");
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
        drained.TargetName.Should().Be("UniFi OS 6.0.11 for UCG-Fiber", "the Audit Log names the build the way the page does");
        drained.Details.Should().NotBeNull();
    }
}

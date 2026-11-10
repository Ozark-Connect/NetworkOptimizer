using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Gates;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// Removing a site removes its floor plans, report PDFs and default-named buckets, and nothing
/// that belongs to another site, to main, or to a custom bucket name.
/// </summary>
public sealed class SiteLeftoverCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "no-site-leftovers-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Mock<IInfluxDbProvisioningService> _provisioner = new();

    public SiteLeftoverCleanupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir; a leftover is harmless */ }
        GC.SuppressFinalize(this);
    }

    private sealed class TestDbFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly DbContextOptions<NetworkOptimizerDbContext> _options;
        public TestDbFactory(DbContextOptions<NetworkOptimizerDbContext> options) => _options = options;
        public NetworkOptimizerDbContext CreateDbContext() => new(_options);
    }

    private sealed class NoopCredentialProtection : ICredentialProtectionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string encrypted) => encrypted;
        public bool IsEncrypted(string? value) => false;
        public void EnsureKeyExists() { }
    }

    private static MonitoringSettings MainSettings(string bucket = "network_monitoring", string longterm = "network_monitoring_longterm") => new()
    {
        InfluxDbUrl = "http://influx.example:8086",
        InfluxDbOrg = "network-optimizer",
        InfluxDbToken = "org-token",
        InfluxDbBucket = bucket,
        InfluxDbLongtermBucket = longterm,
    };

    private async Task<SiteLeftoverCleanup> CleanupAsync(MonitoringSettings? main)
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        if (main != null)
        {
            await using var db = new NetworkOptimizerDbContext(options);
            db.MonitoringSettings.Add(main);
            await db.SaveChangesAsync();
        }
        return new SiteLeftoverCleanup(new TestDbFactory(options), new NoopCredentialProtection(),
            _provisioner.Object, NullLogger<SiteLeftoverCleanup>.Instance);
    }

    private void OrgResolves() =>
        _provisioner.Setup(p => p.TryResolveOrgIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("org-1");

    // --- Buckets ---

    [Fact]
    public void DefaultBucketNames_AreTheSlugPrefixedFallbacks()
    {
        SiteLeftoverCleanup.DefaultBucketNames("branch-office", MainSettings()).Should().Equal(
            "branch-office-network_monitoring", "branch-office-network_monitoring_longterm");
    }

    [Fact]
    public void DefaultBucketNames_TwoSites_NeverOverlap()
    {
        var a = SiteLeftoverCleanup.DefaultBucketNames("site-a", MainSettings());
        var b = SiteLeftoverCleanup.DefaultBucketNames("site-b", MainSettings());

        a.Should().NotIntersectWith(b);
    }

    [Fact]
    public void DefaultBucketNames_NoLongtermBucketOnMain_ListsThePrimaryOnly()
    {
        SiteLeftoverCleanup.DefaultBucketNames("site-a", MainSettings(longterm: "")).Should().Equal("site-a-network_monitoring");
    }

    [Fact]
    public void DefaultBucketNames_FallbackCollidingWithMain_ExcludesMainsName()
    {
        var main = MainSettings(bucket: "net", longterm: "site-a-net");

        var names = SiteLeftoverCleanup.DefaultBucketNames("site-a", main);

        names.Should().NotContain(new[] { "net", "site-a-net" });
        names.Should().Equal("site-a-site-a-net");
    }

    [Fact]
    public async Task RemoveAsync_DeletesOnlyTheDefaultNames()
    {
        OrgResolves();
        IReadOnlyList<string>? asked = null;
        _provisioner.Setup(p => p.DeleteBucketsAsync(It.IsAny<string>(), It.IsAny<string>(), "org-1",
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<string>, CancellationToken>((_, _, _, names, _) => asked = names)
            .ReturnsAsync((string _, string _, string _, IReadOnlyList<string> names, CancellationToken _) => names);
        var cleanup = await CleanupAsync(MainSettings());

        await cleanup.RemoveAsync("site-a-" + Guid.NewGuid().ToString("N")[..6]);

        asked.Should().NotBeNull();
        asked!.Should().HaveCount(2).And.OnlyContain(n => n.EndsWith("-network_monitoring") || n.EndsWith("-network_monitoring_longterm"));
        asked.Should().NotContain(new[] { "network_monitoring", "network_monitoring_longterm" });
    }

    [Fact]
    public async Task RemoveAsync_CustomBucketName_IsNeverDeleted()
    {
        // A site configured with "shared-bucket" keeps it: only the slug-prefixed fallbacks are asked for.
        OrgResolves();
        IReadOnlyList<string>? asked = null;
        _provisioner.Setup(p => p.DeleteBucketsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<string>, CancellationToken>((_, _, _, names, _) => asked = names)
            .ReturnsAsync(Array.Empty<string>());
        var cleanup = await CleanupAsync(MainSettings());

        await cleanup.RemoveAsync("site-a");

        asked.Should().NotContain("shared-bucket");
        asked.Should().Equal("site-a-network_monitoring", "site-a-network_monitoring_longterm");
    }

    [Fact]
    public async Task RemoveAsync_InfluxNotConfigured_CompletesWithoutCallingInflux()
    {
        var cleanup = await CleanupAsync(main: null);

        await cleanup.Invoking(c => c.RemoveAsync("site-a")).Should().NotThrowAsync();

        _provisioner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveAsync_InfluxUnreachable_Completes()
    {
        _provisioner.Setup(p => p.TryResolveOrgIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var cleanup = await CleanupAsync(MainSettings());

        await cleanup.Invoking(c => c.RemoveAsync("site-a")).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RemoveAsync_TokenCannotManageBuckets_DeletesNothing()
    {
        _provisioner.Setup(p => p.TryResolveOrgIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var cleanup = await CleanupAsync(MainSettings());

        await cleanup.RemoveAsync("site-a");

        _provisioner.Verify(p => p.DeleteBucketsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Directories ---

    [Fact]
    public async Task RemoveDirectory_RemovesTheSiteAndLeavesTheOtherSiteAndTheRoot()
    {
        var root = Path.Combine(_dir, "floor-plans");
        var removed = Path.Combine(root, "site-a");
        var kept = Path.Combine(root, "site-b");
        Directory.CreateDirectory(Path.Combine(removed, "1"));
        Directory.CreateDirectory(Path.Combine(kept, "1"));
        File.WriteAllText(Path.Combine(removed, "1", "floor_1.png"), "a");
        File.WriteAllText(Path.Combine(kept, "1", "floor_1.png"), "b");
        File.WriteAllText(Path.Combine(root, "default.png"), "main");
        var cleanup = await CleanupAsync(main: null);

        cleanup.RemoveDirectory("site-a", "floor plan", root, removed).Should().BeTrue();

        Directory.Exists(removed).Should().BeFalse();
        File.Exists(Path.Combine(kept, "1", "floor_1.png")).Should().BeTrue();
        File.Exists(Path.Combine(root, "default.png")).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveDirectory_SubdirectoriesOnly_KeepsTheDefaultSitesBuildingImages()
    {
        // Site slug "2" and the default site's building 2 share {root}/2.
        var root = Path.Combine(_dir, "floor-plans");
        var shared = Path.Combine(root, "2");
        Directory.CreateDirectory(Path.Combine(shared, "1"));
        File.WriteAllText(Path.Combine(shared, "floor_1.png"), "default site, building 2");
        File.WriteAllText(Path.Combine(shared, "1", "floor_1.png"), "site 2, building 1");
        var cleanup = await CleanupAsync(main: null);

        cleanup.RemoveDirectory("2", "floor plan", root, shared, subdirectoriesOnly: true).Should().BeTrue();

        File.Exists(Path.Combine(shared, "floor_1.png")).Should().BeTrue();
        Directory.Exists(Path.Combine(shared, "1")).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveDirectory_SubdirectoriesOnly_NothingElseInIt_RemovesTheDirectory()
    {
        var root = Path.Combine(_dir, "floor-plans");
        var site = Path.Combine(root, "2");
        Directory.CreateDirectory(Path.Combine(site, "1"));
        File.WriteAllText(Path.Combine(site, "1", "floor_1.png"), "site 2, building 1");
        var cleanup = await CleanupAsync(main: null);

        cleanup.RemoveDirectory("2", "floor plan", root, site, subdirectoriesOnly: true).Should().BeTrue();

        Directory.Exists(site).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveDirectory_RootOrOutsideRoot_IsRefused()
    {
        var root = Path.Combine(_dir, "report_pdfs");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(outside);
        var cleanup = await CleanupAsync(main: null);

        cleanup.RemoveDirectory("x", "report PDF", root, root).Should().BeFalse();
        cleanup.RemoveDirectory("x", "report PDF", root, Path.Combine(root, "..", "elsewhere")).Should().BeFalse();

        Directory.Exists(root).Should().BeTrue();
        Directory.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public void SiteDirectories_ComeFromTheServicesOwnHelpers()
    {
        FloorPlanService.GetFloorPlanDirectory("site-a", isDefault: false)
            .Should().Be(Path.Combine(FloorPlanService.GetFloorPlanDirectory("site-a", isDefault: true), "site-a"));
        PdfStorageService.GetSitePdfDirectory("site-a", isDefault: false)
            .Should().Be(Path.Combine(PdfStorageService.GetPdfDirectory(), "site-a"));
    }

    // --- The provisioning delete ---

    private sealed class FakeInflux : HttpMessageHandler
    {
        public Dictionary<string, string> Buckets { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/api/v2/buckets")
            {
                var name = Uri.UnescapeDataString(uri.Query.Split('&').First(p => p.Contains("name=")).Split('=')[1]);
                var json = Buckets.TryGetValue(name, out var id)
                    ? $"{{\"buckets\":[{{\"id\":\"{id}\",\"name\":\"{name}\"}}]}}"
                    : "{\"buckets\":[]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (request.Method == HttpMethod.Delete && uri.AbsolutePath.StartsWith("/api/v2/buckets/", StringComparison.Ordinal))
            {
                Deleted.Add(uri.AbsolutePath["/api/v2/buckets/".Length..]);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task DeleteBucketsAsync_DeletesExistingAndSkipsMissing()
    {
        var influx = new FakeInflux();
        influx.Buckets["site-a-network_monitoring"] = "id-1";
        var service = new InfluxDbProvisioningService(new FakeHttpFactory(influx),
            NullLogger<InfluxDbProvisioningService>.Instance, new AuditContext());

        var deleted = await service.DeleteBucketsAsync("http://influx.example:8086/", "token", "org-1",
            new[] { "site-a-network_monitoring", "site-a-network_monitoring_longterm" });

        deleted.Should().Equal("site-a-network_monitoring");
        influx.Deleted.Should().Equal("id-1");
    }
}

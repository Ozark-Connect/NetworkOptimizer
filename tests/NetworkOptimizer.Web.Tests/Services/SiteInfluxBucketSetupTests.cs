using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// AP Telemetry's silent bucket step: it creates a site's buckets only from a shared token that can
/// already create them, never touches the token, and never throws.
/// </summary>
public class SiteInfluxBucketSetupTests : IAsyncLifetime
{
    private const string Site = "branch";
    private const string OrgId = "test-org-id";
    private const string SharedToken = "shared-token";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("no-site-buckets-");
    private readonly Mock<ISiteInfluxProvisioningService> _provisioner = new();
    private WebApplication _server = null!;
    private SiteDatabasePaths _paths = null!;
    private SiteDbContextFactory _siteDbFactory = null!;
    private TestDbFactory _mainDbFactory = null!;
    private ServiceProvider _services = null!;
    private MonitoringInfluxRegistry _registry = null!;
    private volatile bool _bucketsExist;
    private volatile bool _influxDown;

    private sealed class TestDbFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly DbContextOptions<NetworkOptimizerDbContext> _options;
        public TestDbFactory(DbContextOptions<NetworkOptimizerDbContext> options) => _options = options;
        public NetworkOptimizerDbContext CreateDbContext() => new(_options);
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _server = builder.Build();
        _server.MapGet("/ping", (HttpContext context) =>
            context.Response.StatusCode = _influxDown ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status204NoContent);
        _server.MapPost("/api/v2/query", async (HttpContext context) =>
        {
            if (_influxDown)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { code = "internal error", message = "test outage" });
                return;
            }
            if (!_bucketsExist)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { code = "not found", message = "failed to initialize execute state: could not find bucket" });
                return;
            }
            context.Response.ContentType = "text/csv";
            await context.Response.WriteAsync("");
        });
        await _server.StartAsync();

        _paths = new SiteDatabasePaths(Path.Combine(_directory.FullName, "network_optimizer.db"));
        _siteDbFactory = new SiteDbContextFactory(_paths);
        _mainDbFactory = new TestDbFactory(new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite($"Data Source={_paths.MainDbPath}").Options);
        Directory.CreateDirectory(_paths.GetSiteDataDir(Site));
        foreach (var site in new[] { "main", Site })
        {
            await using var db = _siteDbFactory.CreateForSite(site);
            await db.Database.EnsureCreatedAsync();
        }

        var credentials = new Mock<ICredentialProtectionService>();
        credentials.Setup(c => c.Decrypt(It.IsAny<string>())).Returns((string token) => token);
        _services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<NetworkOptimizerDbContext>>(_mainDbFactory)
            .AddSingleton(_siteDbFactory)
            .AddSingleton(credentials.Object)
            .AddSingleton<ILogger<MonitoringInfluxClient>>(NullLogger<MonitoringInfluxClient>.Instance)
            .BuildServiceProvider();
        _registry = new MonitoringInfluxRegistry(_services, NullLogger<MonitoringInfluxRegistry>.Instance);

        _provisioner.Setup(p => p.TryResolveOrgIdAsync(It.IsAny<string>(), SharedToken, "test-org", It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrgId);
        _provisioner.Setup(p => p.EnsureBucketsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => _bucketsExist = true)
            .ReturnsAsync(new InfluxDbProvisioningService.BucketProvisionResult
            {
                PrimaryBucketId = "primary-id",
                PrimaryBucketName = "branch-metrics",
                LongtermBucketId = "longterm-id",
                LongtermBucketName = "branch-history",
                CreatedBucketNames = ["branch-metrics", "branch-history"]
            });
    }

    public async Task DisposeAsync()
    {
        await _registry.DisposeAsync();
        await _services.DisposeAsync();
        await _server.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _directory.Delete(recursive: true);
    }

    private async Task SetSharedConnectionAsync(string? token = SharedToken)
    {
        await using var db = _siteDbFactory.CreateForSite("main", isDefault: true);
        db.MonitoringSettings.Add(new MonitoringSettings
        {
            InfluxDbUrl = _server.Urls.Single(),
            InfluxDbToken = token,
            InfluxDbOrg = "test-org",
            InfluxDbBucket = "metrics",
            InfluxDbLongtermBucket = "history"
        });
        await db.SaveChangesAsync();
    }

    private SiteInfluxBucketSetup SetupFor(string slug)
    {
        var context = new SiteContextService(new HttpContextAccessor(), _paths);
        context.OverrideSite(slug);
        return new SiteInfluxBucketSetup(context, _mainDbFactory, _services.GetRequiredService<ICredentialProtectionService>(),
            _provisioner.Object, _registry, NullLogger<SiteInfluxBucketSetup>.Instance);
    }

    private void VerifyNoBucketsCreated() =>
        _provisioner.Verify(p => p.EnsureBucketsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

    private async Task<string?> StoredSharedTokenAsync()
    {
        await using var db = _siteDbFactory.CreateForSite("main", isDefault: true);
        return (await db.MonitoringSettings.AsNoTracking().SingleAsync()).InfluxDbToken;
    }

    [Fact]
    public async Task Missing_buckets_are_created_with_the_names_the_site_client_uses()
    {
        await SetSharedConnectionAsync();

        (await SetupFor(Site).EnsureAsync()).Should().BeTrue();

        _provisioner.Verify(p => p.EnsureBucketsAsync(It.IsAny<string>(), SharedToken, OrgId,
            "branch-metrics", "branch-history", It.IsAny<CancellationToken>()), Times.Once);
        (await StoredSharedTokenAsync()).Should().Be(SharedToken);
    }

    [Fact]
    public async Task Buckets_that_already_answer_are_left_alone()
    {
        await SetSharedConnectionAsync();
        _bucketsExist = true;

        (await SetupFor(Site).EnsureAsync()).Should().BeTrue();

        _provisioner.Verify(p => p.TryResolveOrgIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoBucketsCreated();
    }

    [Fact]
    public async Task No_shared_connection_creates_nothing()
    {
        (await SetupFor(Site).EnsureAsync()).Should().BeFalse();

        _provisioner.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_that_cannot_create_buckets_is_never_changed_or_used_to_create()
    {
        await SetSharedConnectionAsync(token: "two-bucket-token");

        (await SetupFor(Site).EnsureAsync()).Should().BeFalse();

        VerifyNoBucketsCreated();
        (await StoredSharedTokenAsync()).Should().Be("two-bucket-token");
    }

    [Fact]
    public async Task The_main_site_is_left_to_the_wizard()
    {
        await SetSharedConnectionAsync();

        (await SetupFor("main").EnsureAsync()).Should().BeFalse();

        _provisioner.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provisioning_failure_reports_not_ready_and_does_not_throw()
    {
        await SetSharedConnectionAsync();
        _provisioner.Setup(p => p.EnsureBucketsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("not a Site Admin"));

        (await SetupFor(Site).EnsureAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task An_unreachable_InfluxDB_reports_not_ready_and_does_not_throw()
    {
        await SetSharedConnectionAsync();
        _influxDown = true;

        var setup = SetupFor(Site);
        (await setup.IsReadyAsync()).Should().BeFalse();
        (await setup.EnsureAsync()).Should().BeFalse();
    }
}

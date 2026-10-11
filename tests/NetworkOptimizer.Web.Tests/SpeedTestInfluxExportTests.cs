using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Licensing;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class SpeedTestInfluxExportTests : IAsyncLifetime
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("no-speed-export-");
    private readonly ConcurrentQueue<(string Bucket, string Body)> _writes = new();
    private readonly Mock<ILogger<MonitoringInfluxClient>> _influxLogger = new();
    private readonly Mock<ILogger<ClientSpeedTestService>> _clientLogger = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _clientEnrichment = new();
    private SiteDbContextFactory _siteDbFactory = null!;
    private TestDbFactory _mainDbFactory = null!;
    private ServiceProvider _services = null!;
    private MonitoringInfluxRegistry _registry = null!;
    private WebApplication _server = null!;
    private int _writeStatus = StatusCodes.Status204NoContent;
    private int _bucketReads;
    private int _bucketStatus = StatusCodes.Status200OK;
    private string _bucketId = "history-id";
    private long _retentionSeconds;
    private Func<HttpContext, Task>? _beforeWrite;

    public async Task InitializeAsync()
    {
        _clientLogger.Setup(log => log.Log(
                It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var state = invocation.Arguments[2];
                var message = state.ToString()!;
                if (!message.StartsWith("Background enrichment complete") && !message.StartsWith("Failed to enrich result"))
                    return;
                var values = (IEnumerable<KeyValuePair<string, object?>>)state;
                var id = (int)values.Single(value => value.Key == "Id").Value!;
                var completion = _clientEnrichment.GetOrAdd(id,
                    _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                if (invocation.Arguments[3] is Exception error)
                    completion.TrySetException(error);
                else
                    completion.TrySetResult();
            }));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _server = builder.Build();
        _server.MapPost("/api/v2/write", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            _writes.Enqueue((context.Request.Query["bucket"].ToString(), await reader.ReadToEndAsync()));
            if (_beforeWrite != null) await _beforeWrite(context);
            context.Response.StatusCode = _writeStatus;
            if (_writeStatus != StatusCodes.Status204NoContent)
                await context.Response.WriteAsJsonAsync(new { code = "unprocessable entity", message = "test write failure" });
        });
        _server.MapGet("/api/v2/buckets", async (HttpContext context) =>
        {
            Interlocked.Increment(ref _bucketReads);
            context.Response.StatusCode = _bucketStatus;
            await context.Response.WriteAsJsonAsync(new
            {
                buckets = new[]
                {
                    new
                    {
                        id = _bucketId, name = context.Request.Query["name"].ToString(), orgID = "test-org-id",
                        retentionRules = new[] { new { type = "expire", everySeconds = _retentionSeconds } }
                    }
                }
            });
        });
        await _server.StartAsync();

        var paths = new SiteDatabasePaths(Path.Combine(_directory.FullName, "network_optimizer.db"));
        _siteDbFactory = new SiteDbContextFactory(paths);
        _mainDbFactory = new TestDbFactory(new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite($"Data Source={paths.MainDbPath}").Options);
        Directory.CreateDirectory(paths.GetSiteDataDir("branch"));

        foreach (var site in new[] { "main", "branch" })
        {
            await using var db = _siteDbFactory.CreateForSite(site);
            await db.Database.EnsureCreatedAsync();
            db.MonitoringSettings.Add(new MonitoringSettings
            {
                Enabled = false,
                InfluxDbUrl = site == "main" ? _server.Urls.Single() : "http://unused.invalid",
                InfluxDbToken = site == "main" ? "test-token" : null,
                InfluxDbOrg = "test-org",
                // Deliberately collide: the client must resolve secondary-site bucket names safely.
                InfluxDbBucket = "metrics",
                InfluxDbLongtermBucket = "history"
            });
            await db.SaveChangesAsync();
        }

        var credentials = new Mock<ICredentialProtectionService>();
        credentials.Setup(protection => protection.Decrypt(It.IsAny<string>()))
            .Returns((string token) => token);
        _services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<NetworkOptimizerDbContext>>(_mainDbFactory)
            .AddSingleton(_siteDbFactory)
            .AddSingleton(credentials.Object)
            .AddSingleton(_influxLogger.Object)
            .BuildServiceProvider();
        _registry = new MonitoringInfluxRegistry(_services, NullLogger<MonitoringInfluxRegistry>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _registry.DisposeAsync();
        await _services.DisposeAsync();
        await _server.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _directory.Delete(recursive: true);
    }

    [Theory]
    [InlineData("main", "history")]
    [InlineData("branch", "branch-history")]
    public async Task Completion_SavesThenExportsToOriginatingSitesLongtermBucket(string site, string bucket)
    {
        var expected = Result();
        var service = Service(site, expected);

        var result = await service.RunTestAsync();
        await _registry.GetFor(site).FlushAsync();

        result.Should().BeSameAs(expected);
        result!.Id.Should().BeGreaterThan(0);
        await using var db = _siteDbFactory.CreateForSite(site);
        var saved = await db.Iperf3Results.SingleAsync();
        saved.Id.Should().Be(result.Id);
        saved.TestTime.Should().Be(expected.TestTime);
        var write = _writes.Should().ContainSingle().Subject;
        write.Bucket.Should().Be(bucket);
        write.Body.Trim().Should().Contain("speed_test,")
            .And.Contain($"result_id={saved.Id}i").And.Contain("success=true");
        await using var otherSite = _siteDbFactory.CreateForSite(site == "main" ? "branch" : "main");
        (await otherSite.Iperf3Results.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PersistedFailure_ExportsFailureStatus()
    {
        var service = Service("main", Result(), failTest: true);

        var result = await service.RunTestAsync();
        await _registry.GetDefault().FlushAsync();

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        await using var db = _mainDbFactory.CreateDbContext();
        (await db.Iperf3Results.SingleAsync()).Success.Should().BeFalse();
        _writes.Should().ContainSingle().Subject.Body.Should().Contain("success=false");
    }

    [Fact]
    public async Task NoInfluxConfiguration_LeavesSuccessfulSqliteResultAndDoesNotWrite()
    {
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            (await db.MonitoringSettings.SingleAsync()).InfluxDbToken = null;
            await db.SaveChangesAsync();
        }

        var result = await Service("main", Result()).RunTestAsync();
        await _registry.GetDefault().FlushAsync();

        result!.Success.Should().BeTrue();
        await using var savedDb = _mainDbFactory.CreateDbContext();
        (await savedDb.Iperf3Results.SingleAsync()).Success.Should().BeTrue();
        _registry.GetDefault().IsConfigured.Should().BeFalse();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingLongtermBucket_UsesExistingPrimaryBucketFallback()
    {
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            (await db.MonitoringSettings.SingleAsync()).InfluxDbLongtermBucket = "";
            await db.SaveChangesAsync();
        }

        await Service("main", Result()).RunTestAsync();
        await _registry.GetDefault().FlushAsync();

        _writes.Should().ContainSingle().Subject.Bucket.Should().Be("metrics");
    }

    [Fact]
    public async Task InfluxWriteFailure_DoesNotChangeTestOutcomeOrSavedResult()
    {
        _writeStatus = StatusCodes.Status503ServiceUnavailable;
        var expected = Result();

        var result = await Service("main", expected).RunTestAsync();
        await _registry.GetDefault().FlushAsync();

        result.Should().BeSameAs(expected);
        result!.Success.Should().BeTrue();
        await using var db = _mainDbFactory.CreateDbContext();
        var saved = await db.Iperf3Results.SingleAsync();
        saved.Success.Should().BeTrue();
        saved.DownloadBitsPerSecond.Should().Be(expected.DownloadBitsPerSecond);
        VerifyLog(_influxLogger, LogLevel.Error, "points dropped");
    }

    [Fact]
    public async Task ExportInitializationFailure_IsLoggedWithoutFailingSavedTest()
    {
        await using var emptyServices = new ServiceCollection().BuildServiceProvider();
        await using var registry = new MonitoringInfluxRegistry(emptyServices, NullLogger<MonitoringInfluxRegistry>.Instance);
        var logger = new Mock<ILogger>();
        var expected = Result();

        var result = await Service("main", expected, registry: registry, logger: logger.Object).RunTestAsync();

        result.Should().BeSameAs(expected);
        result!.Success.Should().BeTrue();
        await using var db = _mainDbFactory.CreateDbContext();
        (await db.Iperf3Results.SingleAsync()).Success.Should().BeTrue();
        VerifyLog(logger, LogLevel.Warning, "Failed to export speed test result");
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task SqliteSaveFailure_DoesNotExport()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_directory.FullName, "network_optimizer.db")}").Options;
        var dbFactory = new Mock<IDbContextFactory<NetworkOptimizerDbContext>>();
        dbFactory.Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new FailingSaveDbContext(options));
        var service = new TestWanService(dbFactory.Object, _siteDbFactory, _registry, "main", Result());

        var result = await service.RunTestAsync();

        result.Should().BeNull();
        await using var db = _mainDbFactory.CreateDbContext();
        (await db.Iperf3Results.CountAsync()).Should().Be(0);
        _registry.GetDefault().IsConfigured.Should().BeFalse();
        _writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("main", true, false)]
    [InlineData("branch", true, false)]
    [InlineData("main", false, false)]
    [InlineData("branch", false, false)]
    [InlineData("main", true, true)]
    [InlineData("branch", true, true)]
    [InlineData("main", false, true)]
    [InlineData("branch", false, true)]
    public async Task GatewayWanCompletion_ExportsSavedRunsButNotEphemeralRuns(string site, bool success, bool ephemeral)
    {
        await using var services = ProductionServices();
        var service = ActivatorUtilities.CreateInstance<GatewayWanSpeedTestService>(services, site);
        GetField(service, "_influxRegistry").Should().BeSameAs(_registry);
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.GetSettingsAsync(It.IsAny<bool>())).ReturnsAsync(new GatewaySshSettings
        {
            Host = "192.0.2.1", Password = "test-password", Enabled = true
        });
        ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, ""));
        ssh.Setup(s => s.RunCommandAsync(
                It.Is<string>(command => command.Contains("-streams")),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((success, success
                ? """{"success":true,"download":{"bps":500000000},"upload":{"bps":50000000},"streams":20,"duration_seconds":8}"""
                : """{"success":false,"error":"Test failed"}"""));
        SetField(service, "_gatewaySsh", ssh.Object);
        var analysisComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnPathAnalysisComplete += _ => analysisComplete.TrySetResult();

        var result = await service.RunTestAsync("eth1", "WAN2", "Backup",
            options: new GatewayWanTestOptions { Ephemeral = ephemeral });
        if (success && !ephemeral)
            await analysisComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _registry.GetFor(site).FlushAsync();

        result.Should().NotBeNull();
        result!.Success.Should().Be(success);
        await using var db = _siteDbFactory.CreateForSite(site);
        if (ephemeral)
        {
            result.Id.Should().Be(0);
            (await db.Iperf3Results.CountAsync()).Should().Be(0);
            _writes.Should().BeEmpty();
        }
        else
        {
            var saved = await db.Iperf3Results.SingleAsync();
            saved.Id.Should().Be(result.Id);
            saved.Success.Should().Be(success);
            saved.WanNetworkGroup.Should().Be("WAN2");
            var write = _writes.Should().ContainSingle().Subject;
            write.Bucket.Should().Be(site == "main" ? "history" : "branch-history");
            write.Body.Should().Contain("runner=gateway").And.Contain("provider=uwn").And.Contain("wan_network_group=WAN2")
                .And.Contain($"result_id={saved.Id}i").And.Contain($"success={success.ToString().ToLowerInvariant()}");
        }
    }

    [Theory]
    [InlineData("main", false)]
    [InlineData("branch", false)]
    [InlineData("main", true)]
    [InlineData("branch", true)]
    public async Task LanSavePaths_ExportPersistedResultThroughInjectedRegistry(string site, bool gateway)
    {
        await using var services = ProductionServices();
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(site);
        object service;
        if (gateway)
        {
            service = ActivatorUtilities.CreateInstance<GatewaySpeedTestService>(scope.ServiceProvider);
            var result = new GatewaySpeedTestResult
            {
                GatewayHost = "192.0.2.1", Success = true,
                DownloadBitsPerSecond = 800_000_000, UploadBitsPerSecond = 700_000_000,
                TestTime = Result().TestTime
            };
            await InvokeAsync(service, "SaveResultToHistoryAsync", result, null);
        }
        else
        {
            service = ActivatorUtilities.CreateInstance<Iperf3SpeedTestService>(services, site);
            var result = Result();
            result.Direction = SpeedTestDirection.ServerToDevice;
            result.DeviceHost = "192.0.2.10";
            await InvokeAsync(service, "SaveResultAsync", result);
        }
        GetField(service, "_influxRegistry").Should().BeSameAs(_registry);
        await _registry.GetFor(site).FlushAsync();

        await using var db = _siteDbFactory.CreateForSite(site);
        var saved = await db.Iperf3Results.SingleAsync();
        saved.Direction.Should().Be(SpeedTestDirection.ServerToDevice);
        saved.Success.Should().BeTrue();
        var write = _writes.Should().ContainSingle().Subject;
        write.Bucket.Should().Be(site == "main" ? "history" : "branch-history");
        write.Body.Should().Contain("test_type=lan").And.Contain($"target_host={saved.DeviceHost}")
            .And.Contain($"result_id={saved.Id}i");
        await using var otherSite = _siteDbFactory.CreateForSite(site == "main" ? "branch" : "main");
        (await otherSite.Iperf3Results.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task WanProductionConstruction_ForwardsRegistryToBaseClass()
    {
        await using var services = ProductionServices();
        var uwn = ActivatorUtilities.CreateInstance<UwnSpeedTestService>(services, "branch");
        var cloudflare = ActivatorUtilities.CreateInstance<CloudflareSpeedTestService>(services);
        var registryField = typeof(WanSpeedTestServiceBase)
            .GetField("_influxRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!;

        registryField.GetValue(uwn).Should().BeSameAs(_registry);
        registryField.GetValue(cloudflare).Should().BeSameAs(_registry);
    }

    [Theory]
    [InlineData("main", false)]
    [InlineData("branch", false)]
    [InlineData("main", true)]
    [InlineData("branch", true)]
    public async Task LanSaveFailure_DoesNotExport(string site, bool gateway)
    {
        await using var services = ProductionServices();
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(site);
        await using (var db = _siteDbFactory.CreateForSite(site))
            await db.Database.ExecuteSqlRawAsync("DROP TABLE Iperf3Results");

        if (gateway)
        {
            var service = ActivatorUtilities.CreateInstance<GatewaySpeedTestService>(scope.ServiceProvider);
            await InvokeAsync(service, "SaveResultToHistoryAsync",
                new GatewaySpeedTestResult { GatewayHost = "192.0.2.1", Success = true }, null);
        }
        else
        {
            var service = ActivatorUtilities.CreateInstance<Iperf3SpeedTestService>(services, site);
            var result = Result();
            result.Direction = SpeedTestDirection.ServerToDevice;
            await InvokeAsync(service, "SaveResultAsync", result);
        }

        _registry.GetFor(site).IsConfigured.Should().BeFalse();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanShutdown_DropsUnflushedExportButKeepsSqliteResult()
    {
        var result = await Service("main", Result()).RunTestAsync();

        await _registry.DisposeAsync();

        result!.Success.Should().BeTrue();
        await using var db = _mainDbFactory.CreateDbContext();
        (await db.Iperf3Results.SingleAsync()).Id.Should().Be(result.Id);
        _writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("main", false)]
    [InlineData("branch", false)]
    [InlineData("main", true)]
    [InlineData("branch", true)]
    public async Task BrowserCompletion_ExportsLanOrWanWithCorrectRatesAndIdentity(string site, bool wan)
    {
        await using var services = ProductionServices();
        var service = ActivatorUtilities.CreateInstance<ClientSpeedTestService>(services, site);

        var result = await service.RecordOpenSpeedTestResultAsync("192.0.2.50",
            downloadMbps: 500, uploadMbps: 50, pingMs: 12.5, jitterMs: 0.5,
            downloadDataMb: 100, uploadDataMb: 10, userAgent: "private-user-agent",
            latitude: 42.123, longitude: -71.123, externalServerId: wan ? "vps-test" : null);
        await _registry.GetFor(site).FlushAsync();
        await WaitForClientEnrichmentAsync(result.Id);

        await using var db = _siteDbFactory.CreateForSite(site);
        var saved = await db.Iperf3Results.SingleAsync();
        saved.Direction.Should().Be(wan ? SpeedTestDirection.OpenSpeedTestWan : SpeedTestDirection.BrowserToServer);
        saved.DownloadBitsPerSecond.Should().Be(wan ? 500_000_000 : 50_000_000);
        saved.UploadBitsPerSecond.Should().Be(wan ? 50_000_000 : 500_000_000);
        var write = _writes.Should().ContainSingle().Subject;
        write.Bucket.Should().Be(site == "main" ? "history" : "branch-history");
        write.Body.Should().Contain($"result_id={saved.Id}i").And.Contain("ping_ms=12.5").And.Contain("jitter_ms=0.5")
            .And.Contain($"download_bps={saved.DownloadBitsPerSecond}")
            .And.Contain($"upload_bps={saved.UploadBitsPerSecond}")
            .And.NotContain("private-user-agent").And.NotContain("42.123").And.NotContain("-71.123");
        if (wan)
        {
            write.Body.Should().Contain("test_type=wan").And.Contain("runner=client").And.Contain("provider=openspeedtest")
                .And.Contain("server_name=\"vps-test\"").And.NotContain("wan_network_group")
                .And.NotContain("server_host").And.NotContain("target_host").And.NotContain("192.0.2.50");
        }
        else
        {
            write.Body.Should().Contain("test_type=lan").And.Contain("runner=client").And.Contain("provider=openspeedtest")
                .And.Contain("target_host=192.0.2.50").And.NotContain("server_name");
        }
        await _registry.GetFor(site).FlushAsync();
        _writes.Should().ContainSingle("background enrichment should not export another summary");
        await using var otherSite = _siteDbFactory.CreateForSite(site == "main" ? "branch" : "main");
        (await otherSite.Iperf3Results.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("branch", true)]
    [InlineData("main", false)]
    [InlineData("branch", false)]
    public async Task ClientIperfMerge_RewritesSamePointWithBothRates(string site, bool downloadFirst)
    {
        await using var services = ProductionServices();
        var service = ActivatorUtilities.CreateInstance<ClientSpeedTestService>(services, site);
        var first = await RecordClientIperfAsync(service, downloadFirst ? 500_000_000 : 0,
            downloadFirst ? 0 : 50_000_000, streams: 3);
        await _registry.GetFor(site).FlushAsync();
        await WaitForClientEnrichmentAsync(first.Id);
        var originalTime = first.TestTime;
        var firstWrite = _writes.Should().ContainSingle().Subject;
        firstWrite.Body.Should().Contain("test_type=lan").And.Contain("runner=client").And.Contain("provider=iperf3")
            .And.Contain("target_host=192.0.2.50")
            .And.Contain(downloadFirst ? "upload_bps=0" : "download_bps=0");

        var merged = await RecordClientIperfAsync(service, downloadFirst ? 0 : 500_000_000,
            downloadFirst ? 50_000_000 : 0, streams: 6);
        await _registry.GetFor(site).FlushAsync();

        merged.Id.Should().Be(first.Id);
        merged.TestTime.Should().Be(originalTime);
        await using var db = _siteDbFactory.CreateForSite(site);
        var saved = await db.Iperf3Results.SingleAsync();
        saved.DownloadBitsPerSecond.Should().Be(500_000_000);
        saved.UploadBitsPerSecond.Should().Be(50_000_000);
        saved.ParallelStreams.Should().Be(6);
        var writes = _writes.ToArray();
        writes.Should().HaveCount(2);
        writes.Select(write => write.Bucket).Should().OnlyContain(bucket => bucket == (site == "main" ? "history" : "branch-history"));
        var firstLine = firstWrite.Body.Trim();
        var mergedLine = writes[1].Body.Trim();
        // InfluxDB's point key is measurement + tags + timestamp, not its fields.
        mergedLine[..mergedLine.IndexOf(' ')].Should().Be(firstLine[..firstLine.IndexOf(' ')]);
        mergedLine[(mergedLine.LastIndexOf(' ') + 1)..].Should().Be(firstLine[(firstLine.LastIndexOf(' ') + 1)..]);
        mergedLine.Should().Contain("download_bps=500000000").And.Contain("upload_bps=50000000")
            .And.Contain("parallel_streams=6i").And.Contain($"result_id={first.Id}i")
            .And.NotContain("private-raw-json");
        await using var otherSite = _siteDbFactory.CreateForSite(site == "main" ? "branch" : "main");
        (await otherSite.Iperf3Results.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ClientIperfSameDirection_CreatesAndExportsSeparateTests()
    {
        await using var services = ProductionServices();
        var service = ActivatorUtilities.CreateInstance<ClientSpeedTestService>(services, "main");
        var first = await RecordClientIperfAsync(service, 500_000_000, 0);
        await _registry.GetDefault().FlushAsync();
        await WaitForClientEnrichmentAsync(first.Id);

        var second = await RecordClientIperfAsync(service, 400_000_000, 0);
        await _registry.GetDefault().FlushAsync();
        await WaitForClientEnrichmentAsync(second.Id);

        second.Id.Should().NotBe(first.Id);
        second.TestTime.Should().NotBe(first.TestTime);
        await using var db = _mainDbFactory.CreateDbContext();
        (await db.Iperf3Results.CountAsync()).Should().Be(2);
        var writes = _writes.ToArray();
        writes.Should().HaveCount(2);
        writes[0].Body.Should().Contain($"result_id={first.Id}i");
        writes[1].Body.Should().Contain($"result_id={second.Id}i");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientCompletion_WithoutInfluxConfigStillSaves(bool iperf)
    {
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            (await db.MonitoringSettings.SingleAsync()).InfluxDbToken = null;
            await db.SaveChangesAsync();
        }
        await using var services = ProductionServices();
        var service = ActivatorUtilities.CreateInstance<ClientSpeedTestService>(services, "main");
        var result = iperf
            ? await RecordClientIperfAsync(service, 500_000_000, 0)
            : await service.RecordOpenSpeedTestResultAsync("192.0.2.50", 500, 50, 12.5, 0.5, 100, 10, null);
        await WaitForClientEnrichmentAsync(result.Id);
        await _registry.GetDefault().FlushAsync();

        result.Success.Should().BeTrue();
        await using var savedDb = _mainDbFactory.CreateDbContext();
        (await savedDb.Iperf3Results.SingleAsync()).Id.Should().Be(result.Id);
        _registry.GetDefault().IsConfigured.Should().BeFalse();
        _writes.Should().BeEmpty();
    }

    private Task WaitForClientEnrichmentAsync(int id) =>
        _clientEnrichment.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .Task.WaitAsync(TimeSpan.FromSeconds(10));

    private static Task<Iperf3Result> RecordClientIperfAsync(ClientSpeedTestService service,
        double downloadBps, double uploadBps, int streams = 3) =>
        service.RecordIperf3ClientResultAsync("192.0.2.50", downloadBps, uploadBps,
            100, 10, 0, 0, 8, streams, "private-raw-json", serverLocalIp: "192.0.2.1");

    [Theory]
    [InlineData("main", SpeedTestDirection.BrowserToServer)]
    [InlineData("branch", SpeedTestDirection.OpenSpeedTestWan)]
    [InlineData("branch", SpeedTestDirection.ClientToServer)]
    public async Task ClientEnrichment_UpdatesMetadataOnOriginalPoint(string site, SpeedTestDirection direction)
    {
        await using var services = ProductionServices(enrichedName: "Laptop");
        var service = ActivatorUtilities.CreateInstance<ClientSpeedTestService>(services, site);
        var result = direction == SpeedTestDirection.ClientToServer
            ? await RecordClientIperfAsync(service, 500_000_000, 0)
            : await service.RecordOpenSpeedTestResultAsync("192.0.2.50", 500, 50, 12.5, 0.5, 100, 10, null,
                externalServerId: direction == SpeedTestDirection.OpenSpeedTestWan ? "vps-test" : null);
        await _registry.GetFor(site).FlushAsync();
        await WaitForClientEnrichmentAsync(result.Id);
        await _registry.GetFor(site).FlushAsync();

        await using var db = _siteDbFactory.CreateForSite(site);
        (await db.Iperf3Results.SingleAsync()).DeviceName.Should().Be("Laptop");
        var writes = _writes.ToArray();
        if (direction == SpeedTestDirection.OpenSpeedTestWan)
        {
            writes.Should().ContainSingle().Which.Body.Should().Contain("server_name=\"vps-test\"").And.NotContain("Laptop");
            return;
        }
        writes.Should().HaveCount(2);
        writes.Select(write => write.Bucket).Should().OnlyContain(bucket =>
            bucket == (site == "main" ? "history" : "branch-history"));
        var summary = writes[0].Body.Trim();
        var metadata = writes[1].Body.Trim();
        metadata[..metadata.IndexOf(' ')].Should().Be(summary[..summary.IndexOf(' ')]);
        metadata[(metadata.LastIndexOf(' ') + 1)..].Should().Be(summary[(summary.LastIndexOf(' ') + 1)..]);
        metadata.Should().Contain("target_name=\"Laptop\"").And.NotContain("download_bps")
            .And.NotContain("upload_bps").And.NotContain("success=").And.NotContain("ping_ms");
    }

    [Fact]
    public async Task StaleEnrichmentMetadata_CannotOverwriteMergedRates()
    {
        await using var services = ProductionServices();
        var service = ActivatorUtilities.CreateInstance<ClientSpeedTestService>(services, "main");
        var first = await RecordClientIperfAsync(service, 500_000_000, 0);
        await WaitForClientEnrichmentAsync(first.Id);
        await _registry.GetDefault().FlushAsync();
        await RecordClientIperfAsync(service, 0, 50_000_000);
        await _registry.GetDefault().FlushAsync();

        // Simulate enrichment finishing with the original single-direction snapshot.
        first.DeviceName = "Laptop";
        first.UploadBitsPerSecond.Should().Be(0);
        await SpeedTestInfluxExporter.ExportMetadataAsync(_registry, "main", first, _clientLogger.Object);
        await _registry.GetDefault().FlushAsync();

        var writes = _writes.ToArray();
        writes.Should().HaveCount(3);
        writes[1].Body.Should().Contain("upload_bps=50000000");
        writes[2].Body.Should().Contain("target_name=\"Laptop\"")
            .And.NotContain("upload_bps").And.NotContain("download_bps").And.NotContain("success=");
        await using var db = _mainDbFactory.CreateDbContext();
        (await db.Iperf3Results.SingleAsync()).UploadBitsPerSecond.Should().Be(50_000_000);
    }

    private ServiceProvider ProductionServices(string? enrichedName = null,
        Dictionary<string, List<WanInterfaceInfo>>? activeWans = null, bool failWanLookup = false)
    {
        // Avoid the console connection's constructor-triggered network initialization, matching
        // ClientDashboardIdentificationTests. The real speed-test constructors still run.
        var connections = new SiteConnectionRegistry(null!);
        var connectionCache = (ConcurrentDictionary<string, UniFiConnectionService>)GetField(connections, "_connections")!;
        var pathAnalyzerMock = new Mock<INetworkPathAnalyzer>();
        pathAnalyzerMock.SetReturnsDefault(Task.FromResult(new NetworkPath()));
        var analysis = new PathAnalysisResult();
        if (enrichedName != null)
            analysis.Path.Hops.Add(new NetworkHop { Type = HopType.WirelessClient, DeviceName = enrichedName });
        pathAnalyzerMock.SetReturnsDefault(analysis);
        var pathAnalyzer = pathAnalyzerMock.Object;
        var speedTests = new SpeedTestServiceRegistry(null!, connections);
        var bundleCache = (ConcurrentDictionary<string, SpeedTestServiceRegistry.SiteSpeedTestServices>)GetField(speedTests, "_instances")!;
        foreach (var site in new[] { "main", "branch" })
        {
            connectionCache[site] = (UniFiConnectionService)RuntimeHelpers.GetUninitializedObject(typeof(UniFiConnectionService));
            SetField(connectionCache[site], "_logger", NullLogger<UniFiConnectionService>.Instance);
            bundleCache[site] = new(null!, null!, null!, null!, null!, null!);
        }
        var services = new ServiceCollection()
            .AddLogging()
            .AddScoped<NetworkOptimizer.Web.Services.Identity.ICallerContext, NetworkOptimizer.Web.Services.Identity.CallerContext>()
            .AddScoped<ISqmService>(sp =>
            {
                var sqm = new Mock<ISqmService>();
                sqm.Setup(s => s.GetWanInterfacesFromControllerAsync()).Returns(() =>
                {
                    sp.GetRequiredService<NetworkOptimizer.Web.Services.Identity.ICallerContext>().Require().IsSystem.Should().BeTrue();
                    if (failWanLookup) throw new InvalidOperationException("WAN lookup unavailable");
                    var slug = sp.GetRequiredService<SiteContextService>().Slug;
                    return Task.FromResult(activeWans != null && activeWans.TryGetValue(slug, out var wans)
                        ? wans : new List<WanInterfaceInfo>());
                });
                return sqm.Object;
            })
            .AddSingleton(_clientLogger.Object)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IDbContextFactory<NetworkOptimizerDbContext>>(_mainDbFactory)
            .AddSingleton(_siteDbFactory)
            .AddSingleton(new SiteDatabasePaths(Path.Combine(_directory.FullName, "network_optimizer.db")))
            .AddScoped(sp => new SiteContextService(new HttpContextAccessor(), sp.GetRequiredService<SiteDatabasePaths>()))
            .AddScoped(sp => _siteDbFactory.CreateForSite(sp.GetRequiredService<SiteContextService>().Slug))
            .AddScoped<ISpeedTestRepository, SpeedTestRepository>()
            .AddSingleton(_registry)
            .AddSingleton(connections)
            .AddSingleton(speedTests)
            .AddSingleton(Mock.Of<ICredentialProtectionService>())
            .AddSingleton(pathAnalyzer)
            .AddSingleton(Mock.Of<ITopologySnapshotService>())
            .AddSingleton<SshClientService>()
            .AddSingleton<GatewaySshRegistry>()
            .AddScoped<IGatewaySshService>(sp => sp.GetRequiredService<GatewaySshRegistry>()
                .GetFor(sp.GetRequiredService<SiteContextService>().Slug))
            .AddSingleton<UniFiSshRegistry>()
            .AddSingleton<SystemSettingsService>()
            .AddSingleton<SiteAgentCoverage>()
            .AddSingleton<SiteTunnelRouting>()
            .AddSingleton(sp => new AgentIperf3Service(null!, null!, sp.GetRequiredService<ILogger<AgentIperf3Service>>()))
            .AddSingleton(sp => new AgentEnrollmentService(_mainDbFactory, null!, null!,
                sp.GetRequiredService<SiteAgentCoverage>(), sp, sp.GetRequiredService<SiteTunnelRouting>(),
                sp.GetRequiredService<ILogger<AgentEnrollmentService>>()))
            .AddSingleton(sp => new LicenseStateService(_mainDbFactory, TimeProvider.System,
                sp.GetRequiredService<ILogger<LicenseStateService>>()))
            .AddSingleton(sp => new Iperf3ServerService(sp.GetRequiredService<ILogger<Iperf3ServerService>>(),
                speedTests, sp.GetRequiredService<IConfiguration>()))
            .AddSingleton(sp => new AgentUwnService(null!, sp.GetRequiredService<ILogger<AgentUwnService>>()))
            .AddSingleton(sp => new AgentWanTestVantageResolver(null!, null!, null!, connections, _siteDbFactory,
                _mainDbFactory, sp.GetRequiredService<ILogger<AgentWanTestVantageResolver>>()))
            .AddHttpClient()
            .AddMemoryCache()
            .BuildServiceProvider();
        // GatewaySpeedTestService needs a site's analyzer but not the rest of the cached bundle.
        foreach (var site in new[] { "main", "branch" })
        {
            var analyzer = new NetworkPathAnalyzer(connectionCache[site], services.GetRequiredService<IMemoryCache>(),
                services.GetRequiredService<ILoggerFactory>());
            bundleCache[site] = bundleCache[site] with { PathAnalyzer = analyzer };
        }
        return services;
    }

    [Theory]
    [InlineData("main", 0, true)]
    [InlineData("main", 1, true)]
    [InlineData("main", 2, true)]
    [InlineData("branch", 0, true)]
    [InlineData("branch", 1, true)]
    [InlineData("branch", 2, true)]
    [InlineData("main", 0, false)]
    [InlineData("main", 1, false)]
    [InlineData("main", 2, false)]
    [InlineData("branch", 0, false)]
    [InlineData("branch", 1, false)]
    [InlineData("branch", 2, false)]
    public async Task BrowserWan_UsesOnlyUnambiguousOriginatingSiteWan(string site, int wanCount, bool influxConfigured)
    {
        if (!influxConfigured)
        {
            await using var mainDb = _mainDbFactory.CreateDbContext();
            (await mainDb.MonitoringSettings.SingleAsync()).InfluxDbToken = null;
            await mainDb.SaveChangesAsync();
        }
        var activeWans = new Dictionary<string, List<WanInterfaceInfo>>
        {
            [site] = Enumerable.Range(1, wanCount).Select(i =>
                new WanInterfaceInfo { NetworkGroup = $"WAN{i}", Name = $"WAN {i}" }).ToList(),
            [site == "main" ? "branch" : "main"] = new() { new() { NetworkGroup = "OTHER-SITE" } }
        };
        await using var services = ProductionServices(enrichedName: "Laptop", activeWans: activeWans);
        var connections = services.GetRequiredService<SiteConnectionRegistry>();
        foreach (var slug in new[] { "main", "branch" })
        {
            var connection = connections.GetFor(slug);
            SetField(connection, "_client", RuntimeHelpers.GetUninitializedObject(typeof(UniFiApiClient)));
            SetField(connection, "_isConnected", true);
            SetField(connection, "_networkCacheTime", DateTime.UtcNow);
            var networks = new List<NetworkInfo>
            {
                new() { Purpose = "corporate", WanNetworkgroup = "not-a-wan" }
            };
            if (slug == site)
                networks.AddRange(Enumerable.Range(1, Math.Max(2, wanCount)).Select(i =>
                    new NetworkInfo { Purpose = "wan", WanNetworkgroup = $"WAN{i}", Name = $"WAN {i}" }));
            else
                networks.Add(new NetworkInfo { Purpose = "wan", WanNetworkgroup = "OTHER-SITE" });
            SetField(connection, "_cachedNetworks", networks);
        }
        var service = ActivatorUtilities.CreateInstance<ClientSpeedTestService>(services, site);

        var result = await service.RecordOpenSpeedTestResultAsync("192.0.2.50", 500, 50, 12.5, 0.5, 100, 10, null,
            externalServerId: "vps-test");
        await WaitForClientEnrichmentAsync(result.Id);
        await _registry.GetFor(site).FlushAsync();

        await using var db = _siteDbFactory.CreateForSite(site);
        var saved = await db.Iperf3Results.SingleAsync();
        saved.WanNetworkGroup.Should().Be(wanCount == 1 ? "WAN1" : null);
        if (!influxConfigured)
        {
            _writes.Should().BeEmpty();
            return;
        }
        var write = _writes.Should().ContainSingle().Subject;
        write.Bucket.Should().Be(site == "main" ? "history" : "branch-history");
        write.Body.Should().Contain("server_name=\"vps-test\"").And.NotContain("Laptop").And.NotContain("OTHER-SITE");
        if (wanCount == 1)
            write.Body.Should().Contain("wan_network_group=WAN1");
        else
            write.Body.Should().NotContain("wan_network_group");
    }

    [Fact]
    public async Task BrowserWan_NetworkLookupFailureDoesNotFailSavedTest()
    {
        await using var services = ProductionServices(failWanLookup: true);
        var connection = services.GetRequiredService<SiteConnectionRegistry>().GetDefault();
        // The fixture's uninitialized client cannot fetch networks; the saved test must still succeed.
        SetField(connection, "_client", RuntimeHelpers.GetUninitializedObject(typeof(UniFiApiClient)));
        SetField(connection, "_isConnected", true);
        var service = ActivatorUtilities.CreateInstance<ClientSpeedTestService>(services, "main");

        var result = await service.RecordOpenSpeedTestResultAsync("192.0.2.50", 500, 50, 12.5, 0.5, 100, 10, null,
            externalServerId: "vps-test");
        await WaitForClientEnrichmentAsync(result.Id);
        await _registry.GetDefault().FlushAsync();

        result.Success.Should().BeTrue();
        await using var db = _mainDbFactory.CreateDbContext();
        (await db.Iperf3Results.SingleAsync()).WanNetworkGroup.Should().BeNull();
        _writes.Should().ContainSingle().Which.Body.Should().Contain("server_name=\"vps-test\"")
            .And.NotContain("wan_network_group");
        VerifyLog(_clientLogger, LogLevel.Warning, "Could not identify the sole WAN");
    }

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static async Task InvokeAsync(object target, string name, params object?[] arguments) =>
        await (Task)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments)!;

    private TestWanService Service(string site, Iperf3Result result, bool failTest = false,
        MonitoringInfluxRegistry? registry = null, ILogger? logger = null) =>
        new(_mainDbFactory, _siteDbFactory, registry ?? _registry, site, result, failTest, logger);

    [Theory]
    [InlineData("main", "history")]
    [InlineData("branch", "branch-history")]
    public async Task Backfill_AllDirectionsUsesOriginatingBucketAndPersistsCompletion(string site, string bucket)
    {
        var results = Enum.GetValues<SpeedTestDirection>().Select(direction =>
        {
            var result = Result();
            result.Direction = direction;
            result.TestTime = DateTime.UtcNow.AddDays(-2).AddSeconds((int)direction);
            result.Success = direction != SpeedTestDirection.CloudflareWanGateway;
            result.DeviceName = "Historical target";
            result.ExternalServerName = "External WAN";
            return result;
        }).ToArray();
        await SaveHistoryAsync(site, results);

        (await BackfillAsync(site)).Should().BeTrue();

        var write = _writes.Should().ContainSingle().Subject;
        write.Bucket.Should().Be(bucket);
        var lines = write.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(results.Length);
        foreach (var result in results)
        {
            var line = lines.Single(line => line.Contains($"result_id={result.Id}i,"));
            line.Should().Contain("runner=").And.Contain("provider=").And.NotContain("direction=");
            if (!result.Success)
                line.Should().NotContain("server_name").And.NotContain("target_name");
            else if (result.Direction == SpeedTestDirection.OpenSpeedTestWan)
                line.Should().Contain("server_name=\"External WAN\"").And.NotContain("target_name");
            else if (result.Direction is SpeedTestDirection.CloudflareWan or SpeedTestDirection.CloudflareWanGateway
                or SpeedTestDirection.UwnWan or SpeedTestDirection.UwnWanGateway)
                line.Should().Contain("server_name=\"Historical target\"").And.NotContain("target_name");
            else
                line.Should().Contain("target_name=\"Historical target\"");
            line.Should().Contain($"success={result.Success.ToString().ToLowerInvariant()}");
            line.Should().EndWith(((result.TestTime.Ticks - DateTime.UnixEpoch.Ticks) * 100).ToString());
        }
        var marker = (await BackfillMarkersAsync(site)).Should().ContainSingle().Subject;
        marker.Key.Length.Should().BeLessThanOrEqualTo(100);
        marker.Value.Should().NotContain("test-token");

        await using var restarted = new MonitoringInfluxClient(_mainDbFactory,
            _services.GetRequiredService<ICredentialProtectionService>(), _influxLogger.Object,
            _siteDbFactory, site == "main" ? null : site);
        try
        {
            await restarted.ReconfigureAsync();
            (await restarted.BackfillSpeedTestResultsAsync()).Should().BeTrue();
            _writes.Should().ContainSingle();
        }
        finally { await restarted.DisposeOwnedAsync(); }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"RetentionSeconds\":\"invalid\"}")]
    public async Task Backfill_InvalidCompletionMarkerIsLoggedAndRepairedAfterAcknowledgedReplay(string invalidValue)
    {
        await SaveHistoryAsync("main", Result());
        (await BackfillAsync()).Should().BeTrue();
        var original = (await BackfillMarkersAsync()).Single();
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            (await db.SystemSettings.FindAsync(original.Key))!.Value = invalidValue;
            await db.SaveChangesAsync();
        }
        _writeStatus = StatusCodes.Status422UnprocessableEntity;

        await Assert.ThrowsAnyAsync<Exception>(() => BackfillAsync());

        VerifyLog(_influxLogger, LogLevel.Warning, "Invalid speed test InfluxDB backfill marker");
        (await BackfillMarkersAsync()).Single().Value.Should().Be(invalidValue);
        _writeStatus = StatusCodes.Status204NoContent;

        (await BackfillAsync()).Should().BeTrue();

        var repaired = (await BackfillMarkersAsync()).Should().ContainSingle().Subject;
        repaired.Key.Should().Be(original.Key);
        repaired.Value.Should().Contain("\"BucketId\":\"history-id\"").And.Contain("\"Exported\":1");
        _writes.Should().HaveCount(3);
        _writes.Select(write => write.Body).Distinct().Should().ContainSingle();

        (await BackfillAsync()).Should().BeTrue();
        _writes.Should().HaveCount(3);
    }

    [Fact]
    public async Task Backfill_EmptyHistoryDoesNotContactInfluxOrMarkComplete()
    {
        (await BackfillAsync()).Should().BeTrue();

        _bucketReads.Should().Be(0);
        _writes.Should().BeEmpty();
        (await BackfillMarkersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Backfill_FailedSecondBatchRemainsPendingAndReplaysAcknowledgedBatch()
    {
        await SaveHistoryAsync("main", Enumerable.Range(0, 501).Select(i =>
        {
            var result = Result();
            result.TestTime = DateTime.UtcNow.AddDays(-1).AddSeconds(i);
            return result;
        }).ToArray());
        _beforeWrite = _ =>
        {
            if (_writes.Count == 2) _writeStatus = StatusCodes.Status422UnprocessableEntity;
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<Exception>(() => BackfillAsync());
        (await BackfillMarkersAsync()).Should().BeEmpty();
        _writes.Select(w => w.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length)
            .Should().Equal(500, 1);
        var firstBatch = _writes.First().Body;
        _beforeWrite = null;
        _writeStatus = StatusCodes.Status204NoContent;

        (await BackfillAsync()).Should().BeTrue();

        _writes.Should().HaveCount(4);
        _writes.ElementAt(2).Body.Should().Be(firstBatch);
        (await BackfillMarkersAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Backfill_RespectsRetentionAndSkipsMalformedRows()
    {
        _retentionSeconds = 86400;
        var expired = Result();
        expired.TestTime = DateTime.UtcNow.AddDays(-2);
        var valid = Result();
        valid.TestTime = DateTime.UtcNow.AddHours(-1);
        var invalid = Result();
        invalid.TestTime = valid.TestTime;
        invalid.Direction = SpeedTestDirection.ClientToServer;
        invalid.DeviceHost = "";
        var unsupported = Result();
        unsupported.TestTime = valid.TestTime;
        unsupported.Direction = (SpeedTestDirection)99;
        var nonFinite = Result();
        nonFinite.TestTime = valid.TestTime;
        nonFinite.DownloadBitsPerSecond = double.PositiveInfinity;
        var invalidTimestamp = Result();
        invalidTimestamp.TestTime = DateTime.MaxValue;
        await SaveHistoryAsync("main", expired, valid, invalid, unsupported, nonFinite, invalidTimestamp);

        (await BackfillAsync()).Should().BeTrue();

        _writes.Should().ContainSingle().Which.Body.Should().Contain($"result_id={valid.Id}i,")
            .And.NotContain($"result_id={expired.Id}i,");
        var marker = (await BackfillMarkersAsync()).Single().Value;
        marker.Should().Contain("\"Exported\":1").And.Contain("\"Skipped\":5");
        VerifyLog(_influxLogger, LogLevel.Warning, $"historical speed test {invalid.Id}");
        VerifyLog(_influxLogger, LogLevel.Warning, $"historical speed test {unsupported.Id}");
        VerifyLog(_influxLogger, LogLevel.Warning, $"historical speed test {nonFinite.Id}");
        VerifyLog(_influxLogger, LogLevel.Warning, $"historical speed test {invalidTimestamp.Id}");
    }

    [Fact]
    public async Task Backfill_RetentionIncreaseAndRecreatedBucketReplayHistory()
    {
        var result = Result();
        result.TestTime = DateTime.UtcNow.AddDays(-2);
        await SaveHistoryAsync("main", result);
        _retentionSeconds = 86400;
        (await BackfillAsync()).Should().BeTrue();
        _writes.Should().BeEmpty();

        _retentionSeconds = 0;
        (await BackfillAsync()).Should().BeTrue();
        _writes.Should().ContainSingle();

        _bucketId = "recreated-history";
        (await BackfillAsync()).Should().BeTrue();
        _writes.Should().HaveCount(2);
        (await BackfillMarkersAsync()).Should().ContainSingle().Which.Value.Should().Contain(_bucketId);
    }

    [Fact]
    public async Task Backfill_ChangedDestinationGetsIndependentCompletionMarker()
    {
        await SaveHistoryAsync("main", Result());
        (await BackfillAsync()).Should().BeTrue();
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            (await db.MonitoringSettings.SingleAsync()).InfluxDbLongtermBucket = "new-history";
            await db.SaveChangesAsync();
        }

        (await BackfillAsync()).Should().BeTrue();

        _writes.Select(write => write.Bucket).Should().Equal("history", "new-history");
        (await BackfillMarkersAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Backfill_TokenRotationDoesNotReplayCompletedHistory()
    {
        await SaveHistoryAsync("main", Result());
        (await BackfillAsync()).Should().BeTrue();
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            (await db.MonitoringSettings.SingleAsync()).InfluxDbToken = "rotated-token";
            await db.SaveChangesAsync();
        }

        (await BackfillAsync()).Should().BeTrue();

        _writes.Should().ContainSingle();
        (await BackfillMarkersAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Backfill_RemovedAndRestoredSettingsRebuildClientWithoutReplayingHistory()
    {
        await SaveHistoryAsync("main", Result());
        (await BackfillAsync()).Should().BeTrue();
        MonitoringSettings settings;
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            settings = await db.MonitoringSettings.AsNoTracking().SingleAsync();
            await db.MonitoringSettings.ExecuteDeleteAsync();
        }
        var client = _registry.GetDefault();
        (await client.ReconfigureAsync()).Should().BeFalse();
        client.IsConfigured.Should().BeFalse();
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            db.MonitoringSettings.Add(settings);
            await db.SaveChangesAsync();
        }

        (await BackfillAsync()).Should().BeTrue();

        client.IsConfigured.Should().BeTrue();
        _writes.Should().ContainSingle();
        (await BackfillMarkersAsync()).Should().ContainSingle();
    }

    [Theory]
    [InlineData("main", "metrics")]
    [InlineData("branch", "branch-metrics")]
    public async Task Backfill_MissingLongtermBucketUsesResolvedPrimaryBucket(string site, string bucket)
    {
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            (await db.MonitoringSettings.SingleAsync()).InfluxDbLongtermBucket = "";
            await db.SaveChangesAsync();
        }
        await using (var db = _siteDbFactory.CreateForSite(site))
        {
            (await db.MonitoringSettings.SingleAsync()).InfluxDbLongtermBucket = "";
            await db.SaveChangesAsync();
        }
        await SaveHistoryAsync(site, Result());

        (await BackfillAsync(site)).Should().BeTrue();

        _writes.Should().ContainSingle().Which.Bucket.Should().Be(bucket);
    }

    [Fact]
    public async Task Backfill_MetadataFailureDoesNotMarkOrWriteHistory()
    {
        await SaveHistoryAsync("main", Result());
        _bucketStatus = StatusCodes.Status403Forbidden;

        await Assert.ThrowsAnyAsync<Exception>(() => BackfillAsync());

        _writes.Should().BeEmpty();
        (await BackfillMarkersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Backfill_SixThousandResultsAreWrittenInBoundedBatches()
    {
        await SaveHistoryAsync("main", Enumerable.Range(0, 6000).Select(i =>
        {
            var result = Result();
            result.TestTime = DateTime.UtcNow.AddDays(-7).AddSeconds(i);
            return result;
        }).ToArray());

        (await BackfillAsync()).Should().BeTrue();

        _writes.Should().HaveCount(12);
        _writes.Should().OnlyContain(write => write.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 500);
        (await BackfillMarkersAsync()).Single().Value.Should().Contain("\"Exported\":6000");
    }

    [Fact]
    public async Task Backfill_ExplicitReconfigurationCancelsPendingWriteWithoutMarkingOriginalDestinationComplete()
    {
        await SaveHistoryAsync("main", Result());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeWrite = async context =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        };
        var backfill = BackfillAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using (var db = _mainDbFactory.CreateDbContext())
            {
                (await db.MonitoringSettings.SingleAsync()).InfluxDbLongtermBucket = "new-history";
                await db.SaveChangesAsync();
            }
            (await _registry.GetDefault().ReconfigureAsync().WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            (await backfill.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
            (await BackfillMarkersAsync()).Should().BeEmpty();
        }
        finally
        {
            _beforeWrite = null;
        }
        _writes.Should().ContainSingle().Which.Bucket.Should().Be("history");
        (await _registry.GetDefault().BackfillSpeedTestResultsAsync()).Should().BeTrue();
        _writes.Last().Bucket.Should().Be("new-history");
        (await BackfillMarkersAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Backfill_LifecycleReconfigurationDoesNotCancelPendingWrite()
    {
        await SaveHistoryAsync("main", Result());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeWrite = async context =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
        };
        var backfill = BackfillAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<bool> reconfiguration;
        try
        {
            reconfiguration = _registry.GetDefault().ReconfigureAsync(refreshSpeedTestBackfill: false);
            reconfiguration.IsCompleted.Should().BeFalse();
            backfill.IsCompleted.Should().BeFalse();
        }
        finally
        {
            _beforeWrite = null;
            release.TrySetResult();
        }
        (await backfill.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        (await reconfiguration.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        (await BackfillMarkersAsync()).Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backfill_PendingExplicitRefreshPreventsNewPassAndAllowsRetryAfterwards(bool cancelRefresh)
    {
        await SaveHistoryAsync("main", Result());
        var client = _registry.GetDefault();
        await client.ReconfigureAsync();
        var gate = (SemaphoreSlim)GetField(client, "_flushSemaphore")!;
        using var cancellation = new CancellationTokenSource();
        await gate.WaitAsync();
        Task<bool> refresh;
        try
        {
            refresh = client.ReconfigureAsync(cancellation.Token);
            refresh.IsCompleted.Should().BeFalse();

            (await client.BackfillSpeedTestResultsAsync().WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
            (await BackfillMarkersAsync()).Should().BeEmpty();
            _writes.Should().BeEmpty();
            if (cancelRefresh) await cancellation.CancelAsync();
        }
        finally
        {
            gate.Release();
        }
        if (cancelRefresh)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        else
            (await refresh).Should().BeTrue();

        (await client.BackfillSpeedTestResultsAsync()).Should().BeTrue();
        (await BackfillMarkersAsync()).Should().ContainSingle();
    }

    [Theory]
    [InlineData(SpeedTestDirection.ServerToDevice)]
    [InlineData(SpeedTestDirection.ClientToServer)]
    [InlineData(SpeedTestDirection.BrowserToServer)]
    [InlineData(SpeedTestDirection.CloudflareWan)]
    [InlineData(SpeedTestDirection.CloudflareWanGateway)]
    [InlineData(SpeedTestDirection.UwnWan)]
    [InlineData(SpeedTestDirection.UwnWanGateway)]
    [InlineData(SpeedTestDirection.OpenSpeedTestWan)]
    public async Task Backfill_FullyPopulatedHistoryMatchesLiveExport(SpeedTestDirection direction)
    {
        var result = Result();
        result.Direction = direction;
        result.DeviceName = "Test target";
        result.DeviceType = "Gateway";
        result.WanName = "Test WAN";
        result.ExternalServerName = "Browser WAN server";
        result.DurationSeconds = 17;
        result.ParallelStreams = 4;
        result.PingMs = 3.5;
        result.JitterMs = 0.5;
        result.DownloadLatencyMs = 13.5;
        result.DownloadJitterMs = 1.5;
        result.UploadLatencyMs = 23.5;
        result.UploadJitterMs = 2.5;
        await SaveHistoryAsync("main", result);
        var client = _registry.GetDefault();
        await client.ReconfigureAsync();
        await client.WriteSpeedTestResultAsync(result);
        await client.FlushAsync();
        var live = _writes.Should().ContainSingle().Subject;

        (await client.BackfillSpeedTestResultsAsync()).Should().BeTrue();

        _writes.Should().HaveCount(2);
        _writes.Last().Should().Be(live);
    }

    [Fact]
    public async Task Backfill_ConcurrentMergeAndMetadataAreWrittenAfterHistoricalSnapshot()
    {
        var result = Result();
        result.Direction = SpeedTestDirection.ClientToServer;
        result.UploadBitsPerSecond = 0;
        await SaveHistoryAsync("main", result);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeWrite = async context =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
        };
        var backfill = BackfillAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using (var db = _mainDbFactory.CreateDbContext())
            {
                var merged = await db.Iperf3Results.SingleAsync();
                merged.UploadBitsPerSecond = 123_000_000;
                merged.DeviceName = "Enriched target";
                await db.SaveChangesAsync();
                await _registry.GetDefault().WriteSpeedTestResultAsync(merged);
                await _registry.GetDefault().WriteSpeedTestMetadataAsync(merged);
            }
            await _registry.GetDefault().FlushAsync();
            _writes.Should().ContainSingle();
            (await BackfillMarkersAsync()).Should().BeEmpty();
        }
        finally
        {
            _beforeWrite = null;
            release.TrySetResult();
        }
        (await backfill).Should().BeTrue();
        await _registry.GetDefault().FlushAsync();

        _writes.Should().HaveCount(2);
        _writes.First().Body.Should().Contain("upload_bps=0");
        _writes.Last().Body.Should().Contain("upload_bps=123000000")
            .And.Contain("target_name=\"Enriched target\"");
        var lines = _writes.SelectMany(write => write.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        lines.Select(line => line.Split(' ')[0]).Distinct().Should().ContainSingle();
        lines.Select(line => line.Split(' ')[^1]).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Backfill_PreexistingBufferedWriteIsDrainedBeforeCurrentHistory()
    {
        var result = Result();
        result.UploadBitsPerSecond = 0;
        await SaveHistoryAsync("main", result);
        var client = _registry.GetDefault();
        await client.ReconfigureAsync();
        await client.WriteSpeedTestResultAsync(result);
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            (await db.Iperf3Results.SingleAsync()).UploadBitsPerSecond = 321_000_000;
            await db.SaveChangesAsync();
        }

        (await client.BackfillSpeedTestResultsAsync()).Should().BeTrue();

        _writes.Should().HaveCount(2);
        _writes.First().Body.Should().Contain("upload_bps=0");
        _writes.Last().Body.Should().Contain("upload_bps=321000000");
    }

    [Fact]
    public async Task Backfill_CancellationLeavesMarkerUnsetAndCanRetry()
    {
        await SaveHistoryAsync("main", Result());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeWrite = async context =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        };
        using var cancellation = new CancellationTokenSource();
        var backfill = BackfillAsync(ct: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backfill);
        (await BackfillMarkersAsync()).Should().BeEmpty();
        _beforeWrite = null;
        (await BackfillAsync()).Should().BeTrue();
        (await BackfillMarkersAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task BackfillScheduler_ConfiguredLaterExportsWithoutMonitoringOrRestart()
    {
        await SaveHistoryAsync("main", Result());
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            (await db.MonitoringSettings.SingleAsync()).InfluxDbToken = null;
            await db.SaveChangesAsync();
        }
        using var schedulingServices = new ServiceCollection().AddSingleton(_registry).BuildServiceProvider();
        var scheduler = new MonitoringCollectionRegistry(
            schedulingServices,
            _mainDbFactory, null!, NullLogger<MonitoringCollectionRegistry>.Instance);
        try
        {
            await ScheduleBackfillAsync(scheduler);
            _writes.Should().BeEmpty();
            (await BackfillMarkersAsync()).Should().BeEmpty();

            await using (var db = _mainDbFactory.CreateDbContext())
            {
                (await db.MonitoringSettings.SingleAsync()).InfluxDbToken = "test-token";
                await db.SaveChangesAsync();
            }
            await _registry.ReconfigureAllAsync();
            await ScheduleBackfillAsync(scheduler);
            _writes.Should().ContainSingle();
            var bucketReads = _bucketReads;

            await ScheduleBackfillAsync(scheduler);
            _bucketReads.Should().Be(bucketReads);
            _writes.Should().ContainSingle();
        }
        finally
        {
            await scheduler.StopForSiteAsync("main");
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task BackfillScheduler_FailureIsThrottledAndUnfinishedWorkRetries()
    {
        await SaveHistoryAsync("main", Result());
        _writeStatus = StatusCodes.Status503ServiceUnavailable;
        using var schedulingServices = new ServiceCollection().AddSingleton(_registry).BuildServiceProvider();
        var logger = new Mock<ILogger<MonitoringCollectionRegistry>>();
        var scheduler = new MonitoringCollectionRegistry(schedulingServices, _mainDbFactory, null!, logger.Object);
        try
        {
            await ScheduleBackfillAsync(scheduler);
            VerifyLog(logger, LogLevel.Warning, "backfill failed");
            (await BackfillMarkersAsync()).Should().BeEmpty();
            _writes.Should().ContainSingle();

            await ScheduleBackfillAsync(scheduler);
            _writes.Should().ContainSingle();
            SetBackfillState(scheduler, "RetryAfter", DateTime.MinValue);
            _writeStatus = StatusCodes.Status204NoContent;

            await ScheduleBackfillAsync(scheduler);
            _writes.Should().HaveCount(2);
            (await BackfillMarkersAsync()).Should().ContainSingle();
        }
        finally
        {
            await scheduler.StopForSiteAsync("main");
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task Backfill_FailedBufferedMonitoringWriteDoesNotBlockHistoricalCompletion()
    {
        await SaveHistoryAsync("main", Result());
        var client = _registry.GetDefault();
        await client.ReconfigureAsync();
        await client.WriteCustomFieldsAsync("device_health", "aa:bb:cc:dd:ee:ff",
            new Dictionary<string, object> { ["temperature"] = 40.0 }, null, null, null, DateTime.UtcNow);
        _beforeWrite = context =>
        {
            _writeStatus = context.Request.Query["bucket"] == "metrics"
                ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        };

        (await client.BackfillSpeedTestResultsAsync()).Should().BeTrue();

        _writes.Select(write => write.Bucket).Should().Equal("metrics", "history");
        (await BackfillMarkersAsync()).Should().ContainSingle();
        VerifyLog(_influxLogger, LogLevel.Error, "points dropped");
    }

    [Fact]
    public async Task BackfillScheduler_ConfigurationReadFailureIsThrottledUntilExplicitRefresh()
    {
        await SaveHistoryAsync("main", Result());
        using var schedulingServices = new ServiceCollection().AddSingleton(_registry).BuildServiceProvider();
        var logger = new Mock<ILogger<MonitoringCollectionRegistry>>();
        var scheduler = new MonitoringCollectionRegistry(schedulingServices, _mainDbFactory, null!, logger.Object);
        try
        {
            _mainDbFactory.FailCreation = true;
            await ScheduleBackfillAsync(scheduler);
            VerifyLog(logger, LogLevel.Warning, "backfill failed");
            _mainDbFactory.FailCreation = false;

            await ScheduleBackfillAsync(scheduler);
            _bucketReads.Should().Be(0);
            _writes.Should().BeEmpty();
            (await BackfillMarkersAsync()).Should().BeEmpty();
            VerifyLog(logger, LogLevel.Warning, "backfill failed");

            await _registry.ReconfigureAllAsync();
            await ScheduleBackfillAsync(scheduler);

            _writes.Should().ContainSingle();
            (await BackfillMarkersAsync()).Should().ContainSingle();
        }
        finally
        {
            _mainDbFactory.FailCreation = false;
            await scheduler.StopForSiteAsync("main");
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task BackfillScheduler_CompletedAndThrottledPassesDoNotReconfigureClients()
    {
        await SaveHistoryAsync("main", Result());
        using var schedulingServices = new ServiceCollection().AddSingleton(_registry).BuildServiceProvider();
        var scheduler = new MonitoringCollectionRegistry(schedulingServices, _mainDbFactory, null!,
            NullLogger<MonitoringCollectionRegistry>.Instance);
        try
        {
            await ScheduleBackfillAsync(scheduler);
            var client = _registry.GetDefault();
            var configGate = (SemaphoreSlim)GetField(client, "_configLock")!;
            await configGate.WaitAsync();
            try
            {
                await ScheduleBackfillAsync(scheduler);
                SetBackfillState(scheduler, "Completed", false);
                await ScheduleBackfillAsync(scheduler);
            }
            finally { configGate.Release(); }
            _bucketReads.Should().Be(1);
            _writes.Should().ContainSingle();
        }
        finally
        {
            await scheduler.StopForSiteAsync("main");
            scheduler.Dispose();
        }
    }

    [Theory]
    [InlineData("main")]
    [InlineData("branch")]
    public async Task BackfillScheduler_EmptyUnconfiguredSiteRechecksAfterExplicitSetup(string site)
    {
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            (await db.MonitoringSettings.SingleAsync()).InfluxDbToken = null;
            await db.SaveChangesAsync();
        }
        using var schedulingServices = new ServiceCollection().AddSingleton(_registry).BuildServiceProvider();
        var scheduler = new MonitoringCollectionRegistry(schedulingServices, _mainDbFactory, null!,
            NullLogger<MonitoringCollectionRegistry>.Instance);
        try
        {
            await ScheduleBackfillAsync(scheduler, site);
            _bucketReads.Should().Be(0);
            await SaveHistoryAsync(site, Result());
            await using (var db = _mainDbFactory.CreateDbContext())
            {
                (await db.MonitoringSettings.SingleAsync()).InfluxDbToken = "test-token";
                await db.SaveChangesAsync();
            }
            await _registry.ReconfigureAllAsync();

            await ScheduleBackfillAsync(scheduler, site);

            _writes.Should().ContainSingle().Which.Bucket.Should().Be(site == "main" ? "history" : "branch-history");
            (await BackfillMarkersAsync(site)).Should().ContainSingle();
        }
        finally
        {
            await scheduler.StopForSiteAsync(site);
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task BackfillScheduler_SharedDestinationRefreshInvalidatesCompletedSecondarySite()
    {
        await SaveHistoryAsync("branch", Result());
        using var schedulingServices = new ServiceCollection().AddSingleton(_registry).BuildServiceProvider();
        var scheduler = new MonitoringCollectionRegistry(schedulingServices, _mainDbFactory, null!,
            NullLogger<MonitoringCollectionRegistry>.Instance);
        try
        {
            await ScheduleBackfillAsync(scheduler, "branch");
            await using (var db = _mainDbFactory.CreateDbContext())
            {
                (await db.MonitoringSettings.SingleAsync()).InfluxDbOrg = "new-org";
                await db.SaveChangesAsync();
            }
            await _registry.ReconfigureAllAsync();

            await ScheduleBackfillAsync(scheduler, "branch");

            _writes.Should().HaveCount(2);
            _writes.Should().OnlyContain(write => write.Bucket == "branch-history");
            (await BackfillMarkersAsync("branch")).Should().HaveCount(2);
        }
        finally
        {
            await scheduler.StopForSiteAsync("branch");
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task BackfillScheduler_UnconfiguredSiteProbesSettingsWhenRetryBecomesDue()
    {
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            (await db.MonitoringSettings.SingleAsync()).InfluxDbToken = null;
            await db.SaveChangesAsync();
        }
        using var schedulingServices = new ServiceCollection().AddSingleton(_registry).BuildServiceProvider();
        var scheduler = new MonitoringCollectionRegistry(schedulingServices, _mainDbFactory, null!,
            NullLogger<MonitoringCollectionRegistry>.Instance);
        try
        {
            await ScheduleBackfillAsync(scheduler);
            await SaveHistoryAsync("main", Result());
            await using (var db = _mainDbFactory.CreateDbContext())
            {
                (await db.MonitoringSettings.SingleAsync()).InfluxDbToken = "test-token";
                await db.SaveChangesAsync();
            }
            await ScheduleBackfillAsync(scheduler);
            _writes.Should().BeEmpty();
            SetBackfillState(scheduler, "RetryAfter", DateTime.MinValue);

            await ScheduleBackfillAsync(scheduler);

            _writes.Should().ContainSingle();
            (await BackfillMarkersAsync()).Should().ContainSingle();
        }
        finally
        {
            await scheduler.StopForSiteAsync("main");
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task BackfillScheduler_StoppingSiteCancelsAndAwaitsPendingWrite()
    {
        await SaveHistoryAsync("main", Result());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeWrite = async context =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        };
        using var schedulingServices = new ServiceCollection().AddSingleton(_registry).BuildServiceProvider();
        var scheduler = new MonitoringCollectionRegistry(
            schedulingServices, _mainDbFactory, null!, NullLogger<MonitoringCollectionRegistry>.Instance);
        try
        {
            var scheduled = ScheduleBackfillAsync(scheduler);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await scheduler.StopForSiteAsync("main").WaitAsync(TimeSpan.FromSeconds(10));
            await scheduled;

            (await BackfillMarkersAsync()).Should().BeEmpty();
            ((System.Collections.IDictionary)GetField(scheduler, "_backfills")!).Count.Should().Be(0);
        }
        finally
        {
            await scheduler.StopForSiteAsync("main");
            scheduler.Dispose();
        }
    }

    private static void SetBackfillState(MonitoringCollectionRegistry scheduler, string property, object value)
    {
        var state = ((System.Collections.IDictionary)GetField(scheduler, "_backfills")!)["main"]!;
        state.GetType().GetProperty(property)!.SetValue(state, value);
    }

    private static async Task ScheduleBackfillAsync(MonitoringCollectionRegistry scheduler, string site = "main")
    {
        var running = (HashSet<string>)GetField(scheduler, "_running")!;
        running.Add(site);
        scheduler.GetType().GetMethod("ScheduleSpeedTestBackfill", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, new object[] { site });
        var states = (System.Collections.IDictionary)GetField(scheduler, "_backfills")!;
        var state = states[site]!;
        await ((Task)state.GetType().GetProperty("Task")!.GetValue(state)!).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task<bool> BackfillAsync(string site = "main", CancellationToken ct = default)
    {
        var client = _registry.GetFor(site);
        await client.ReconfigureAsync(ct);
        return await client.BackfillSpeedTestResultsAsync(ct);
    }

    private async Task SaveHistoryAsync(string site, params Iperf3Result[] results)
    {
        await using var db = _siteDbFactory.CreateForSite(site);
        db.Iperf3Results.AddRange(results);
        await db.SaveChangesAsync();
    }

    private async Task<List<SystemSetting>> BackfillMarkersAsync(string site = "main")
    {
        await using var db = _siteDbFactory.CreateForSite(site);
        return await db.SystemSettings.Where(setting =>
            setting.Key.StartsWith(SystemSettingKeys.SpeedTestInfluxBackfillPrefix)).ToListAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LanMissingTarget_LogsSkippedExportWithoutChangingSavedResult(string host)
    {
        var result = Result();
        result.Direction = SpeedTestDirection.ServerToDevice;
        result.DeviceHost = host;
        await using (var db = _mainDbFactory.CreateDbContext())
        {
            db.Iperf3Results.Add(result);
            await db.SaveChangesAsync();
        }
        var logger = new Mock<ILogger>();

        await SpeedTestInfluxExporter.ExportAsync(_registry, "main", result, logger.Object);
        await _registry.GetDefault().FlushAsync();

        VerifyLog(logger, LogLevel.Warning, "Failed to export speed test result");
        _writes.Should().BeEmpty();
        result.Success.Should().BeTrue();
        await using var savedDb = _mainDbFactory.CreateDbContext();
        var saved = await savedDb.Iperf3Results.SingleAsync();
        saved.Id.Should().Be(result.Id);
        saved.DeviceHost.Should().Be(host);
        saved.Success.Should().BeTrue();
    }

    private static Iperf3Result Result() => new()
    {
        Direction = SpeedTestDirection.UwnWan,
        DeviceHost = "speed.example.net",
        WanNetworkGroup = "WAN2",
        Success = true,
        DownloadBitsPerSecond = 500_000_000,
        UploadBitsPerSecond = 50_000_000,
        TestTime = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc)
    };

    private static void VerifyLog<T>(Mock<T> logger, LogLevel level, string message) where T : class, ILogger =>
        logger.Verify(log => log.Log(
            level, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((value, _) => value.ToString()!.Contains(message)),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

    private sealed class TestDbFactory(DbContextOptions<NetworkOptimizerDbContext> options)
        : IDbContextFactory<NetworkOptimizerDbContext>
    {
        public bool FailCreation { get; set; }
        public NetworkOptimizerDbContext CreateDbContext() => FailCreation
            ? throw new InvalidOperationException("SQLite configuration is temporarily unavailable")
            : new(options);
    }

    private sealed class FailingSaveDbContext(DbContextOptions<NetworkOptimizerDbContext> options)
        : NetworkOptimizerDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<int>(new InvalidOperationException("SQLite save failed"));
    }

    private sealed class TestWanService : WanSpeedTestServiceBase
    {
        private readonly Iperf3Result _result;
        private readonly bool _failTest;

        public TestWanService(
            IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
            SiteDbContextFactory siteDbFactory,
            MonitoringInfluxRegistry registry,
            string site,
            Iperf3Result result,
            bool failTest = false,
            ILogger? logger = null)
            : base(dbFactory, Mock.Of<INetworkPathAnalyzer>(), logger ?? NullLogger.Instance,
                new Iperf3ServerService(NullLogger<Iperf3ServerService>.Instance, null!, new ConfigurationBuilder().Build()),
                siteDbFactory: siteDbFactory, siteSlug: site, influxRegistry: registry)
        {
            _result = result;
            _failTest = failTest;
        }

        protected override SpeedTestDirection Direction => SpeedTestDirection.UwnWan;
        protected override Task<bool> CanRunForSiteAsync() => Task.FromResult(true);

        protected override Task<Iperf3Result?> RunTestCoreAsync(Action<string, int, string?> report, CancellationToken cancellationToken) =>
            _failTest ? throw new InvalidOperationException("Test failed") : Task.FromResult<Iperf3Result?>(_result);

        protected override Iperf3Result CreateFailedResult(string errorMessage) => new()
        {
            Direction = Direction,
            DeviceHost = "speed.example.net",
            Success = false,
            ErrorMessage = errorMessage
        };

        protected override Task AnalyzePathInBackgroundAsync(int resultId, string? wanIp = null, string? resolvedWanGroup = null) =>
            Task.CompletedTask;
    }
}

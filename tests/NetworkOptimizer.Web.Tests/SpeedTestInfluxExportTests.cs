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
            context.Response.StatusCode = _writeStatus;
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

        _services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<NetworkOptimizerDbContext>>(_mainDbFactory)
            .AddSingleton(_siteDbFactory)
            .AddSingleton(Mock.Of<ICredentialProtectionService>(
                protection => protection.Decrypt(It.IsAny<string>()) == "test-token"))
            .AddSingleton(_influxLogger.Object)
            .BuildServiceProvider();
        _registry = new MonitoringInfluxRegistry(_services, NullLogger<MonitoringInfluxRegistry>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _registry.DisposeAsync();
        await _services.DisposeAsync();
        await _server.DisposeAsync();
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
            write.Body.Should().Contain("direction=UwnWanGateway").And.Contain("wan_network_group=WAN2")
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
            write.Body.Should().Contain("test_type=wan").And.Contain("direction=OpenSpeedTestWan")
                .And.Contain("external_server_name=\"vps-test\"").And.Contain("wan_network_group=unknown")
                .And.NotContain("server_host").And.NotContain("target_host").And.NotContain("192.0.2.50");
        }
        else
        {
            write.Body.Should().Contain("test_type=lan").And.Contain("direction=BrowserToServer")
                .And.Contain("target_host=192.0.2.50").And.NotContain("external_server_name");
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
        firstWrite.Body.Should().Contain("test_type=lan").And.Contain("direction=ClientToServer")
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

    private ServiceProvider ProductionServices(string? enrichedName = null)
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
            bundleCache[site] = new(null!, null!, null!, null!, null!, null!);
        }
        var services = new ServiceCollection()
            .AddLogging()
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
        public NetworkOptimizerDbContext CreateDbContext() => new(options);
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

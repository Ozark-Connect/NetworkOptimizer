using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// A site whose buckets were never provisioned fails every read, every few seconds. The first
/// failure warns; repeats of the same failure log at Debug.
/// </summary>
public sealed class MonitoringInfluxClientFailureLogTests : IDisposable
{
    private const string MissingBucket = "failed to initialize execute state: could not find bucket \"test-network_monitoring\"";

    private readonly HttpListener _influx = new();
    private readonly string _url;
    private int _queries;

    public MonitoringInfluxClientFailureLogTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _url = $"http://localhost:{port}/";
        _influx.Prefixes.Add(_url);
        _influx.Start();
        _ = AnswerNotFoundAsync();
    }

    private async Task AnswerNotFoundAsync()
    {
        while (_influx.IsListening)
        {
            HttpListenerContext context;
            try { context = await _influx.GetContextAsync(); }
            catch { return; }

            Interlocked.Increment(ref _queries);
            var body = Encoding.UTF8.GetBytes($"{{\"code\":\"not found\",\"message\":\"{MissingBucket.Replace("\"", "\\\"")}\"}}");
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    public void Dispose() => _influx.Close();

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

    private sealed class RecordingLogger : ILogger<MonitoringInfluxClient>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task A_repeated_query_failure_warns_once()
    {
        var (client, logger) = await ConfiguredClientAsync();
        try
        {
            (await client.ReconfigureAsync(refreshSpeedTestBackfill: false)).Should().BeTrue();

            for (var i = 0; i < 3; i++)
                (await client.QueryWifiClientBandsAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow)).Should().BeEmpty();

            _queries.Should().Be(3);
            List<(LogLevel Level, string Message)> failures;
            lock (logger.Entries)
                failures = logger.Entries.Where(e => e.Message.Contains("InfluxDB query failed")).ToList();
            failures.Should().HaveCount(3);
            failures.Count(e => e.Level == LogLevel.Warning).Should().Be(1);
            failures.Skip(1).Should().OnlyContain(e => e.Level == LogLevel.Debug);
        }
        finally
        {
            await client.DisposeOwnedAsync();
        }
    }

    [Fact]
    public async Task Concurrent_query_failures_warn_once()
    {
        var (client, logger) = await ConfiguredClientAsync();
        try
        {
            (await client.ReconfigureAsync(refreshSpeedTestBackfill: false)).Should().BeTrue();

            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                client.QueryWifiClientBandsAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow))));

            List<(LogLevel Level, string Message)> failures;
            lock (logger.Entries)
                failures = logger.Entries.Where(e => e.Message.Contains("InfluxDB query failed")).ToList();
            failures.Should().HaveCount(16);
            failures.Count(e => e.Level == LogLevel.Warning).Should().Be(1);
        }
        finally
        {
            await client.DisposeOwnedAsync();
        }
    }

    [Fact]
    public async Task A_repeated_flush_failure_logs_an_error_once()
    {
        var (client, logger) = await ConfiguredClientAsync();
        try
        {
            (await client.ReconfigureAsync(refreshSpeedTestBackfill: false)).Should().BeTrue();

            for (var i = 0; i < 3; i++)
            {
                await client.WriteLinkSpeedAsync("aa:bb:cc:dd:ee:ff", "eth0", 1000, true, DateTime.UtcNow);
                await client.FlushAsync();
            }

            _queries.Should().Be(3, "each flush sends its one point");
            List<(LogLevel Level, string Message)> drops;
            lock (logger.Entries)
                drops = logger.Entries.Where(e => e.Message.Contains("Failed to flush monitoring points")).ToList();
            drops.Should().HaveCount(3);
            drops.Count(e => e.Level == LogLevel.Error).Should().Be(1);
            drops.Skip(1).Should().OnlyContain(e => e.Level == LogLevel.Debug);
        }
        finally
        {
            await client.DisposeOwnedAsync();
        }
    }

    private async Task<(MonitoringInfluxClient Client, RecordingLogger Logger)> ConfiguredClientAsync()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new NetworkOptimizerDbContext(options))
        {
            db.MonitoringSettings.Add(new MonitoringSettings
            {
                InfluxDbUrl = _url,
                InfluxDbOrg = "test-org",
                InfluxDbToken = "test-token",
                InfluxDbBucket = "test-network_monitoring",
            });
            await db.SaveChangesAsync();
        }

        var logger = new RecordingLogger();
        return (new MonitoringInfluxClient(new TestDbFactory(options), new NoopCredentialProtection(), logger), logger);
    }
}

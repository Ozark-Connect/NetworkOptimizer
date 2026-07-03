using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// MonitoringInfluxClient is owned by MonitoringInfluxRegistry but handed to
/// request/circuit scopes through a scoped forwarding DI registration. Because
/// it is IAsyncDisposable, the container calls DisposeAsync at every scope end -
/// so DisposeAsync MUST be a no-op, or the first chart request / page visit
/// tears down the shared client and the collection agent, ISP Health, and all
/// history reads then throw ObjectDisposedException (the production outage this
/// guards against). Real teardown lives in DisposeOwnedAsync, called only by the
/// registry at app shutdown.
/// </summary>
public class MonitoringInfluxClientDisposalTests
{
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

    private static MonitoringInfluxClient NewClient()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MonitoringInfluxClient(
            new TestDbFactory(options),
            new NoopCredentialProtection(),
            NullLogger<MonitoringInfluxClient>.Instance);
    }

    [Fact]
    public async Task DisposeAsync_IsNoOp_ClientRemainsUsableAfterScopeDisposal()
    {
        var client = NewClient();

        // Simulate the DI container disposing the scope-forwarded instance at the
        // end of a request/circuit scope (and a redundant second scope).
        await client.DisposeAsync();
        await client.DisposeAsync();

        // ReconfigureAsync acquires the config semaphore first (line that threw in
        // production). With the no-op DisposeAsync it must succeed - returning false
        // only because no settings row exists - not throw ObjectDisposedException.
        var configured = await client.ReconfigureAsync();
        Assert.False(configured);
    }

    [Fact]
    public async Task DisposeOwnedAsync_PerformsRealTeardown_AndIsIdempotent()
    {
        var client = NewClient();

        await client.DisposeOwnedAsync();
        await client.DisposeOwnedAsync(); // guarded by _disposed; must not throw
    }
}

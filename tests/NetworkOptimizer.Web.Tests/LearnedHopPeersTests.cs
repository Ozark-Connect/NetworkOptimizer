using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class LearnedHopPeersTests
{
    private sealed class InMemoryFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly string _name = Guid.NewGuid().ToString();

        public NetworkOptimizerDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseInMemoryDatabase(_name).Options);
    }

    private sealed class OnlyPeers(params string[] slugs) : ILearnedHopPeers
    {
        public Task<Func<string, bool>> PeersOfAsync(string siteSlug, CancellationToken ct = default) =>
            Task.FromResult<Func<string, bool>>(s => slugs.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class BrokenPeers : ILearnedHopPeers
    {
        public Task<Func<string, bool>> PeersOfAsync(string siteSlug, CancellationToken ct = default) =>
            throw new InvalidOperationException("unreadable");
    }

    private static InMemoryFactory Seed()
    {
        var factory = new InMemoryFactory();
        using var db = factory.CreateDbContext();
        db.LearnedHopLocations.AddRange(
            Row("site-a", "203.0.113.1", "Dallas"),
            Row("site-b", "203.0.113.2", "Atlanta"),
            Row("site-c", "203.0.113.3", "Denver"));
        db.SaveChanges();
        return factory;
    }

    private static LearnedHopLocation Row(string site, string address, string city) => new()
    {
        SiteSlug = site, Address = address, City = city, CountryCode = "US", Latitude = 33, Longitude = -97,
        Source = (int)HopLocationSource.Hostname, Detail = "From the router's hostname", SeenAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Every_site_shares_by_default()
    {
        var learned = new LearnedHopLocations(Seed(), new InstallLearnedHopPeers(), NullLogger<LearnedHopLocations>.Instance);

        var places = await learned.GetAsync("site-a");

        places.Keys.Should().BeEquivalentTo("203.0.113.1", "203.0.113.2", "203.0.113.3");
    }

    [Fact]
    public async Task Only_peer_sites_places_are_used()
    {
        var learned = new LearnedHopLocations(Seed(), new OnlyPeers("site-a", "site-b"), NullLogger<LearnedHopLocations>.Instance);

        var places = await learned.GetAsync("site-a");

        places.Keys.Should().BeEquivalentTo("203.0.113.1", "203.0.113.2");
    }

    [Fact]
    public async Task Unreadable_peers_fall_back_to_the_sites_own_places()
    {
        var learned = new LearnedHopLocations(Seed(), new BrokenPeers(), NullLogger<LearnedHopLocations>.Instance);

        var places = await learned.GetAsync("site-b");

        places.Keys.Should().BeEquivalentTo("203.0.113.2");
    }
}

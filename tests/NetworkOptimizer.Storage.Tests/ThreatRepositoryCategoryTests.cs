using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// Covers the source/destination geo separation in GetTopSourcesAsync and the new
/// GetSourcesByCategoryAsync entry point that powers the audit report's categorized
/// "Known Infrastructure Activity" and "Trusted User Activity" sub-tables.
/// </summary>
public class ThreatRepositoryCategoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ThreatRepository _repository;
    private readonly DateTime _now = DateTime.UtcNow;

    public ThreatRepositoryCategoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        var logger = new Mock<ILogger<ThreatRepository>>();
        _repository = new ThreatRepository(_context, logger.Object);
    }

    public void Dispose() => _context.Dispose();

    private ThreatEvent MakeEvent(string sourceIp, string destIp = "203.0.113.10",
        string? sourceCountry = null, string? sourceAsnOrg = null,
        string? destCountry = "US", string? destAsnOrg = "Example LLC",
        int severity = 2)
    {
        return new ThreatEvent
        {
            Timestamp = _now,
            SourceIp = sourceIp,
            DestIp = destIp,
            DestPort = 443,
            Protocol = "tcp",
            SignatureName = "test",
            Category = "test",
            Severity = severity,
            Action = ThreatAction.Blocked,
            InnerAlertId = Guid.NewGuid().ToString(),
            CountryCode = sourceCountry,
            AsnOrg = sourceAsnOrg,
            DestCountryCode = destCountry,
            DestAsnOrg = destAsnOrg,
            GeoEnriched = true
        };
    }

    // --- GetTopSourcesAsync: source vs destination separation ---

    [Fact]
    public async Task GetTopSourcesAsync_PrivateSource_DoesNotInheritDestinationAsn()
    {
        // This is the bug the PR fixes. Pre-fix, the destination ASN (NextDNS, Google)
        // was written onto the event's source-row fields and surfaced here. Post-fix,
        // source enrichment is null for RFC1918 sources, which is the truthful answer.
        _context.ThreatEvents.Add(MakeEvent(
            sourceIp: "192.0.2.20",
            destIp: "198.51.100.65",
            sourceCountry: null, sourceAsnOrg: null,
            destCountry: "US", destAsnOrg: "NextDNS, Inc."));
        await _context.SaveChangesAsync();

        var result = await _repository.GetTopSourcesAsync(_now.AddHours(-1), _now.AddHours(1), 10);

        result.Should().HaveCount(1);
        result[0].SourceIp.Should().Be("192.0.2.20");
        result[0].AsnOrg.Should().BeNull("an RFC1918 source has no public ASN");
        result[0].CountryCode.Should().BeNull("an RFC1918 source has no country");
    }

    [Fact]
    public async Task GetTopSourcesAsync_PublicSource_ReturnsSourceEnrichment()
    {
        _context.ThreatEvents.Add(MakeEvent(
            sourceIp: "8.8.8.8",
            sourceCountry: "US", sourceAsnOrg: "Google LLC"));
        await _context.SaveChangesAsync();

        var result = await _repository.GetTopSourcesAsync(_now.AddHours(-1), _now.AddHours(1), 10);

        result.Should().HaveCount(1);
        result[0].AsnOrg.Should().Be("Google LLC");
        result[0].CountryCode.Should().Be("US");
    }

    // --- GetSourcesByCategoryAsync ---

    [Fact]
    public async Task GetSourcesByCategoryAsync_NoFiltersOfCategory_ReturnsEmpty()
    {
        _context.ThreatEvents.Add(MakeEvent("192.0.2.10"));
        await _context.SaveChangesAsync();

        var result = await _repository.GetSourcesByCategoryAsync(
            _now.AddHours(-1), _now.AddHours(1), ThreatFilterCategory.Infrastructure);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSourcesByCategoryAsync_ExactMatch_ReturnsMatchingSources()
    {
        _context.ThreatEvents.AddRange(
            MakeEvent("192.0.2.10"),
            MakeEvent("192.0.2.10"),
            MakeEvent("192.0.2.30"));
        _context.ThreatNoiseFilters.Add(new ThreatNoiseFilter
        {
            SourceIp = "192.0.2.10",
            Category = ThreatFilterCategory.Infrastructure,
            Label = "Network Optimizer (self)",
            Description = "self",
            Enabled = true
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetSourcesByCategoryAsync(
            _now.AddHours(-1), _now.AddHours(1), ThreatFilterCategory.Infrastructure);

        result.Should().HaveCount(1);
        result[0].SourceIp.Should().Be("192.0.2.10");
        result[0].EventCount.Should().Be(2);
        result[0].Label.Should().Be("Network Optimizer (self)");
        result[0].MatchedFilterCategory.Should().Be(ThreatFilterCategory.Infrastructure);
    }

    [Fact]
    public async Task GetSourcesByCategoryAsync_CidrFilter_MatchesSubnet()
    {
        _context.ThreatEvents.AddRange(
            MakeEvent("192.0.2.10"),
            MakeEvent("192.0.2.20"),
            MakeEvent("198.51.100.25")); // outside /24
        _context.ThreatNoiseFilters.Add(new ThreatNoiseFilter
        {
            SourceIp = "192.0.2.0/24",
            Category = ThreatFilterCategory.Infrastructure,
            Label = "Servers VLAN infrastructure",
            Description = "servers vlan",
            Enabled = true
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetSourcesByCategoryAsync(
            _now.AddHours(-1), _now.AddHours(1), ThreatFilterCategory.Infrastructure);

        result.Should().HaveCount(2);
        result.Select(r => r.SourceIp).Should().BeEquivalentTo(new[] { "192.0.2.10", "192.0.2.20" });
        result.All(r => r.Label == "Servers VLAN infrastructure").Should().BeTrue();
    }

    [Fact]
    public async Task GetSourcesByCategoryAsync_DisabledFilter_Excluded()
    {
        _context.ThreatEvents.Add(MakeEvent("192.0.2.10"));
        _context.ThreatNoiseFilters.Add(new ThreatNoiseFilter
        {
            SourceIp = "192.0.2.10",
            Category = ThreatFilterCategory.Infrastructure,
            Description = "self",
            Enabled = false
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetSourcesByCategoryAsync(
            _now.AddHours(-1), _now.AddHours(1), ThreatFilterCategory.Infrastructure);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSourcesByCategoryAsync_BypassesNoiseFilterExclusion()
    {
        // The whole point: even though SetNoiseFilters would normally hide events
        // from this source, GetSourcesByCategoryAsync surfaces them so the audit
        // report can render them in the categorized sub-table.
        _context.ThreatEvents.Add(MakeEvent("192.0.2.10"));
        var filter = new ThreatNoiseFilter
        {
            SourceIp = "192.0.2.10",
            Category = ThreatFilterCategory.Infrastructure,
            Label = "self",
            Description = "self",
            Enabled = true
        };
        _context.ThreatNoiseFilters.Add(filter);
        await _context.SaveChangesAsync();

        // Activate the filter on the repo (this is what AuditService.BuildThreatSummaryAsync does).
        _repository.SetNoiseFilters(new List<ThreatNoiseFilter> { filter });

        var topSources = await _repository.GetTopSourcesAsync(_now.AddHours(-1), _now.AddHours(1));
        var infraSources = await _repository.GetSourcesByCategoryAsync(
            _now.AddHours(-1), _now.AddHours(1), ThreatFilterCategory.Infrastructure);

        topSources.Should().BeEmpty("the noise filter excludes this source from the main table");
        infraSources.Should().HaveCount(1, "the category-specific query surfaces it back");
    }

    [Fact]
    public async Task GetSourcesByCategoryAsync_OnlyReturnsRequestedCategory()
    {
        _context.ThreatEvents.AddRange(
            MakeEvent("192.0.2.10"),
            MakeEvent("203.0.113.10"));
        _context.ThreatNoiseFilters.AddRange(
            new ThreatNoiseFilter
            {
                SourceIp = "192.0.2.10",
                Category = ThreatFilterCategory.Infrastructure,
                Label = "self",
                Description = "infra",
                Enabled = true
            },
            new ThreatNoiseFilter
            {
                SourceIp = "203.0.113.10",
                Category = ThreatFilterCategory.TrustedUser,
                Label = "Engineer workstation",
                Description = "trusted",
                Enabled = true
            });
        await _context.SaveChangesAsync();

        var infra = await _repository.GetSourcesByCategoryAsync(
            _now.AddHours(-1), _now.AddHours(1), ThreatFilterCategory.Infrastructure);
        var trusted = await _repository.GetSourcesByCategoryAsync(
            _now.AddHours(-1), _now.AddHours(1), ThreatFilterCategory.TrustedUser);

        infra.Should().ContainSingle().Which.SourceIp.Should().Be("192.0.2.10");
        trusted.Should().ContainSingle().Which.SourceIp.Should().Be("203.0.113.10");
    }

    [Fact]
    public async Task GetTopSourcesAsync_NoiseOnlyFilterSet_AllowsInfrastructureRowsThrough()
    {
        // The dashboard's Option B layout depends on this guarantee: when only
        // Noise-category filters are applied to the repo, Infrastructure and
        // TrustedUser sources must still appear in GetTopSourcesAsync so the
        // razor can attach a Category badge and show them in the main table.
        _context.ThreatEvents.AddRange(
            MakeEvent("192.0.2.10"),
            MakeEvent("192.0.2.10"),
            MakeEvent("198.51.100.99"));
        var infraFilter = new ThreatNoiseFilter
        {
            SourceIp = "192.0.2.10",
            Category = ThreatFilterCategory.Infrastructure,
            Label = "self",
            Description = "infra",
            Enabled = true
        };
        var noiseFilter = new ThreatNoiseFilter
        {
            SourceIp = "198.51.100.99",
            Category = ThreatFilterCategory.Noise,
            Description = "junk",
            Enabled = true
        };
        _context.ThreatNoiseFilters.AddRange(infraFilter, noiseFilter);
        await _context.SaveChangesAsync();

        // Simulate the dashboard service applying ONLY Noise-category filters
        // so Infrastructure rows remain visible in the main table.
        _repository.SetNoiseFilters(new List<ThreatNoiseFilter> { noiseFilter });

        var topSources = await _repository.GetTopSourcesAsync(_now.AddHours(-1), _now.AddHours(1), 10);

        topSources.Should().HaveCount(1, "the Noise-tagged source is excluded, but the Infrastructure one is not");
        topSources[0].SourceIp.Should().Be("192.0.2.10");
        topSources[0].EventCount.Should().Be(2);
    }
}

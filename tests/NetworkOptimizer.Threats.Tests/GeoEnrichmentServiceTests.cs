using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Threats.Enrichment;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

/// <summary>
/// Tests the source/destination geo enrichment contract. Full MaxMind lookups need
/// .mmdb files on disk so these tests exercise the boundaries that do not depend on
/// a real database: private-IP filtering, no-DB no-op behavior, and the per-event
/// invariants the bug fix relies on.
/// </summary>
public class GeoEnrichmentServiceTests
{
    private GeoEnrichmentService Make() =>
        new(new Mock<ILogger<GeoEnrichmentService>>().Object);

    private ThreatEvent MakeEvent(string sourceIp, string destIp) => new()
    {
        Timestamp = DateTime.UtcNow,
        SourceIp = sourceIp,
        DestIp = destIp,
        DestPort = 443,
        Protocol = "tcp",
        SignatureName = "test",
        Category = "test",
        InnerAlertId = Guid.NewGuid().ToString()
    };

    [Fact]
    public void Enrich_PrivateIpv4_ReturnsEmpty()
    {
        var svc = Make();

        svc.Enrich("10.99.99.99").Should().Be(GeoInfo.Empty);
        svc.Enrich("192.168.1.1").Should().Be(GeoInfo.Empty);
        svc.Enrich("172.16.5.5").Should().Be(GeoInfo.Empty);
    }

    [Fact]
    public void Enrich_LoopbackAndLinkLocal_ReturnsEmpty()
    {
        var svc = Make();

        svc.Enrich("127.0.0.1").Should().Be(GeoInfo.Empty);
        svc.Enrich("169.254.1.1").Should().Be(GeoInfo.Empty);
    }

    [Fact]
    public void Enrich_InvalidIp_ReturnsEmpty()
    {
        var svc = Make();

        svc.Enrich("not-an-ip").Should().Be(GeoInfo.Empty);
        svc.Enrich("").Should().Be(GeoInfo.Empty);
    }

    [Fact]
    public void EnrichEvents_EmptyList_DoesNotThrow()
    {
        var svc = Make();
        var act = () => svc.EnrichEvents(new List<ThreatEvent>());
        act.Should().NotThrow();
    }

    [Fact]
    public void EnrichEvents_NoDatabaseLoaded_LeavesFieldsNull()
    {
        // With no .mmdb files on disk, both readers are null and EnrichEvents is a
        // no-op. The pre-fix code path would still write destination ASN onto source
        // fields under these conditions if the redirect logic had run - this test
        // documents that none of that happens anymore.
        var svc = Make();
        var events = new List<ThreatEvent>
        {
            MakeEvent("192.0.2.20", "198.51.100.65")
        };

        svc.EnrichEvents(events);

        events[0].CountryCode.Should().BeNull();
        events[0].AsnOrg.Should().BeNull();
        events[0].DestCountryCode.Should().BeNull();
        events[0].DestAsnOrg.Should().BeNull();
        events[0].GeoEnriched.Should().BeFalse(
            "the backfill loop relies on this flag to avoid re-processing when no DB is available");
    }

    [Fact]
    public void IsCityAvailable_NoInit_ReturnsFalse()
    {
        var svc = Make();
        svc.IsCityAvailable.Should().BeFalse();
        svc.IsAsnAvailable.Should().BeFalse();
    }
}

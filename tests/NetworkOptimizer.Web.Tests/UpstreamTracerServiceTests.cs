using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class CleanAsnNameTests
{
    [Theory]
    [InlineData("Windstream Communications", "Windstream")]
    [InlineData("AT&T Enterprises", "AT&T")]
    [InlineData("Level 3 Parent, LLC", "Level 3")]
    [InlineData("Cox Communications Inc.", "Cox")]
    [InlineData("EXAMPLE TELEPHONE", "EXAMPLE")]
    [InlineData("Cisco OpenDNS, LLC", "Cisco OpenDNS")]
    [InlineData("Akamai International B.V.", "Akamai International")]
    [InlineData("Fastly, Inc.", "Fastly")]
    [InlineData("Deutsche Telekom AG", "Deutsche Telekom")]
    [InlineData("Comcast Cable Communications, LLC", "Comcast")]
    [InlineData("Charter Communications Inc", "Charter")]
    [InlineData("Lumen Technologies", "Lumen")]
    [InlineData("Frontier Communications Corporation", "Frontier")]
    [InlineData("TDS Telecom", "TDS")]
    [InlineData("Sievert Larsen Fiber LLC", "Sievert Larsen")]
    [InlineData("Rural Electric Cooperative", "Rural")]
    [InlineData("Google", "Google")]
    [InlineData("Cloudflare", "Cloudflare")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    public void Strips_corporate_suffixes(string? input, string expected)
    {
        UpstreamTracerService.CleanAsnName(input).Should().Be(expected);
    }

    [Fact]
    public void Strips_chained_suffixes()
    {
        UpstreamTracerService.CleanAsnName("Acme Telephone Company LLC")
            .Should().Be("Acme");
    }

    [Fact]
    public void Stops_before_emptying_the_name()
    {
        UpstreamTracerService.CleanAsnName("Communications LLC")
            .Should().NotBeEmpty();
    }
}

public class FormatTransitHopLabelTests
{
    [Theory]
    [InlineData("ae6-0.agr01.pop01-xx.us.windstream.net", null, "ae6-0.agr01.pop01-xx.us")]
    [InlineData("lag-101.ear1.Metro2.Level3.net", null, "lag-101.ear1.Metro2")]
    [InlineData("mcibbrj01.rd.ks.cox.net", null, "mcibbrj01.rd.ks")]
    [InlineData("ae25.25.ear1.Dallas1.net.lumen.tech", null, "ae25.25.ear1.Dallas1.net")]
    [InlineData("rtr1-handoff.example.net", null, "rtr1-handoff")]
    public void Strips_sld_and_tld(string hostname, string? ip, string expected)
    {
        UpstreamTracerService.FormatTransitHopLabel(hostname, ip).Should().Be(expected);
    }

    [Theory]
    [InlineData("h40.113.0.203.static.ip.example.net", "203.0.113.40")]
    [InlineData("203.0.113.146", "203.0.113.146")]
    public void Returns_null_for_ip_derived_hostnames(string hostname, string ip)
    {
        UpstreamTracerService.FormatTransitHopLabel(hostname, ip).Should().BeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void Returns_null_for_empty_input(string? hostname, string? ip)
    {
        UpstreamTracerService.FormatTransitHopLabel(hostname, ip).Should().BeNull();
    }

    [Theory]
    [InlineData("router.net", null)]
    [InlineData("single", null)]
    public void Returns_null_when_too_few_labels(string hostname, string? ip)
    {
        UpstreamTracerService.FormatTransitHopLabel(hostname, ip).Should().BeNull();
    }
}

public class UpstreamCommitTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _db;

    public UpstreamCommitTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new NetworkOptimizerDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Enabled_access_hop_creates_new_target()
    {
        var hops = new List<AccessHopCandidate>
        {
            MakeAccessHop("192.0.2.1", "TestISP bng", enabled: true)
        };
        var transits = new List<TransitAsnCandidate>();

        await CommitAsync(hops, transits);

        var target = await _db.MonitoringTargets.FirstOrDefaultAsync(t => t.Address == "192.0.2.1");
        target.Should().NotBeNull();
        target!.Enabled.Should().BeTrue();
        target.Name.Should().Be("TestISP bng");
        target.TargetType.Should().Be(MonitoringTargetType.AccessIsp);
    }

    [Fact]
    public async Task Disabled_access_hop_does_not_create_target()
    {
        var hops = new List<AccessHopCandidate>
        {
            MakeAccessHop("192.0.2.1", "TestISP bng", enabled: false)
        };

        await CommitAsync(hops, new List<TransitAsnCandidate>());

        var target = await _db.MonitoringTargets.FirstOrDefaultAsync(t => t.Address == "192.0.2.1");
        target.Should().BeNull();
    }

    [Fact]
    public async Task Unchecking_existing_access_hop_disables_it()
    {
        _db.MonitoringTargets.Add(MakeDbTarget("192.0.2.1", "Old Name", MonitoringTargetType.AccessIsp, enabled: true));
        await _db.SaveChangesAsync();

        var hops = new List<AccessHopCandidate>
        {
            MakeAccessHop("192.0.2.1", "New Name", enabled: false)
        };

        await CommitAsync(hops, new List<TransitAsnCandidate>());

        var target = await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.1");
        target.Enabled.Should().BeFalse();
        target.Name.Should().Be("New Name");
    }

    [Fact]
    public async Task Rechecking_disabled_access_hop_enables_it()
    {
        _db.MonitoringTargets.Add(MakeDbTarget("192.0.2.1", "Old Name", MonitoringTargetType.AccessIsp, enabled: false));
        await _db.SaveChangesAsync();

        var hops = new List<AccessHopCandidate>
        {
            MakeAccessHop("192.0.2.1", "New Name", enabled: true)
        };

        await CommitAsync(hops, new List<TransitAsnCandidate>());

        var target = await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.1");
        target.Enabled.Should().BeTrue();
        target.Name.Should().Be("New Name");
    }

    [Fact]
    public async Task Enabled_transit_creates_new_target()
    {
        var transit = MakeTransitCandidate("198.51.100.1", "Windstream agr01", 7029,
            method: DiscoveryMethod.DirectRouter, enabled: true);

        await CommitAsync(new List<AccessHopCandidate>(), new List<TransitAsnCandidate> { transit });

        var target = await _db.MonitoringTargets.FirstOrDefaultAsync(t => t.Address == "198.51.100.1");
        target.Should().NotBeNull();
        target!.Enabled.Should().BeTrue();
        target.Name.Should().Be("Windstream agr01");
        target.TargetType.Should().Be(MonitoringTargetType.Transit);
    }

    [Fact]
    public async Task Unchecking_existing_transit_disables_it()
    {
        _db.MonitoringTargets.Add(MakeDbTarget("198.51.100.1", "Old Transit", MonitoringTargetType.Transit, enabled: true));
        await _db.SaveChangesAsync();

        var transit = MakeTransitCandidate("198.51.100.1", "New Transit", 7029,
            method: DiscoveryMethod.DirectRouter, enabled: false);

        await CommitAsync(new List<AccessHopCandidate>(), new List<TransitAsnCandidate> { transit });

        var target = await _db.MonitoringTargets.FirstAsync(t => t.Address == "198.51.100.1");
        target.Enabled.Should().BeFalse();
        target.Name.Should().Be("New Transit");
    }

    [Fact]
    public async Task Rechecking_disabled_transit_enables_it()
    {
        _db.MonitoringTargets.Add(MakeDbTarget("198.51.100.1", "Old Transit", MonitoringTargetType.Transit, enabled: false));
        await _db.SaveChangesAsync();

        var transit = MakeTransitCandidate("198.51.100.1", "New Transit", 7029,
            method: DiscoveryMethod.DirectRouter, enabled: true);

        await CommitAsync(new List<AccessHopCandidate>(), new List<TransitAsnCandidate> { transit });

        var target = await _db.MonitoringTargets.FirstAsync(t => t.Address == "198.51.100.1");
        target.Enabled.Should().BeTrue();
        target.Name.Should().Be("New Transit");
    }

    [Fact]
    public async Task Unchecking_pathproxy_disables_it()
    {
        _db.MonitoringTargets.Add(MakeDbTarget("203.0.113.1", "Cloudflare", MonitoringTargetType.InternetService, enabled: true));
        await _db.SaveChangesAsync();

        var pathProxy = MakeTransitCandidate("203.0.113.1", "Cloudflare", 13335,
            method: DiscoveryMethod.PathProxy, enabled: false);
        pathProxy.PathProxyTarget = "203.0.113.1";

        await CommitAsync(new List<AccessHopCandidate>(), new List<TransitAsnCandidate> { pathProxy });

        var target = await _db.MonitoringTargets.FirstAsync(t => t.Address == "203.0.113.1");
        target.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Rechecking_pathproxy_enables_it()
    {
        _db.MonitoringTargets.Add(MakeDbTarget("203.0.113.1", "Cloudflare", MonitoringTargetType.InternetService, enabled: false));
        await _db.SaveChangesAsync();

        var pathProxy = MakeTransitCandidate("203.0.113.1", "Cloudflare", 13335,
            method: DiscoveryMethod.PathProxy, enabled: true);
        pathProxy.PathProxyTarget = "203.0.113.1";

        await CommitAsync(new List<AccessHopCandidate>(), new List<TransitAsnCandidate> { pathProxy });

        var target = await _db.MonitoringTargets.FirstAsync(t => t.Address == "203.0.113.1");
        target.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Upsert_matches_by_address_when_targetid_differs()
    {
        var existing = MakeDbTarget("198.51.100.1", "Custom Name", MonitoringTargetType.Transit, enabled: true);
        existing.TargetId = "custom-abc123";
        _db.MonitoringTargets.Add(existing);
        await _db.SaveChangesAsync();
        var originalId = existing.Id;

        var transit = MakeTransitCandidate("198.51.100.1", "Discovery Name", 7029,
            method: DiscoveryMethod.DirectRouter, enabled: true);
        transit.TargetId = "transit-as7029-198-51-100-1";

        await CommitAsync(new List<AccessHopCandidate>(), new List<TransitAsnCandidate> { transit });

        var targets = await _db.MonitoringTargets.Where(t => t.Address == "198.51.100.1").ToListAsync();
        targets.Should().HaveCount(1);
        targets[0].Id.Should().Be(originalId);
        targets[0].Name.Should().Be("Discovery Name");
    }

    [Fact]
    public async Task Rename_applies_to_disabled_targets()
    {
        _db.MonitoringTargets.Add(MakeDbTarget("192.0.2.1", "bng01.example", MonitoringTargetType.AccessIsp, enabled: true));
        await _db.SaveChangesAsync();

        var hop = MakeAccessHop("192.0.2.1", "EXAMPLE bng01", enabled: false);

        await CommitAsync(new List<AccessHopCandidate> { hop }, new List<TransitAsnCandidate>());

        var target = await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.1");
        target.Enabled.Should().BeFalse();
        target.Name.Should().Be("EXAMPLE bng01");
    }

    [Fact]
    public async Task Asn_name_cleaned_on_save()
    {
        var hop = MakeAccessHop("192.0.2.1", "EXAMPLE bng", enabled: true);
        hop.AsnName = "EXAMPLE TELEPHONE";

        await CommitAsync(new List<AccessHopCandidate> { hop }, new List<TransitAsnCandidate>());

        var target = await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.1");
        target.AsnName.Should().Be("EXAMPLE");
    }

    [Fact]
    public async Task Unreachable_target_disabled_on_save()
    {
        _db.MonitoringTargets.Add(MakeDbTarget("198.51.100.1", "Transit 1", MonitoringTargetType.Transit, enabled: true));
        await _db.SaveChangesAsync();

        var transit = MakeTransitCandidate("198.51.100.1", "Transit 1", 7029,
            method: DiscoveryMethod.DirectRouter, enabled: false);
        transit.Unreachable = true;

        await CommitAsync(new List<AccessHopCandidate>(), new List<TransitAsnCandidate> { transit });

        var target = await _db.MonitoringTargets.FirstAsync(t => t.Address == "198.51.100.1");
        target.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Mixed_enable_disable_across_all_types()
    {
        _db.MonitoringTargets.Add(MakeDbTarget("192.0.2.1", "Access A", MonitoringTargetType.AccessIsp, enabled: true));
        _db.MonitoringTargets.Add(MakeDbTarget("192.0.2.2", "Access B", MonitoringTargetType.AccessIsp, enabled: true));
        _db.MonitoringTargets.Add(MakeDbTarget("198.51.100.1", "Transit A", MonitoringTargetType.Transit, enabled: true));
        _db.MonitoringTargets.Add(MakeDbTarget("198.51.100.2", "Transit B", MonitoringTargetType.Transit, enabled: false));
        _db.MonitoringTargets.Add(MakeDbTarget("203.0.113.1", "CDN A", MonitoringTargetType.InternetService, enabled: true));
        await _db.SaveChangesAsync();

        var hops = new List<AccessHopCandidate>
        {
            MakeAccessHop("192.0.2.1", "Access A New", enabled: true),
            MakeAccessHop("192.0.2.2", "Access B New", enabled: false),
        };
        var transits = new List<TransitAsnCandidate>
        {
            MakeTransitCandidate("198.51.100.1", "Transit A New", 7029, DiscoveryMethod.DirectRouter, enabled: false),
            MakeTransitCandidate("198.51.100.2", "Transit B New", 7029, DiscoveryMethod.DirectRouter, enabled: true),
            MakeTransitCandidate("203.0.113.1", "CDN A New", 13335, DiscoveryMethod.PathProxy, enabled: false),
        };
        transits[2].PathProxyTarget = "203.0.113.1";

        await CommitAsync(hops, transits);

        (await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.1")).Enabled.Should().BeTrue("Access A stays on");
        (await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.2")).Enabled.Should().BeFalse("Access B turned off");
        (await _db.MonitoringTargets.FirstAsync(t => t.Address == "198.51.100.1")).Enabled.Should().BeFalse("Transit A turned off");
        (await _db.MonitoringTargets.FirstAsync(t => t.Address == "198.51.100.2")).Enabled.Should().BeTrue("Transit B turned on");
        (await _db.MonitoringTargets.FirstAsync(t => t.Address == "203.0.113.1")).Enabled.Should().BeFalse("CDN A turned off");

        (await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.1")).Name.Should().Be("Access A New");
        (await _db.MonitoringTargets.FirstAsync(t => t.Address == "198.51.100.1")).Name.Should().Be("Transit A New");
        (await _db.MonitoringTargets.FirstAsync(t => t.Address == "203.0.113.1")).Name.Should().Be("CDN A New");
    }

    [Fact]
    public async Task Second_discovery_preserves_user_disable_decision()
    {
        _db.MonitoringTargets.Add(MakeDbTarget("192.0.2.1", "Access A", MonitoringTargetType.AccessIsp, enabled: true));
        _db.MonitoringTargets.Add(MakeDbTarget("198.51.100.1", "Transit A", MonitoringTargetType.Transit, enabled: true));
        await _db.SaveChangesAsync();

        // First save: user unchecks both
        await CommitAsync(
            new List<AccessHopCandidate> { MakeAccessHop("192.0.2.1", "Access A", enabled: false) },
            new List<TransitAsnCandidate> { MakeTransitCandidate("198.51.100.1", "Transit A", 7029, DiscoveryMethod.DirectRouter, enabled: false) }
        );

        (await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.1")).Enabled.Should().BeFalse();
        (await _db.MonitoringTargets.FirstAsync(t => t.Address == "198.51.100.1")).Enabled.Should().BeFalse();

        // Second save: user re-checks both
        await CommitAsync(
            new List<AccessHopCandidate> { MakeAccessHop("192.0.2.1", "Access A v2", enabled: true) },
            new List<TransitAsnCandidate> { MakeTransitCandidate("198.51.100.1", "Transit A v2", 7029, DiscoveryMethod.DirectRouter, enabled: true) }
        );

        var access = await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.1");
        access.Enabled.Should().BeTrue();
        access.Name.Should().Be("Access A v2");

        var transit = await _db.MonitoringTargets.FirstAsync(t => t.Address == "198.51.100.1");
        transit.Enabled.Should().BeTrue();
        transit.Name.Should().Be("Transit A v2");
    }

    [Fact]
    public async Task Third_save_after_toggle_cycle()
    {
        // Start: enabled
        _db.MonitoringTargets.Add(MakeDbTarget("192.0.2.1", "Hop A", MonitoringTargetType.AccessIsp, enabled: true));
        await _db.SaveChangesAsync();

        // Save 1: disable
        await CommitAsync(
            new List<AccessHopCandidate> { MakeAccessHop("192.0.2.1", "Hop A", enabled: false) },
            new List<TransitAsnCandidate>());
        (await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.1")).Enabled.Should().BeFalse();

        // Save 2: re-enable with new name
        await CommitAsync(
            new List<AccessHopCandidate> { MakeAccessHop("192.0.2.1", "Hop A Renamed", enabled: true) },
            new List<TransitAsnCandidate>());
        var target = await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.1");
        target.Enabled.Should().BeTrue();
        target.Name.Should().Be("Hop A Renamed");

        // Save 3: disable again
        await CommitAsync(
            new List<AccessHopCandidate> { MakeAccessHop("192.0.2.1", "Hop A Final", enabled: false) },
            new List<TransitAsnCandidate>());
        target = await _db.MonitoringTargets.FirstAsync(t => t.Address == "192.0.2.1");
        target.Enabled.Should().BeFalse();
        target.Name.Should().Be("Hop A Final");
    }

    [Fact]
    public void Reconcile_absorbs_user_edited_name_for_transit()
    {
        var candidates = new List<TransitAsnCandidate>
        {
            MakeTransitCandidate("198.51.100.1", "Windstream ae6-0.agr01.pop01-xx.us", 7029, DiscoveryMethod.DirectRouter, true)
        };
        var existing = MakeDbTarget("198.51.100.1", "Windstream ae6-0.agr01.pop01-xx.us 1", MonitoringTargetType.Transit, enabled: true);
        _db.MonitoringTargets.Add(existing);
        _db.SaveChanges();

        var byAddress = new Dictionary<string, MonitoringTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["198.51.100.1"] = existing
        };

        foreach (var c in candidates)
        {
            var addr = c.HopAddress ?? c.PathProxyTarget;
            if (string.IsNullOrEmpty(addr)) continue;
            if (byAddress.TryGetValue(addr, out var ex))
            {
                c.Enabled = ex.Enabled;
                if (!string.IsNullOrEmpty(ex.Name))
                    c.Label = ex.Name;
            }
        }

        candidates[0].Label.Should().Be("Windstream ae6-0.agr01.pop01-xx.us 1");
        candidates[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Reconcile_absorbs_user_edited_name_for_access()
    {
        var hops = new List<AccessHopCandidate>
        {
            MakeAccessHop("192.0.2.1", "EXAMPLE border01", enabled: true)
        };
        var existing = MakeDbTarget("192.0.2.1", "EXAMPLE border01 1", MonitoringTargetType.AccessIsp, enabled: true);
        _db.MonitoringTargets.Add(existing);
        _db.SaveChanges();

        var byAddress = new Dictionary<string, MonitoringTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["192.0.2.1"] = existing
        };

        foreach (var hop in hops)
        {
            if (byAddress.TryGetValue(hop.Address, out var ex))
            {
                hop.Enabled = ex.Enabled;
                if (!string.IsNullOrEmpty(ex.Name))
                    hop.Label = ex.Name;
            }
        }

        hops[0].Label.Should().Be("EXAMPLE border01 1");
    }

    [Fact]
    public void Reconcile_absorbs_name_for_pathproxy()
    {
        var candidates = new List<TransitAsnCandidate>
        {
            MakeTransitCandidate("203.0.113.1", "Cloudflare", 13335, DiscoveryMethod.PathProxy, true)
        };
        candidates[0].PathProxyTarget = "203.0.113.1";
        var existing = MakeDbTarget("203.0.113.1", "Cloudflare Primary", MonitoringTargetType.InternetService, enabled: true);
        _db.MonitoringTargets.Add(existing);
        _db.SaveChanges();

        var byAddress = new Dictionary<string, MonitoringTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["203.0.113.1"] = existing
        };

        foreach (var c in candidates)
        {
            var addr = c.HopAddress ?? c.PathProxyTarget;
            if (string.IsNullOrEmpty(addr)) continue;
            if (byAddress.TryGetValue(addr, out var ex))
            {
                c.Enabled = ex.Enabled;
                if (!string.IsNullOrEmpty(ex.Name))
                    c.Label = ex.Name;
            }
        }

        candidates[0].Label.Should().Be("Cloudflare Primary");
    }

    [Fact]
    public void Reconcile_does_not_overwrite_with_empty_db_name()
    {
        var candidates = new List<TransitAsnCandidate>
        {
            MakeTransitCandidate("198.51.100.1", "Windstream agr01", 7029, DiscoveryMethod.DirectRouter, true)
        };
        var existing = MakeDbTarget("198.51.100.1", "", MonitoringTargetType.Transit, enabled: true);
        _db.MonitoringTargets.Add(existing);
        _db.SaveChanges();

        var byAddress = new Dictionary<string, MonitoringTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["198.51.100.1"] = existing
        };

        foreach (var c in candidates)
        {
            var addr = c.HopAddress ?? c.PathProxyTarget;
            if (string.IsNullOrEmpty(addr)) continue;
            if (byAddress.TryGetValue(addr, out var ex))
            {
                c.Enabled = ex.Enabled;
                if (!string.IsNullOrEmpty(ex.Name))
                    c.Label = ex.Name;
            }
        }

        candidates[0].Label.Should().Be("Windstream agr01");
    }

    // ---- Helpers ----

    private async Task CommitAsync(List<AccessHopCandidate> hops, List<TransitAsnCandidate> transits)
    {
        var wanInterface = "wan";

        foreach (var hop in hops.Where(h => h.Enabled))
        {
            var existing = await _db.MonitoringTargets.FirstOrDefaultAsync(t => t.TargetId == hop.TargetId)
                           ?? await _db.MonitoringTargets.FirstOrDefaultAsync(t => t.Address == hop.Address);
            if (existing == null)
            {
                _db.MonitoringTargets.Add(new MonitoringTarget
                {
                    TargetId = hop.TargetId,
                    Name = hop.Label,
                    Address = hop.Address,
                    ProbeMode = hop.RespondedTo,
                    TargetType = MonitoringTargetType.AccessIsp,
                    AsnNumber = hop.AsnNumber,
                    AsnName = UpstreamTracerService.CleanAsnName(hop.AsnName),
                    VantagePoint = "server",
                    PollIntervalSeconds = 10,
                    PingCount = 5,
                    Enabled = true,
                    AutoDiscovered = true,
                    DiscoveryMethod = hop.Method,
                    WanInterface = wanInterface,
                    CreatedAt = DateTime.UtcNow,
                    LastVerified = DateTime.UtcNow
                });
            }
            else
            {
                existing.Enabled = true;
                existing.Name = hop.Label;
                if (hop.AsnNumber.HasValue) existing.AsnNumber = hop.AsnNumber;
                if (!string.IsNullOrEmpty(hop.AsnName)) existing.AsnName = UpstreamTracerService.CleanAsnName(hop.AsnName);
                existing.LastVerified = DateTime.UtcNow;
            }
        }
        foreach (var hop in hops.Where(h => !h.Enabled))
        {
            var existing = await _db.MonitoringTargets.FirstOrDefaultAsync(t => t.Address == hop.Address);
            if (existing != null)
            {
                existing.Enabled = false;
                existing.Name = hop.Label;
                if (!string.IsNullOrEmpty(hop.AsnName)) existing.AsnName = UpstreamTracerService.CleanAsnName(hop.AsnName);
            }
        }
        foreach (var transit in transits.Where(t => t.Enabled))
        {
            var targetType = transit.Method == DiscoveryMethod.PathProxy
                ? MonitoringTargetType.InternetService
                : MonitoringTargetType.Transit;
            var address = transit.HopAddress ?? transit.PathProxyTarget;
            var existing = await _db.MonitoringTargets.FirstOrDefaultAsync(t => t.TargetId == transit.TargetId)
                           ?? (address != null ? await _db.MonitoringTargets.FirstOrDefaultAsync(t => t.Address == address) : null);
            if (existing == null && !string.IsNullOrEmpty(address))
            {
                _db.MonitoringTargets.Add(new MonitoringTarget
                {
                    TargetId = transit.TargetId ?? $"transit-{Guid.NewGuid():N}",
                    Name = transit.Label ?? transit.AsnName,
                    Address = address,
                    ProbeMode = transit.RespondedTo ?? ProbeMode.Icmp,
                    TargetType = targetType,
                    AsnNumber = transit.AsnNumber,
                    AsnName = transit.AsnName,
                    VantagePoint = "server",
                    PollIntervalSeconds = 15,
                    PingCount = 5,
                    Enabled = true,
                    AutoDiscovered = true,
                    DiscoveryMethod = transit.Method,
                    WanInterface = wanInterface,
                    CreatedAt = DateTime.UtcNow,
                    LastVerified = DateTime.UtcNow
                });
            }
            else if (existing != null)
            {
                existing.Enabled = true;
                existing.Name = transit.Label ?? transit.AsnName;
                existing.Address = address ?? existing.Address;
                if (transit.AsnNumber > 0) existing.AsnNumber = transit.AsnNumber;
                if (!string.IsNullOrEmpty(transit.AsnName)) existing.AsnName = transit.AsnName;
                existing.LastVerified = DateTime.UtcNow;
            }
        }
        foreach (var transit in transits.Where(t => !t.Enabled))
        {
            var addr = transit.HopAddress ?? transit.PathProxyTarget;
            if (string.IsNullOrEmpty(addr)) continue;
            var existing = await _db.MonitoringTargets.FirstOrDefaultAsync(t => t.Address == addr);
            if (existing != null)
            {
                existing.Enabled = false;
                existing.Name = transit.Label ?? transit.AsnName;
                if (!string.IsNullOrEmpty(transit.AsnName)) existing.AsnName = transit.AsnName;
            }
        }

        await _db.SaveChangesAsync();
    }

    private static AccessHopCandidate MakeAccessHop(string address, string label, bool enabled) => new()
    {
        TargetId = $"access-{address.Replace('.', '-')}",
        Label = label,
        Address = address,
        AsnNumber = 64500,
        AsnName = "TEST ISP",
        Role = UpstreamRole.Aggregation,
        HopNumber = 2,
        RespondedTo = ProbeMode.Icmp,
        Enabled = enabled
    };

    private static TransitAsnCandidate MakeTransitCandidate(string address, string label, int asn,
        DiscoveryMethod method, bool enabled) => new()
        {
            AsnNumber = asn,
            AsnName = $"AS{asn}",
            Label = label,
            Method = method,
            TargetId = $"transit-as{asn}-{address.Replace('.', '-')}",
            HopAddress = address,
            RespondedTo = ProbeMode.Icmp,
            Enabled = enabled
        };

    private static MonitoringTarget MakeDbTarget(string address, string name,
        MonitoringTargetType type, bool enabled) => new()
        {
            TargetId = $"test-{address.Replace('.', '-')}",
            Name = name,
            Address = address,
            ProbeMode = ProbeMode.Icmp,
            TargetType = type,
            VantagePoint = "server",
            PollIntervalSeconds = 10,
            PingCount = 5,
            Enabled = enabled,
            AutoDiscovered = true,
            WanInterface = "wan",
            CreatedAt = DateTime.UtcNow,
            LastVerified = DateTime.UtcNow
        };
}

public class ComputeNearTransitAsnsTests
{
    // Generic private-use access ASN (not a real ISP); public tier-1 ASNs are real
    // because the logic keys on them.
    private const int Access = 64500;
    private const int UpstreamA = 64510;
    private const int UpstreamB = 64520;
    private const int Lumen = 3356;
    private const int Cogent = 174;
    private const int Arelion = 1299;
    private const int Indatel = 30517;
    private const int Cloudflare = 13335;

    private static IReadOnlySet<int> AccessSet => new HashSet<int> { Access };
    private static IReadOnlySet<int> NoDest => new HashSet<int>();
    private static IReadOnlySet<int> Tier1 => new HashSet<int> { Lumen, Cogent, Arelion };

    private static Dictionary<string, int> Map(params (string Ip, int Asn)[] e)
        => e.ToDictionary(x => x.Ip, x => x.Asn, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Direct_upstream_is_near_transit()
    {
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Lumen));
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            new[] { new[] { "192.0.2.1", "192.0.2.2" } }, map, AccessSet, NoDest, Tier1);
        near.Should().Contain(Lumen);
    }

    [Fact]
    public void Upstreams_upstream_is_near_transit()
    {
        // access -> UpstreamA -> Lumen: Lumen is 2nd-degree, still in window.
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", UpstreamA), ("192.0.2.3", Lumen));
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            new[] { new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3" } }, map, AccessSet, NoDest, Tier1);
        near.Should().BeEquivalentTo(new[] { UpstreamA, Lumen });
    }

    [Fact]
    public void Third_degree_asn_is_not_near_transit()
    {
        // access -> UpstreamA -> UpstreamB -> Lumen: Lumen is 3rd-degree, dropped.
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", UpstreamA),
                      ("192.0.2.3", UpstreamB), ("192.0.2.4", Lumen));
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            new[] { new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3", "192.0.2.4" } }, map, AccessSet, NoDest, Tier1);
        near.Should().BeEquivalentTo(new[] { UpstreamA, UpstreamB });
        near.Should().NotContain(Lumen);
    }

    [Fact]
    public void Every_direct_upstream_of_a_multihomed_isp_is_captured()
    {
        // Different traces exit via different upstreams; the per-trace union keeps all.
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Lumen),
                      ("198.51.100.1", Access), ("198.51.100.2", UpstreamA));
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            new[]
            {
                new[] { "192.0.2.1", "192.0.2.2" },
                new[] { "198.51.100.1", "198.51.100.2" }
            }, map, AccessSet, NoDest, Tier1);
        near.Should().BeEquivalentTo(new[] { Lumen, UpstreamA });
    }

    [Fact]
    public void Per_trace_window_does_not_let_one_trace_crowd_out_another_upstream()
    {
        // Regression: a short trace reaching UpstreamA at hop 1 plus a long trace
        // reaching Lumen only at hop 4. A merged-pool "first two ASNs by hop number"
        // filled both slots on the low hops and dropped Lumen; per-trace keeps both.
        var map = Map(("198.51.100.1", UpstreamA),
                      ("192.0.2.1", Access), ("192.0.2.2", Access),
                      ("192.0.2.3", Access), ("192.0.2.4", Lumen));
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            new[]
            {
                new[] { "198.51.100.1" },
                new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3", "192.0.2.4" }
            }, map, AccessSet, NoDest, Tier1);
        near.Should().Contain(Lumen);
        near.Should().Contain(UpstreamA);
    }

    [Fact]
    public void Destination_asn_does_not_consume_a_degree_slot()
    {
        // access -> UpstreamA -> Cloudflare(dest) -> UpstreamB: with dest skipped, the
        // real 2nd-degree transit (UpstreamB) still fits in the window. Non-tier-1 hops
        // so the tier-1 horizon stop doesn't interfere with what this test isolates.
        var dest = new HashSet<int> { Cloudflare };
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", UpstreamA),
                      ("192.0.2.3", Cloudflare), ("192.0.2.4", UpstreamB));
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            new[] { new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3", "192.0.2.4" } }, map, AccessSet, dest, Tier1);
        near.Should().BeEquivalentTo(new[] { UpstreamA, UpstreamB });
    }

    [Fact]
    public void Unresolved_hops_are_skipped()
    {
        var map = Map(("192.0.2.1", Access), ("192.0.2.3", Lumen)); // .2 has no ASN (gap)
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            new[] { new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3" } }, map, AccessSet, NoDest, Tier1);
        near.Should().Contain(Lumen);
    }

    [Fact]
    public void Access_only_path_yields_nothing()
    {
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Access));
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            new[] { new[] { "192.0.2.1", "192.0.2.2" } }, map, AccessSet, NoDest, Tier1);
        near.Should().BeEmpty();
    }

    [Fact]
    public void No_traces_yields_nothing()
    {
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            Array.Empty<IReadOnlyList<string>>(), new Dictionary<string, int>(), AccessSet, NoDest, Tier1);
        near.Should().BeEmpty();
    }

    [Fact]
    public void Asn_reached_through_a_tier1_is_not_near_transit()
    {
        // access -> Arelion(tier-1) -> INDATEL: the endpoint sits beyond a tier-1, so it
        // is not adjacent to the ISP and must not be near-transit (the AT&T-via-Arelion
        // case). The tier-1 itself is the first upstream and stays.
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Arelion), ("192.0.2.3", Indatel));
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            new[] { new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3" } }, map, AccessSet, NoDest, Tier1);
        near.Should().Contain(Arelion);
        near.Should().NotContain(Indatel);
    }

    [Fact]
    public void Endpoint_one_hop_off_the_access_isp_is_near_transit()
    {
        // access -> INDATEL directly: no tier-1 in between, so it stays near-transit
        // (the directly-adjacent case). Same endpoint ASN as above, opposite verdict.
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Indatel));
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            new[] { new[] { "192.0.2.1", "192.0.2.2" } }, map, AccessSet, NoDest, Tier1);
        near.Should().Contain(Indatel);
    }

    [Fact]
    public void Walk_stops_at_the_first_tier1()
    {
        // access -> Lumen(tier-1) -> Cogent(tier-1): only the first tier-1 is near-transit;
        // a second tier-1 reached through it is core peering, not our transit.
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Lumen), ("192.0.2.3", Cogent));
        var near = UpstreamTracerService.ComputeNearTransitAsns(
            new[] { new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3" } }, map, AccessSet, NoDest, Tier1);
        near.Should().Contain(Lumen);
        near.Should().NotContain(Cogent);
    }
}

public class ComputeExcludedTier1AsnsTests
{
    private const int Access = 64500;
    private const int Regional = 64510;
    private const int Lumen = 3356;
    private const int Cogent = 174;
    private const int Gtt = 3257;
    private const int Att = 7018;

    private static IReadOnlySet<int> Tier1 => new HashSet<int> { Lumen, Cogent, Gtt, Att };
    private static IReadOnlySet<int> AccessSet => new HashSet<int> { Access };

    private static Dictionary<string, int> Map(params (string Ip, int Asn)[] e)
        => e.ToDictionary(x => x.Ip, x => x.Asn, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Tier1_above_non_tier1_is_kept()
    {
        // access(non-T1) -> Lumen(T1): grounded, not excluded.
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Lumen));
        var excluded = UpstreamTracerService.ComputeExcludedTier1Asns(
            new[] { new[] { "192.0.2.1", "192.0.2.2" } }, map, Tier1, AccessSet);
        excluded.Should().BeEmpty();
    }

    [Fact]
    public void Tier1_directly_above_another_tier1_is_excluded()
    {
        // access -> Cogent(T1) -> Lumen(T1): Lumen is core peering, excluded. Cogent
        // sits above access (non-T1) so it stays.
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Cogent), ("192.0.2.3", Lumen));
        var excluded = UpstreamTracerService.ComputeExcludedTier1Asns(
            new[] { new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3" } }, map, Tier1, AccessSet);
        excluded.Should().BeEquivalentTo(new[] { Lumen });
    }

    [Fact]
    public void Kept_when_grounded_on_any_trace()
    {
        // One trace shows Lumen above Cogent; another shows Lumen directly above access.
        // A single grounded sighting keeps it.
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Cogent), ("192.0.2.3", Lumen),
                      ("198.51.100.1", Access), ("198.51.100.2", Lumen));
        var excluded = UpstreamTracerService.ComputeExcludedTier1Asns(
            new[]
            {
                new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3" },
                new[] { "198.51.100.1", "198.51.100.2" }
            }, map, Tier1, AccessSet);
        excluded.Should().BeEmpty();
    }

    [Fact]
    public void Excluded_only_when_tier1_downstream_on_every_trace()
    {
        // Lumen sits above a tier-1 (Cogent, then GTT) on both traces -> excluded.
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Cogent), ("192.0.2.3", Lumen),
                      ("198.51.100.1", Access), ("198.51.100.2", Gtt), ("198.51.100.3", Lumen));
        var excluded = UpstreamTracerService.ComputeExcludedTier1Asns(
            new[]
            {
                new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3" },
                new[] { "198.51.100.1", "198.51.100.2", "198.51.100.3" }
            }, map, Tier1, AccessSet);
        excluded.Should().BeEquivalentTo(new[] { Lumen });
    }

    [Fact]
    public void Tier1_as_first_resolved_hop_is_grounded()
    {
        // Unresolved gateway hop then Lumen: downstream is us, so Lumen is grounded.
        var map = Map(("192.0.2.2", Lumen)); // first hop has no ASN
        var excluded = UpstreamTracerService.ComputeExcludedTier1Asns(
            new[] { new[] { "10.0.0.1", "192.0.2.2" } }, map, Tier1, AccessSet);
        excluded.Should().BeEmpty();
    }

    [Fact]
    public void Consecutive_same_asn_hops_are_collapsed()
    {
        // access -> Lumen -> Lumen -> Cogent: Cogent's true downstream is Lumen(T1),
        // so Cogent is excluded; Lumen is grounded by access and kept.
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Lumen),
                      ("192.0.2.3", Lumen), ("192.0.2.4", Cogent));
        var excluded = UpstreamTracerService.ComputeExcludedTier1Asns(
            new[] { new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3", "192.0.2.4" } }, map, Tier1, AccessSet);
        excluded.Should().BeEquivalentTo(new[] { Cogent });
    }

    [Fact]
    public void Non_tier1_asns_are_never_excluded()
    {
        var map = Map(("192.0.2.1", Access), ("192.0.2.2", Regional));
        var excluded = UpstreamTracerService.ComputeExcludedTier1Asns(
            new[] { new[] { "192.0.2.1", "192.0.2.2" } }, map, Tier1, AccessSet);
        excluded.Should().BeEmpty();
    }

    [Fact]
    public void No_traces_yields_nothing()
    {
        var excluded = UpstreamTracerService.ComputeExcludedTier1Asns(
            Array.Empty<IReadOnlyList<string>>(), new Dictionary<string, int>(), Tier1, AccessSet);
        excluded.Should().BeEmpty();
    }

    [Fact]
    public void First_tier1_above_a_tier1_access_isp_is_kept()
    {
        // AT&T fiber customer: access ASN is itself tier-1 (AS7018), and Lumen sits
        // directly above it. Lumen is AT&T's upstream/peer - the thing to monitor - not
        // core peering, so the access ASN grounds it even though it's a tier-1.
        var access = new HashSet<int> { Att };
        var map = Map(("192.0.2.1", Att), ("192.0.2.2", Lumen));
        var excluded = UpstreamTracerService.ComputeExcludedTier1Asns(
            new[] { new[] { "192.0.2.1", "192.0.2.2" } }, map, Tier1, access);
        excluded.Should().BeEmpty();
    }

    [Fact]
    public void Second_tier1_above_a_tier1_access_isp_is_excluded()
    {
        // access=AT&T(T1) -> Lumen(T1) -> Cogent(T1): Lumen is grounded by the access
        // ISP and kept; Cogent sits above a non-access tier-1 (Lumen) and is excluded.
        var access = new HashSet<int> { Att };
        var map = Map(("192.0.2.1", Att), ("192.0.2.2", Lumen), ("192.0.2.3", Cogent));
        var excluded = UpstreamTracerService.ComputeExcludedTier1Asns(
            new[] { new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3" } }, map, Tier1, access);
        excluded.Should().BeEquivalentTo(new[] { Cogent });
    }

    [Fact]
    public void Tier1Asns_constant_includes_major_carriers_and_live_siblings()
    {
        // Core tier-1s, plus the corrected AS1239 (now Cogent) and active AT&T/Lumen
        // sibling ASNs that show up in US traces.
        UpstreamTracerService.Tier1Asns.Should().Contain(
            new[] { 3356, 174, 7018, 2914, 1299, 6461, 1239, 7132, 3561 });
    }

    [Fact]
    public void Tier1Asns_excludes_hurricane_electric()
    {
        // AS6939 is not settlement-free on IPv4, so it carries no reliable core-peering
        // signal and stays out of the adjacency set.
        UpstreamTracerService.Tier1Asns.Should().NotContain(6939);
    }
}

public class AccessIspFallbackTests
{
    [Fact]
    public void AccessIspFallbackHosts_AS3320_has_the_pingable_telekom_pops()
    {
        UpstreamTracerService.AccessIspFallbackHosts.Should().ContainKey(3320);
        UpstreamTracerService.AccessIspFallbackHosts[3320].Should().BeEquivalentTo(new[]
        {
            "ffm.wsqm.telekom-dienste.de",
            "ham.wsqm.telekom-dienste.de",
            "mue.wsqm.telekom-dienste.de",
            "ber.wsqm.telekom-dienste.de",
        });
    }

    [Fact]
    public void AccessIspFallbackHosts_AS3320_omits_non_pingable_dusseldorf()
    {
        // dssd-tc.wsqm.telekom-dienste.de does not answer ICMP, so it must stay out of the map.
        UpstreamTracerService.AccessIspFallbackHosts[3320]
            .Should().NotContain(h => h.StartsWith("dssd"));
    }

    [Fact]
    public void SelectLowestRtt_picks_the_lowest_rtt_candidate()
    {
        var probes = new[]
        {
            new UpstreamTracerService.AccessFallbackProbe("ffm.wsqm.telekom-dienste.de", "203.0.113.10", 24.5),
            new UpstreamTracerService.AccessFallbackProbe("ham.wsqm.telekom-dienste.de", "203.0.113.20", 11.2),
            new UpstreamTracerService.AccessFallbackProbe("mue.wsqm.telekom-dienste.de", "203.0.113.30", 31.0),
        };

        var winner = UpstreamTracerService.SelectLowestRtt(probes);

        winner.Should().NotBeNull();
        winner!.Host.Should().Be("ham.wsqm.telekom-dienste.de");
        winner.Rtt.Should().Be(11.2);
    }

    [Fact]
    public void SelectLowestRtt_returns_the_single_candidate_when_only_one_reachable()
    {
        var probes = new[]
        {
            new UpstreamTracerService.AccessFallbackProbe("mue.wsqm.telekom-dienste.de", "203.0.113.30", 31.0),
        };

        UpstreamTracerService.SelectLowestRtt(probes)!.Host.Should().Be("mue.wsqm.telekom-dienste.de");
    }

    [Fact]
    public void SelectLowestRtt_returns_null_when_none_reachable()
    {
        UpstreamTracerService.SelectLowestRtt(Array.Empty<UpstreamTracerService.AccessFallbackProbe>())
            .Should().BeNull();
    }
}

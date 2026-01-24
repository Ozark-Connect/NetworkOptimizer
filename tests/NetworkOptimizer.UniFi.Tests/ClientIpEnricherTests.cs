using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for ClientIpEnricher, which enriches client IPs from history data.
/// This is needed for UX/UX7 connected clients that don't have IPs in stat/sta (UniFi API bug).
/// </summary>
public class ClientIpEnricherTests
{
    #region BuildMacToIpLookup Tests

    [Fact]
    public void BuildMacToIpLookup_WithActiveClients_UsesIpField()
    {
        // Arrange - /clients/active returns 'ip' field
        var clients = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "aa:bb:cc:dd:ee:f1", Ip = "10.0.0.101" },
            new() { Mac = "aa:bb:cc:dd:ee:f2", Ip = "10.0.0.102" },
            new() { Mac = "aa:bb:cc:dd:ee:f3", Ip = "10.0.0.103" }
        };

        // Act
        var lookup = ClientIpEnricher.BuildMacToIpLookup(clients);

        // Assert
        lookup.Should().HaveCount(3);
        lookup["aa:bb:cc:dd:ee:f1"].Should().Be("10.0.0.101");
        lookup["aa:bb:cc:dd:ee:f2"].Should().Be("10.0.0.102");
        lookup["aa:bb:cc:dd:ee:f3"].Should().Be("10.0.0.103");
    }

    [Fact]
    public void BuildMacToIpLookup_WithHistoryClients_UsesLastIpField()
    {
        // Arrange - /clients/history returns 'last_ip' field
        var clients = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "aa:bb:cc:dd:ee:f1", LastIp = "10.0.0.101" },
            new() { Mac = "aa:bb:cc:dd:ee:f2", LastIp = "10.0.0.102" }
        };

        // Act
        var lookup = ClientIpEnricher.BuildMacToIpLookup(clients);

        // Assert
        lookup.Should().HaveCount(2);
        lookup["aa:bb:cc:dd:ee:f1"].Should().Be("10.0.0.101");
    }

    [Fact]
    public void BuildMacToIpLookup_PrefersIpOverLastIp()
    {
        // Arrange - BestIp should prefer 'ip' over 'last_ip'
        var clients = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "aa:bb:cc:dd:ee:ff", Ip = "10.0.0.100", LastIp = "10.0.0.200" }
        };

        // Act
        var lookup = ClientIpEnricher.BuildMacToIpLookup(clients);

        // Assert - should use 'ip' not 'last_ip'
        lookup["aa:bb:cc:dd:ee:ff"].Should().Be("10.0.0.100");
    }

    [Fact]
    public void BuildMacToIpLookup_IsCaseInsensitive()
    {
        // Arrange
        var clients = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "AA:BB:CC:DD:EE:FF", Ip = "10.0.0.100" }
        };

        // Act
        var lookup = ClientIpEnricher.BuildMacToIpLookup(clients);

        // Assert - should find with lowercase
        lookup.TryGetValue("aa:bb:cc:dd:ee:ff", out var ip).Should().BeTrue();
        ip.Should().Be("10.0.0.100");
    }

    [Fact]
    public void BuildMacToIpLookup_WithNullHistory_ReturnsEmptyLookup()
    {
        // Act
        var lookup = ClientIpEnricher.BuildMacToIpLookup(null!);

        // Assert
        lookup.Should().NotBeNull();
        lookup.Should().BeEmpty();
    }

    [Fact]
    public void BuildMacToIpLookup_WithEmptyHistory_ReturnsEmptyLookup()
    {
        // Arrange
        var history = new List<UniFiClientHistoryResponse>();

        // Act
        var lookup = ClientIpEnricher.BuildMacToIpLookup(history);

        // Assert
        lookup.Should().BeEmpty();
    }

    [Fact]
    public void BuildMacToIpLookup_SkipsEntriesWithNullMac()
    {
        // Arrange
        var history = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = null!, Ip = "10.0.0.100" },
            new() { Mac = "aa:bb:cc:dd:ee:ff", Ip = "10.0.0.101" }
        };

        // Act
        var lookup = ClientIpEnricher.BuildMacToIpLookup(history);

        // Assert
        lookup.Should().HaveCount(1);
        lookup.ContainsKey("aa:bb:cc:dd:ee:ff").Should().BeTrue();
    }

    [Fact]
    public void BuildMacToIpLookup_SkipsEntriesWithEmptyMac()
    {
        // Arrange
        var history = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "", Ip = "10.0.0.100" },
            new() { Mac = "aa:bb:cc:dd:ee:ff", Ip = "10.0.0.101" }
        };

        // Act
        var lookup = ClientIpEnricher.BuildMacToIpLookup(history);

        // Assert
        lookup.Should().HaveCount(1);
    }

    [Fact]
    public void BuildMacToIpLookup_SkipsEntriesWithNullLastIp()
    {
        // Arrange
        var history = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "aa:bb:cc:dd:ee:f1", Ip = null },
            new() { Mac = "aa:bb:cc:dd:ee:f2", Ip = "10.0.0.102" }
        };

        // Act
        var lookup = ClientIpEnricher.BuildMacToIpLookup(history);

        // Assert
        lookup.Should().HaveCount(1);
        lookup.ContainsKey("aa:bb:cc:dd:ee:f2").Should().BeTrue();
    }

    [Fact]
    public void BuildMacToIpLookup_SkipsEntriesWithEmptyLastIp()
    {
        // Arrange
        var history = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "aa:bb:cc:dd:ee:f1", Ip = "" },
            new() { Mac = "aa:bb:cc:dd:ee:f2", Ip = "10.0.0.102" }
        };

        // Act
        var lookup = ClientIpEnricher.BuildMacToIpLookup(history);

        // Assert
        lookup.Should().HaveCount(1);
    }

    [Fact]
    public void BuildMacToIpLookup_WithDuplicateMacs_UsesFirst()
    {
        // Arrange - same MAC with different IPs (maybe IP changed)
        var history = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "aa:bb:cc:dd:ee:ff", Ip = "10.0.0.100" },
            new() { Mac = "aa:bb:cc:dd:ee:ff", Ip = "10.0.0.101" }
        };

        // Act
        var lookup = ClientIpEnricher.BuildMacToIpLookup(history);

        // Assert - should use first entry
        lookup.Should().HaveCount(1);
        lookup["aa:bb:cc:dd:ee:ff"].Should().Be("10.0.0.100");
    }

    #endregion

    #region GetEnrichedIp Tests

    [Fact]
    public void GetEnrichedIp_WithPrimaryIp_ReturnsPrimaryIp()
    {
        // Arrange
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aa:bb:cc:dd:ee:ff"] = "10.0.0.200"
        };

        // Act - primary IP takes precedence
        var result = ClientIpEnricher.GetEnrichedIp("10.0.0.100", "aa:bb:cc:dd:ee:ff", lookup);

        // Assert
        result.Should().Be("10.0.0.100");
    }

    [Fact]
    public void GetEnrichedIp_WithNullPrimaryIp_ReturnsHistoryIp()
    {
        // Arrange - simulates UX/UX7 client without IP in stat/sta
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aa:bb:cc:dd:ee:ff"] = "10.0.0.100"
        };

        // Act
        var result = ClientIpEnricher.GetEnrichedIp(null, "aa:bb:cc:dd:ee:ff", lookup);

        // Assert
        result.Should().Be("10.0.0.100");
    }

    [Fact]
    public void GetEnrichedIp_WithEmptyPrimaryIp_ReturnsHistoryIp()
    {
        // Arrange - simulates UX/UX7 client with empty IP in stat/sta
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aa:bb:cc:dd:ee:ff"] = "10.0.0.100"
        };

        // Act
        var result = ClientIpEnricher.GetEnrichedIp("", "aa:bb:cc:dd:ee:ff", lookup);

        // Assert
        result.Should().Be("10.0.0.100");
    }

    [Fact]
    public void GetEnrichedIp_WithNoPrimaryIpAndNoHistoryMatch_ReturnsNull()
    {
        // Arrange - new client not in history
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aa:bb:cc:dd:ee:f1"] = "10.0.0.101"
        };

        // Act
        var result = ClientIpEnricher.GetEnrichedIp(null, "aa:bb:cc:dd:ee:ff", lookup);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetEnrichedIp_WithNullMac_ReturnsNull()
    {
        // Arrange
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aa:bb:cc:dd:ee:ff"] = "10.0.0.100"
        };

        // Act
        var result = ClientIpEnricher.GetEnrichedIp(null, null, lookup);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetEnrichedIp_WithEmptyMac_ReturnsNull()
    {
        // Arrange
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aa:bb:cc:dd:ee:ff"] = "10.0.0.100"
        };

        // Act
        var result = ClientIpEnricher.GetEnrichedIp(null, "", lookup);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetEnrichedIp_MacLookupIsCaseInsensitive()
    {
        // Arrange
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AA:BB:CC:DD:EE:FF"] = "10.0.0.100"
        };

        // Act - lowercase MAC should match uppercase in lookup
        var result = ClientIpEnricher.GetEnrichedIp(null, "aa:bb:cc:dd:ee:ff", lookup);

        // Assert
        result.Should().Be("10.0.0.100");
    }

    #endregion

    #region Real-World Scenario Tests

    [Fact]
    public void UxClientWithoutIp_GetsIpFromHistory()
    {
        // Arrange - simulates the exact scenario from GitHub issue #141
        // UX/UX7 connected client has MAC but no IP in stat/sta response
        var history = new List<UniFiClientHistoryResponse>
        {
            // This client is connected via UX and has IP in history but not in stat/sta
            new()
            {
                Mac = "00:5b:94:a8:50:a1",
                Ip = "10.0.0.137",
                DisplayName = "TestDevice"
            },
            // Other clients that work normally
            new()
            {
                Mac = "aa:bb:cc:dd:ee:ff",
                Ip = "10.0.0.141",
                DisplayName = "NormalClient"
            }
        };

        var lookup = ClientIpEnricher.BuildMacToIpLookup(history);

        // Act - UX client has null IP in stat/sta
        var uxClientIp = ClientIpEnricher.GetEnrichedIp(null, "00:5b:94:a8:50:a1", lookup);

        // Normal client has IP in stat/sta
        var normalClientIp = ClientIpEnricher.GetEnrichedIp("10.0.0.141", "aa:bb:cc:dd:ee:ff", lookup);

        // Assert
        uxClientIp.Should().Be("10.0.0.137");
        normalClientIp.Should().Be("10.0.0.141");
    }

    [Fact]
    public void MultipleUxClientsWithoutIps_AllGetEnrichedFromHistory()
    {
        // Arrange - multiple clients connected via UX
        var history = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "00:11:22:33:44:01", Ip = "10.0.0.101" },
            new() { Mac = "00:11:22:33:44:02", Ip = "10.0.0.102" },
            new() { Mac = "00:11:22:33:44:03", Ip = "10.0.0.103" }
        };

        var lookup = ClientIpEnricher.BuildMacToIpLookup(history);

        // Act - all UX clients have null IPs in stat/sta
        var ip1 = ClientIpEnricher.GetEnrichedIp(null, "00:11:22:33:44:01", lookup);
        var ip2 = ClientIpEnricher.GetEnrichedIp("", "00:11:22:33:44:02", lookup);
        var ip3 = ClientIpEnricher.GetEnrichedIp(null, "00:11:22:33:44:03", lookup);

        // Assert
        ip1.Should().Be("10.0.0.101");
        ip2.Should().Be("10.0.0.102");
        ip3.Should().Be("10.0.0.103");
    }

    [Fact]
    public void MixedClients_SomeWithIpsSomeWithout()
    {
        // Arrange - realistic scenario with mixed clients
        var history = new List<UniFiClientHistoryResponse>
        {
            // UX clients
            new() { Mac = "ux:cl:ie:nt:00:01", Ip = "10.0.0.50" },
            new() { Mac = "ux:cl:ie:nt:00:02", Ip = "10.0.0.51" },
            // Normal clients
            new() { Mac = "no:rm:al:cl:ie:01", Ip = "10.0.0.100" },
            new() { Mac = "no:rm:al:cl:ie:02", Ip = "10.0.0.101" }
        };

        var lookup = ClientIpEnricher.BuildMacToIpLookup(history);

        // Act
        // UX clients don't have IP in stat/sta
        var uxIp1 = ClientIpEnricher.GetEnrichedIp(null, "ux:cl:ie:nt:00:01", lookup);
        var uxIp2 = ClientIpEnricher.GetEnrichedIp("", "ux:cl:ie:nt:00:02", lookup);

        // Normal clients have IP in stat/sta (takes precedence)
        var normalIp1 = ClientIpEnricher.GetEnrichedIp("10.0.0.100", "no:rm:al:cl:ie:01", lookup);
        var normalIp2 = ClientIpEnricher.GetEnrichedIp("10.0.0.101", "no:rm:al:cl:ie:02", lookup);

        // Assert
        uxIp1.Should().Be("10.0.0.50");
        uxIp2.Should().Be("10.0.0.51");
        normalIp1.Should().Be("10.0.0.100");
        normalIp2.Should().Be("10.0.0.101");
    }

    [Fact]
    public void ClientNotInHistory_ReturnsNullWhenNoStatStaIp()
    {
        // Arrange - brand new client not yet in history
        var history = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "ex:is:ti:ng:cl:01", Ip = "10.0.0.100" }
        };

        var lookup = ClientIpEnricher.BuildMacToIpLookup(history);

        // Act - new client without IP and not in history
        var ip = ClientIpEnricher.GetEnrichedIp(null, "ne:wc:li:en:t0:01", lookup);

        // Assert
        ip.Should().BeNull();
    }

    #endregion
}

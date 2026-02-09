using FluentAssertions;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class WiFiAnalysisHelpersTests
{
    public class SortByIp
    {
        [Fact]
        public void ReturnsEmptyList_WhenInputIsEmpty()
        {
            var result = WiFiAnalysisHelpers.SortByIp(new List<AccessPointSnapshot>());
            result.Should().BeEmpty();
        }

        [Fact]
        public void ReturnsSingleAp_WhenOnlyOneAp()
        {
            var ap = CreateAp("AP1", "192.168.1.1");
            var result = WiFiAnalysisHelpers.SortByIp(new[] { ap });
            result.Should().ContainSingle().Which.Should().Be(ap);
        }

        [Fact]
        public void SortsNumericallNotLexicographically()
        {
            // Lexicographic: 192.168.1.10 < 192.168.1.2 (because "1" < "2")
            // Numeric: 192.168.1.2 < 192.168.1.10
            var ap2 = CreateAp("AP2", "192.168.1.2");
            var ap10 = CreateAp("AP10", "192.168.1.10");

            var result = WiFiAnalysisHelpers.SortByIp(new[] { ap10, ap2 });

            result[0].Ip.Should().Be("192.168.1.2");
            result[1].Ip.Should().Be("192.168.1.10");
        }

        [Fact]
        public void SortsAcrossOctets()
        {
            var ap1 = CreateAp("AP1", "10.0.0.1");
            var ap2 = CreateAp("AP2", "192.168.1.1");
            var ap3 = CreateAp("AP3", "172.16.0.1");

            var result = WiFiAnalysisHelpers.SortByIp(new[] { ap2, ap1, ap3 });

            result[0].Ip.Should().Be("10.0.0.1");
            result[1].Ip.Should().Be("172.16.0.1");
            result[2].Ip.Should().Be("192.168.1.1");
        }

        [Fact]
        public void PlacesNullIpsAtEnd()
        {
            var apWithIp = CreateAp("AP1", "192.168.1.1");
            var apNullIp = CreateAp("AP2", null);

            var result = WiFiAnalysisHelpers.SortByIp(new[] { apNullIp, apWithIp });

            result[0].Should().Be(apWithIp);
            result[1].Should().Be(apNullIp);
        }

        [Fact]
        public void PlacesInvalidIpsAtEnd()
        {
            var apWithIp = CreateAp("AP1", "192.168.1.1");
            var apInvalidIp = CreateAp("AP2", "not-an-ip");

            var result = WiFiAnalysisHelpers.SortByIp(new[] { apInvalidIp, apWithIp });

            result[0].Should().Be(apWithIp);
            result[1].Should().Be(apInvalidIp);
        }

        [Fact]
        public void PlacesIpv6AtEnd()
        {
            var apV4 = CreateAp("AP1", "192.168.1.1");
            var apV6 = CreateAp("AP2", "2001:db8::1");

            var result = WiFiAnalysisHelpers.SortByIp(new[] { apV6, apV4 });

            result[0].Should().Be(apV4);
            result[1].Should().Be(apV6);
        }

        private static AccessPointSnapshot CreateAp(string name, string? ip) => new()
        {
            Name = name,
            Ip = ip ?? "",
            Mac = "00:11:22:33:44:55"
        };
    }

    public class FilterOutMeshPairs
    {
        [Fact]
        public void ReturnsSameList_WhenLessThanTwoAps()
        {
            var ap = CreateAp("AP1", "aa:bb:cc:dd:ee:01");
            var input = new List<AccessPointSnapshot> { ap };

            var result = WiFiAnalysisHelpers.FilterOutMeshPairs(input, RadioBand.Band5GHz, 36);

            result.Should().ContainSingle().Which.Should().Be(ap);
        }

        [Fact]
        public void ReturnsEmptyList_WhenInputIsEmpty()
        {
            var result = WiFiAnalysisHelpers.FilterOutMeshPairs(
                new List<AccessPointSnapshot>(), RadioBand.Band5GHz, 36);

            result.Should().BeEmpty();
        }

        [Fact]
        public void ReturnsAllAps_WhenNoMeshRelationships()
        {
            var ap1 = CreateAp("AP1", "aa:bb:cc:dd:ee:01");
            var ap2 = CreateAp("AP2", "aa:bb:cc:dd:ee:02");
            var input = new List<AccessPointSnapshot> { ap1, ap2 };

            var result = WiFiAnalysisHelpers.FilterOutMeshPairs(input, RadioBand.Band5GHz, 36);

            result.Should().HaveCount(2);
            result.Should().Contain(ap1);
            result.Should().Contain(ap2);
        }

        [Fact]
        public void ReturnsEmptyList_WhenAllApsAreMeshPairs()
        {
            var parentMac = "aa:bb:cc:dd:ee:01";
            var childMac = "aa:bb:cc:dd:ee:02";

            var parent = CreateAp("Parent", parentMac);
            var child = CreateMeshChild("Child", childMac, parentMac, RadioBand.Band5GHz, 36);

            var input = new List<AccessPointSnapshot> { parent, child };

            var result = WiFiAnalysisHelpers.FilterOutMeshPairs(input, RadioBand.Band5GHz, 36);

            result.Should().BeEmpty();
        }

        [Fact]
        public void ReturnsNonMeshAps_WhenMixedWithMeshPairs()
        {
            var parentMac = "aa:bb:cc:dd:ee:01";
            var childMac = "aa:bb:cc:dd:ee:02";
            var standaloneMac = "aa:bb:cc:dd:ee:03";

            var parent = CreateAp("Parent", parentMac);
            var child = CreateMeshChild("Child", childMac, parentMac, RadioBand.Band5GHz, 36);
            var standalone = CreateAp("Standalone", standaloneMac);

            var input = new List<AccessPointSnapshot> { parent, child, standalone };

            var result = WiFiAnalysisHelpers.FilterOutMeshPairs(input, RadioBand.Band5GHz, 36);

            result.Should().ContainSingle().Which.Mac.Should().Be(standaloneMac);
        }

        [Fact]
        public void IsCaseInsensitiveForMacAddresses()
        {
            var parentMac = "AA:BB:CC:DD:EE:01"; // uppercase
            var childMac = "aa:bb:cc:dd:ee:02"; // lowercase

            var parent = CreateAp("Parent", parentMac);
            var child = CreateMeshChild("Child", childMac, parentMac.ToLowerInvariant(), RadioBand.Band5GHz, 36);

            var input = new List<AccessPointSnapshot> { parent, child };

            var result = WiFiAnalysisHelpers.FilterOutMeshPairs(input, RadioBand.Band5GHz, 36);

            result.Should().BeEmpty();
        }

        [Fact]
        public void DoesNotFilterMeshPairs_WhenBandDoesNotMatch()
        {
            var parentMac = "aa:bb:cc:dd:ee:01";
            var childMac = "aa:bb:cc:dd:ee:02";

            var parent = CreateAp("Parent", parentMac);
            var child = CreateMeshChild("Child", childMac, parentMac, RadioBand.Band2_4GHz, 6); // Different band

            var input = new List<AccessPointSnapshot> { parent, child };

            // Filtering for 5GHz should not affect 2.4GHz mesh pairs
            var result = WiFiAnalysisHelpers.FilterOutMeshPairs(input, RadioBand.Band5GHz, 36);

            result.Should().HaveCount(2);
        }

        [Fact]
        public void DoesNotFilterMeshPairs_WhenChannelDoesNotMatch()
        {
            var parentMac = "aa:bb:cc:dd:ee:01";
            var childMac = "aa:bb:cc:dd:ee:02";

            var parent = CreateAp("Parent", parentMac);
            var child = CreateMeshChild("Child", childMac, parentMac, RadioBand.Band5GHz, 44); // Different channel

            var input = new List<AccessPointSnapshot> { parent, child };

            // Filtering for channel 36 should not affect channel 44 mesh pairs
            var result = WiFiAnalysisHelpers.FilterOutMeshPairs(input, RadioBand.Band5GHz, 36);

            result.Should().HaveCount(2);
        }

        private static AccessPointSnapshot CreateAp(string name, string mac) => new()
        {
            Name = name,
            Mac = mac,
            Ip = "192.168.1.1",
            IsMeshChild = false
        };

        private static AccessPointSnapshot CreateMeshChild(
            string name,
            string mac,
            string parentMac,
            RadioBand uplinkBand,
            int uplinkChannel) => new()
        {
            Name = name,
            Mac = mac,
            Ip = "192.168.1.2",
            IsMeshChild = true,
            MeshParentMac = parentMac,
            MeshUplinkBand = uplinkBand,
            MeshUplinkChannel = uplinkChannel
        };
    }
}

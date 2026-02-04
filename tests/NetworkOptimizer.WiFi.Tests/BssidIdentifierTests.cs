using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class BssidIdentifierTests
{
    public class IdentifyByBssid
    {
        [Fact]
        public void ReturnsNull_WhenBssidIsNull()
        {
            BssidIdentifier.IdentifyByBssid(null).Should().BeNull();
        }

        [Fact]
        public void ReturnsNull_WhenBssidIsEmpty()
        {
            BssidIdentifier.IdentifyByBssid("").Should().BeNull();
        }

        [Fact]
        public void ReturnsXboxWiFiDirect_ForMatchingBssid_ColonSeparator()
        {
            BssidIdentifier.IdentifyByBssid("62:45:AA:BB:CC:DD").Should().Be("Xbox Wi-Fi Direct");
        }

        [Fact]
        public void ReturnsXboxWiFiDirect_ForMatchingBssid_DashSeparator()
        {
            BssidIdentifier.IdentifyByBssid("62-45-AA-BB-CC-DD").Should().Be("Xbox Wi-Fi Direct");
        }

        [Fact]
        public void ReturnsXboxWiFiDirect_ForMatchingBssid_NoSeparator()
        {
            BssidIdentifier.IdentifyByBssid("6245AABBCCDD").Should().Be("Xbox Wi-Fi Direct");
        }

        [Fact]
        public void ReturnsXboxWiFiDirect_ForMatchingBssid_DotSeparator()
        {
            BssidIdentifier.IdentifyByBssid("6245.AABB.CCDD").Should().Be("Xbox Wi-Fi Direct");
        }

        [Fact]
        public void ReturnsXboxWiFiDirect_ForMatchingBssid_LowerCase()
        {
            BssidIdentifier.IdentifyByBssid("62:45:aa:bb:cc:dd").Should().Be("Xbox Wi-Fi Direct");
        }

        [Fact]
        public void ReturnsNull_ForUnknownBssid()
        {
            BssidIdentifier.IdentifyByBssid("AA:BB:CC:DD:EE:FF").Should().BeNull();
        }

        [Fact]
        public void ReturnsNull_ForInvalidBssidLength()
        {
            BssidIdentifier.IdentifyByBssid("62:45:AA").Should().BeNull();
        }

        [Fact]
        public void ReturnsNull_ForBssidTooLong()
        {
            BssidIdentifier.IdentifyByBssid("62:45:AA:BB:CC:DD:EE").Should().BeNull();
        }
    }

    public class GetDisplayName
    {
        [Fact]
        public void ReturnsSsid_WhenSsidIsProvided()
        {
            BssidIdentifier.GetDisplayName("MyNetwork", "62:45:AA:BB:CC:DD").Should().Be("MyNetwork");
        }

        [Fact]
        public void ReturnsHiddenWithIdentifier_ForKnownBssid()
        {
            BssidIdentifier.GetDisplayName(null, "62:45:AA:BB:CC:DD").Should().Be("(Hidden: Xbox Wi-Fi Direct)");
        }

        [Fact]
        public void ReturnsHiddenWithIdentifier_ForKnownBssid_EmptySsid()
        {
            BssidIdentifier.GetDisplayName("", "62:45:AA:BB:CC:DD").Should().Be("(Hidden: Xbox Wi-Fi Direct)");
        }

        [Fact]
        public void ReturnsHidden_ForUnknownBssid()
        {
            BssidIdentifier.GetDisplayName(null, "AA:BB:CC:DD:EE:FF").Should().Be("(Hidden)");
        }

        [Fact]
        public void ReturnsHidden_ForNullBssid()
        {
            BssidIdentifier.GetDisplayName(null, null).Should().Be("(Hidden)");
        }

        [Fact]
        public void ReturnsSsid_EvenWhenBssidIsNull()
        {
            BssidIdentifier.GetDisplayName("MyNetwork", null).Should().Be("MyNetwork");
        }
    }
}

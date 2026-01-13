using System.Net;
using FluentAssertions;
using NetworkOptimizer.Audit.Dns;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

public class DohProviderRegistryTests : IDisposable
{
    public DohProviderRegistryTests()
    {
        // Mock DNS resolver to avoid real network calls and timeouts
        DohProviderRegistry.DnsResolver = _ => Task.FromResult<string?>(null);
    }

    public void Dispose()
    {
        DohProviderRegistry.ResetDnsResolver();
    }

    #region IdentifyProvider - By Hostname

    [Theory]
    [InlineData("dns.cloudflare.com", "Cloudflare")]
    [InlineData("1dot1dot1dot1.cloudflare-dns.com", "Cloudflare")]
    [InlineData("one.one.one.one", "Cloudflare")]
    [InlineData("dns.google", "Google")]
    [InlineData("dns.google.com", "Google")]
    [InlineData("dns.quad9.net", "Quad9")]
    [InlineData("doh.opendns.com", "OpenDNS")]
    [InlineData("dns.adguard.com", "AdGuard")]
    [InlineData("doh.cleanbrowsing.org", "CleanBrowsing")]
    [InlineData("doh.libredns.gr", "LibreDNS")]
    public void IdentifyProvider_KnownHostname_ReturnsCorrectProvider(string hostname, string expectedProvider)
    {
        var result = DohProviderRegistry.IdentifyProvider(hostname);

        result.Should().NotBeNull();
        result!.Name.Should().Be(expectedProvider);
    }

    [Fact]
    public void IdentifyProvider_NextDnsHostname_ReturnsNextDns()
    {
        var result = DohProviderRegistry.IdentifyProvider("dns1.nextdns.io");

        result.Should().NotBeNull();
        result!.Name.Should().Be("NextDNS");
    }

    [Fact]
    public void IdentifyProvider_UnknownHostname_ReturnsNull()
    {
        var result = DohProviderRegistry.IdentifyProvider("unknown.dns.example.com");

        result.Should().BeNull();
    }

    [Fact]
    public void IdentifyProvider_NullHostname_ReturnsNull()
    {
        var result = DohProviderRegistry.IdentifyProvider(null!);

        result.Should().BeNull();
    }

    [Fact]
    public void IdentifyProvider_EmptyHostname_ReturnsNull()
    {
        var result = DohProviderRegistry.IdentifyProvider(string.Empty);

        result.Should().BeNull();
    }

    [Fact]
    public void IdentifyProvider_CaseInsensitive_ReturnsProvider()
    {
        var result = DohProviderRegistry.IdentifyProvider("DNS.CLOUDFLARE.COM");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Cloudflare");
    }

    #endregion

    #region IdentifyProviderFromName - By Server Name

    [Theory]
    [InlineData("NextDNS-abc123", "NextDNS")]
    [InlineData("Cloudflare", "Cloudflare")]
    [InlineData("Google-dns", "Google")]
    [InlineData("Quad9-secure", "Quad9")]
    [InlineData("AdGuard-family", "AdGuard")]
    public void IdentifyProviderFromName_KnownPrefix_ReturnsProvider(string serverName, string expectedProvider)
    {
        var result = DohProviderRegistry.IdentifyProviderFromName(serverName);

        result.Should().NotBeNull();
        result!.Name.Should().Be(expectedProvider);
    }

    [Fact]
    public void IdentifyProviderFromName_UnknownName_ReturnsNull()
    {
        var result = DohProviderRegistry.IdentifyProviderFromName("UnknownProvider-123");

        result.Should().BeNull();
    }

    [Fact]
    public void IdentifyProviderFromName_NullName_ReturnsNull()
    {
        var result = DohProviderRegistry.IdentifyProviderFromName(null!);

        result.Should().BeNull();
    }

    [Fact]
    public void IdentifyProviderFromName_EmptyName_ReturnsNull()
    {
        var result = DohProviderRegistry.IdentifyProviderFromName(string.Empty);

        result.Should().BeNull();
    }

    [Fact]
    public void IdentifyProviderFromName_CaseInsensitive_ReturnsProvider()
    {
        var result = DohProviderRegistry.IdentifyProviderFromName("nextdns-fcdba9");

        result.Should().NotBeNull();
        result!.Name.Should().Be("NextDNS");
    }

    #endregion

    #region IdentifyProviderFromIp - By IP Address

    [Theory]
    [InlineData("1.1.1.1", "Cloudflare")]
    [InlineData("1.0.0.1", "Cloudflare")]
    [InlineData("8.8.8.8", "Google")]
    [InlineData("8.8.4.4", "Google")]
    [InlineData("9.9.9.9", "Quad9")]
    [InlineData("149.112.112.112", "Quad9")]
    [InlineData("208.67.222.222", "OpenDNS")]
    [InlineData("94.140.14.14", "AdGuard")]
    [InlineData("185.228.168.168", "CleanBrowsing")]
    public void IdentifyProviderFromIp_KnownIp_ReturnsProvider(string ip, string expectedProvider)
    {
        var result = DohProviderRegistry.IdentifyProviderFromIp(ip);

        result.Should().NotBeNull();
        result!.Name.Should().Be(expectedProvider);
    }

    [Fact]
    public void IdentifyProviderFromIp_NextDnsPrefixMatch_ReturnsNextDns()
    {
        // NextDNS uses prefix matching for 45.90.x.x
        var result = DohProviderRegistry.IdentifyProviderFromIp("45.90.28.123");

        result.Should().NotBeNull();
        result!.Name.Should().Be("NextDNS");
    }

    [Fact]
    public void IdentifyProviderFromIp_UnknownIp_ReturnsNull()
    {
        var result = DohProviderRegistry.IdentifyProviderFromIp("192.168.1.1");

        result.Should().BeNull();
    }

    [Fact]
    public void IdentifyProviderFromIp_NullIp_ReturnsNull()
    {
        var result = DohProviderRegistry.IdentifyProviderFromIp(null!);

        result.Should().BeNull();
    }

    [Fact]
    public void IdentifyProviderFromIp_EmptyIp_ReturnsNull()
    {
        var result = DohProviderRegistry.IdentifyProviderFromIp(string.Empty);

        result.Should().BeNull();
    }

    #endregion

    #region DohProviderInfo.MatchesIp

    [Fact]
    public void MatchesIp_ExactMatch_ReturnsTrue()
    {
        var provider = DohProviderRegistry.Providers["Google"];

        var result = provider.MatchesIp("8.8.8.8");

        result.Should().BeTrue();
    }

    [Fact]
    public void MatchesIp_PrefixMatch_ReturnsTrue()
    {
        var provider = DohProviderRegistry.Providers["NextDNS"];

        // NextDNS uses prefix matching (45.90.)
        var result = provider.MatchesIp("45.90.123.45");

        result.Should().BeTrue();
    }

    [Fact]
    public void MatchesIp_NoMatch_ReturnsFalse()
    {
        var provider = DohProviderRegistry.Providers["Google"];

        var result = provider.MatchesIp("1.1.1.1");

        result.Should().BeFalse();
    }

    [Fact]
    public void MatchesIp_NullIp_ReturnsFalse()
    {
        var provider = DohProviderRegistry.Providers["Google"];

        var result = provider.MatchesIp(null!);

        result.Should().BeFalse();
    }

    [Fact]
    public void MatchesIp_EmptyIp_ReturnsFalse()
    {
        var provider = DohProviderRegistry.Providers["Google"];

        var result = provider.MatchesIp(string.Empty);

        result.Should().BeFalse();
    }

    #endregion

    #region Providers Registry

    [Fact]
    public void Providers_ContainsExpectedProviders()
    {
        DohProviderRegistry.Providers.Should().ContainKey("NextDNS");
        DohProviderRegistry.Providers.Should().ContainKey("Cloudflare");
        DohProviderRegistry.Providers.Should().ContainKey("Google");
        DohProviderRegistry.Providers.Should().ContainKey("Quad9");
        DohProviderRegistry.Providers.Should().ContainKey("OpenDNS");
        DohProviderRegistry.Providers.Should().ContainKey("AdGuard");
        DohProviderRegistry.Providers.Should().ContainKey("CleanBrowsing");
        DohProviderRegistry.Providers.Should().ContainKey("LibreDNS");
    }

    [Fact]
    public void Providers_AllHaveRequiredFields()
    {
        foreach (var provider in DohProviderRegistry.Providers.Values)
        {
            provider.Name.Should().NotBeNullOrEmpty();
            provider.StampPrefix.Should().NotBeNullOrEmpty();
            provider.Hostnames.Should().NotBeEmpty();
            provider.DnsIps.Should().NotBeEmpty();
            provider.Description.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Providers_NextDnsSupportsFiltering()
    {
        var nextDns = DohProviderRegistry.Providers["NextDNS"];

        nextDns.SupportsFiltering.Should().BeTrue();
        nextDns.HasCustomConfig.Should().BeTrue();
    }

    [Fact]
    public void Providers_CloudflareDoesNotSupportFiltering()
    {
        var cloudflare = DohProviderRegistry.Providers["Cloudflare"];

        cloudflare.SupportsFiltering.Should().BeFalse();
        cloudflare.HasCustomConfig.Should().BeFalse();
    }

    #endregion

    #region ReverseDnsLookupAsync

    [Fact]
    public async Task ReverseDnsLookupAsync_NullIp_ReturnsNull()
    {
        var result = await DohProviderRegistry.ReverseDnsLookupAsync(null!);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReverseDnsLookupAsync_EmptyIp_ReturnsNull()
    {
        var result = await DohProviderRegistry.ReverseDnsLookupAsync(string.Empty);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReverseDnsLookupAsync_InvalidIp_ReturnsNull()
    {
        var result = await DohProviderRegistry.ReverseDnsLookupAsync("not-an-ip");

        result.Should().BeNull();
    }

    #endregion

    #region IdentifyProviderFromIpWithPtrAsync

    [Fact]
    public async Task IdentifyProviderFromIpWithPtrAsync_NullIp_ReturnsNullProvider()
    {
        var (provider, reverseDns) = await DohProviderRegistry.IdentifyProviderFromIpWithPtrAsync(null!);

        provider.Should().BeNull();
        reverseDns.Should().BeNull();
    }

    [Fact]
    public async Task IdentifyProviderFromIpWithPtrAsync_EmptyIp_ReturnsNullProvider()
    {
        var (provider, reverseDns) = await DohProviderRegistry.IdentifyProviderFromIpWithPtrAsync(string.Empty);

        provider.Should().BeNull();
        reverseDns.Should().BeNull();
    }

    [Fact]
    public async Task IdentifyProviderFromIpWithPtrAsync_KnownStaticIp_FallsBackToStaticMatch()
    {
        // Using a private IP that won't have PTR but testing the fallback logic
        // For known provider IPs, static matching should work as fallback
        var (provider, _) = await DohProviderRegistry.IdentifyProviderFromIpWithPtrAsync("8.8.8.8");

        // Google's IP should be identified via static match
        provider.Should().NotBeNull();
        provider!.Name.Should().Be("Google");
    }

    #endregion

    #region IPv6 Support

    [Theory]
    [InlineData("2a07:a8c0::43:b56f", "NextDNS")]
    [InlineData("2a07:a8c1::43:b56f", "NextDNS")]
    [InlineData("2a07:a8c0::ab:cdef", "NextDNS")]
    [InlineData("2a07:a8c1::12:3456", "NextDNS")]
    public void IdentifyProviderFromIp_NextDnsIpv6_ReturnsNextDns(string ip, string expectedProvider)
    {
        var result = DohProviderRegistry.IdentifyProviderFromIp(ip);

        result.Should().NotBeNull();
        result!.Name.Should().Be(expectedProvider);
    }

    [Fact]
    public void MatchesIp_NextDnsIpv6PrefixMatch_ReturnsTrue()
    {
        var provider = DohProviderRegistry.Providers["NextDNS"];

        // NextDNS IPv6 uses prefix matching (2a07:a8c0:: and 2a07:a8c1::)
        provider.MatchesIp("2a07:a8c0::43:b56f").Should().BeTrue();
        provider.MatchesIp("2a07:a8c1::43:b56f").Should().BeTrue();
    }

    [Fact]
    public void MatchesIp_Ipv6CaseInsensitive_ReturnsTrue()
    {
        var provider = DohProviderRegistry.Providers["NextDNS"];

        // IPv6 matching should be case-insensitive
        provider.MatchesIp("2A07:A8C0::43:B56F").Should().BeTrue();
        provider.MatchesIp("2a07:a8c1::AB:CDEF").Should().BeTrue();
    }

    [Fact]
    public void MatchesIp_UnknownIpv6_ReturnsFalse()
    {
        var provider = DohProviderRegistry.Providers["NextDNS"];

        // Unknown IPv6 should not match
        provider.MatchesIp("2001:4860:4860::8888").Should().BeFalse(); // Google DNS IPv6
    }

    [Fact]
    public void IdentifyProviderFromIp_UnknownIpv6_ReturnsNull()
    {
        var result = DohProviderRegistry.IdentifyProviderFromIp("2001:4860:4860::8888");

        result.Should().BeNull();
    }

    #endregion

    #region NextDNS Profile ID Extraction

    [Theory]
    [InlineData("/43b56f", "43b56f")]
    [InlineData("/abc123", "abc123")]
    [InlineData("43b56f", "43b56f")]
    [InlineData("/ABCDEF", "ABCDEF")]
    public void ExtractNextDnsProfileId_ValidPath_ReturnsProfileId(string path, string expectedProfileId)
    {
        var result = DohProviderRegistry.ExtractNextDnsProfileId(path);

        result.Should().Be(expectedProfileId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public void ExtractNextDnsProfileId_InvalidPath_ReturnsNull(string? path)
    {
        var result = DohProviderRegistry.ExtractNextDnsProfileId(path);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("2a07:a8c0::43:b56f", "43b56f")]
    [InlineData("2a07:a8c1::43:b56f", "43b56f")]
    [InlineData("2a07:a8c0::ab:cdef", "abcdef")]
    [InlineData("2a07:a8c1::12:3456", "123456")]
    [InlineData("2A07:A8C0::AB:CDEF", "abcdef")] // Case insensitive
    public void ExtractProfileIdFromNextDnsIpv6_ValidIpv6_ReturnsProfileId(string ip, string expectedProfileId)
    {
        var result = DohProviderRegistry.ExtractProfileIdFromNextDnsIpv6(ip);

        result.Should().Be(expectedProfileId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2001:4860:4860::8888")] // Google DNS
    [InlineData("45.90.28.109")] // IPv4
    [InlineData("invalid")]
    public void ExtractProfileIdFromNextDnsIpv6_InvalidIpv6_ReturnsNull(string? ip)
    {
        var result = DohProviderRegistry.ExtractProfileIdFromNextDnsIpv6(ip);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("2a07:a8c0::43:b56f", "43b56f", true)]
    [InlineData("2a07:a8c1::43:b56f", "43b56f", true)]
    [InlineData("2a07:a8c0::ab:cdef", "abcdef", true)]
    [InlineData("2a07:a8c0::43:b56f", "different", false)]
    [InlineData("2a07:a8c0::43:b56f", null, true)] // No expected profile = prefix match only
    public void NextDnsIpv6MatchesProfile_VariousScenarios_ReturnsExpected(
        string ip, string? expectedProfileId, bool shouldMatch)
    {
        var result = DohProviderRegistry.NextDnsIpv6MatchesProfile(ip, expectedProfileId);

        result.Should().Be(shouldMatch);
    }

    [Theory]
    [InlineData("2A07:A8C0::43:B56F", "43b56f", true)] // Uppercase IP
    [InlineData("2a07:a8c0::43:b56f", "43B56F", true)] // Uppercase profile
    public void NextDnsIpv6MatchesProfile_CaseInsensitive_ReturnsTrue(
        string ip, string expectedProfileId, bool shouldMatch)
    {
        var result = DohProviderRegistry.NextDnsIpv6MatchesProfile(ip, expectedProfileId);

        result.Should().Be(shouldMatch);
    }

    #endregion
}

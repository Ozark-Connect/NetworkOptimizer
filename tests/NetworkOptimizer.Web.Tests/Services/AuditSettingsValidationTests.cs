using FluentAssertions;
using Moq;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// The Security Audit settings accept every value the audit parses as meant, and reject what it
/// would silently drop.
/// </summary>
public class AuditSettingsValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("100")]
    [InlineData("100, 200")]
    [InlineData("1,4094")]
    [InlineData("100,,200,")]
    public void ExcludedVlans_AcceptsVlanIdLists(string? value) =>
        AuditSettingsValidation.ExcludedVlans(value).Should().BeNull();

    [Theory]
    [InlineData("asdfasdf")]
    [InlineData("100, abc")]
    [InlineData("0")]
    [InlineData("4095")]
    [InlineData("100 200")]
    [InlineData("100-200")]
    public void ExcludedVlans_RejectsAnythingElse(string value) =>
        AuditSettingsValidation.ExcludedVlans(value).Should().Be("VLAN ID must be between 1 and 4094.");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("192.0.2.10")]
    [InlineData("192.0.2.10, 198.51.100.53")]
    [InlineData("192.0.2.10;198.51.100.53")]
    [InlineData("192.0.2.10 198.51.100.53")]
    [InlineData("192.0.2.10\n198.51.100.53")]
    [InlineData("2001:db8::53")]
    [InlineData("192.0.2.10, 2001:db8::53")]
    public void TrustedDnsRedirectTargets_AcceptsIpLists(string? value) =>
        AuditSettingsValidation.TrustedDnsRedirectTargets(value).Should().BeNull();

    [Theory]
    [InlineData("dns.local")]
    [InlineData("192.0.2.10, junk")]
    [InlineData("10")]
    [InlineData("10.1")]
    [InlineData("192.0.2.300")]
    [InlineData("192.0.2.0/24")]
    public void TrustedDnsRedirectTargets_RejectsAnythingElse(string value) =>
        AuditSettingsValidation.TrustedDnsRedirectTargets(value).Should().NotBeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("8080")]
    [InlineData("65535")]
    [InlineData(" 5380 ")]
    [InlineData("https://dns.local")]
    [InlineData("http://192.0.2.10:8080/admin")]
    public void DnsManagementEndpoint_AcceptsPortOrHttpUrl(string? value) =>
        AuditSettingsValidation.DnsManagementEndpoint(value).Should().BeNull();

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("dns.local")]
    [InlineData("dns.local:8080")]
    [InlineData("ftp://dns.local")]
    [InlineData("asdf")]
    public void DnsManagementEndpoint_RejectsAnythingElse(string value) =>
        AuditSettingsValidation.DnsManagementEndpoint(value).Should().NotBeNull();

    [Theory]
    [InlineData(AuditSettingsValidation.UnusedPortDaysKey, "1", true)]
    [InlineData(AuditSettingsValidation.UnusedPortDaysKey, "365", true)]
    [InlineData(AuditSettingsValidation.UnusedPortDaysKey, "0", false)]
    [InlineData(AuditSettingsValidation.NamedPortDaysKey, "45", true)]
    [InlineData(AuditSettingsValidation.NamedPortDaysKey, "0", false)]
    [InlineData(AuditSettingsValidation.NamedPortDaysKey, "366", false)]
    public void GraceDays_AllowOneToMax(string key, string value, bool valid) =>
        (AuditSettingsValidation.ErrorFor(key, value) is null).Should().Be(valid);

    [Fact]
    public void ErrorFor_IgnoresKeysItDoesNotValidate() =>
        AuditSettingsValidation.ErrorFor("audit:allowPrintersOnMainNetwork", "anything").Should().BeNull();

    [Fact]
    public async Task SaveAuditOptions_InvalidValue_ThrowsAndSavesNothing()
    {
        var settings = new Mock<ISystemSettingsService>();
        var site = new SiteContextService(
            new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            new NetworkOptimizer.Storage.Services.SiteDatabasePaths("/tmp"));
        var admin = new SiteSettingsAdmin(settings.Object, site);

        var act = () => admin.SaveAuditOptionsAsync(new Dictionary<string, string?>
        {
            ["audit:allowPrintersOnMainNetwork"] = "true",
            [AuditSettingsValidation.DnatExcludedVlansKey] = "asdfasdf",
        });

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("VLAN ID must be between 1 and 4094.*");
        settings.Verify(s => s.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }
}

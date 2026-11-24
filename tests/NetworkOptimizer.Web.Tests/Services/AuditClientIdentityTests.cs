using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// One acknowledgement covers a named client on any AP, any band, and offline; keys saved per AP and band before
/// client identity existed keep working; a client known only by its type is never merged.
/// </summary>
public class AuditClientIdentityTests
{
    private const string Title = "IoT Device Allowed on VLAN";

    private static AuditIssue Wireless(string name, string ap, string band, bool named = true) => new()
    {
        Title = Title, DeviceName = $"{name} on {ap} ({band})", ClientName = name, ClientNamed = named,
        ClientMac = "aa:bb:cc:dd:ee:ff", IsWireless = true,
    };

    private static AuditIssue Offline(string name, bool named = true) => new()
    {
        Title = Title, DeviceName = $"{name} (offline)", ClientNamed = named,
    };

    [Fact]
    public void ANamedClient_IsTheSameClientOnAnyApBandAndOffline()
    {
        AuditService.ClientName(Wireless("[TV] Den", "[AP] Den", "5 GHz")).Should().Be("[TV] Den");
        AuditService.ClientName(Wireless("[TV] Den", "[AP] Lights on Porch", "2.4 GHz")).Should().Be("[TV] Den");
        AuditService.ClientName(Offline("[TV] Den")).Should().Be("[TV] Den");
    }

    [Fact]
    public void AnAcknowledgementSavedPerApAndBand_CoversTheClientEverywhere()
    {
        AuditService.KeyCoversClient($"{Title}|[TV] Den on [AP] Den (5 GHz)|", Title, "[TV] Den").Should().BeTrue();
        AuditService.KeyCoversClient($"{Title}|[TV] Den on [AP] Lights on Porch (2.4 GHz)|", Title, "[TV] Den").Should().BeTrue();
        AuditService.KeyCoversClient($"{Title}|[TV] Den (offline)||38AFDCCE5EFDE8D2", Title, "[TV] Den").Should().BeTrue();
    }

    [Fact]
    public void AnotherClientTitleOrPort_IsNotCovered()
    {
        AuditService.KeyCoversClient($"{Title}|[TV] Den 2 on [AP] Den (5 GHz)|", Title, "[TV] Den").Should().BeFalse();
        AuditService.KeyCoversClient($"Printer Allowed on VLAN|[TV] Den (offline)||38AFDCCE5EFDE8D2", Title, "[TV] Den").Should().BeFalse();
        AuditService.KeyCoversClient($"{Title}|[TV] Den on [Switch] Core|5", Title, "[TV] Den").Should().BeFalse();
    }

    [Fact]
    public void AClientKnownOnlyByItsType_OrAWiredPort_IsNeverMerged()
    {
        AuditService.ClientName(Wireless("Smart TV", "[AP] Den", "5 GHz", named: false)).Should().BeNull();
        AuditService.ClientName(Offline("aa:bb:cc:dd:ee:ff", named: false)).Should().BeNull();
        AuditService.ClientName(new AuditIssue { Title = Title, DeviceName = "[TV] Den on [Switch] Core", Port = "5", ClientNamed = true })
            .Should().BeNull();
    }
}

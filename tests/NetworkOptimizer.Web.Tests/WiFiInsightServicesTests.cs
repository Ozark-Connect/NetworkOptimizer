using Moq;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>The gated services are thin over the store; what they must get right is the band code.</summary>
public class WiFiInsightServicesTests
{
    [Fact]
    public async Task Keep_and_release_write_the_bands_unifi_code()
    {
        var repo = new Mock<IWiFiInsightRepository>();
        var service = new WiFiRadioKeepService(repo.Object);

        await service.KeepAsync("AA:BB:CC:DD:EE:01", RadioBand.Band6GHz);
        await service.ReleaseAsync("AA:BB:CC:DD:EE:01", RadioBand.Band5GHz);

        repo.Verify(r => r.SetKeptAsync("AA:BB:CC:DD:EE:01", "6e", true, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.SetKeptAsync("AA:BB:CC:DD:EE:01", "na", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Acknowledge_and_restore_pass_the_key_through()
    {
        var repo = new Mock<IWiFiInsightRepository>();
        var service = new WiFiIssueAcknowledgmentService(repo.Object);

        await service.AcknowledgeAsync("WIFI-X|site");
        await service.RestoreAsync("WIFI-X|site");

        repo.Verify(r => r.AcknowledgeIssueAsync("WIFI-X|site", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.RestoreIssueAsync("WIFI-X|site", It.IsAny<CancellationToken>()), Times.Once);
    }
}

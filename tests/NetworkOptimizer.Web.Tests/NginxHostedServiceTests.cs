using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class NginxHostedServiceTests
{
    [Fact]
    public void ConstructUrls_UsesIndependentConfiguredRoutes()
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENSPEEDTEST_SAVE_DATA_URL"] = "/api/public/speedtest/results",
            ["OPENSPEEDTEST_CLIENT_RESULTS_URL"] = "https://optimizer.example/client-speedtest"
        };

        NginxHostedService.ConstructSaveDataUrl(config, "/ignored")
            .Should().Be("/api/public/speedtest/results");
        NginxHostedService.ConstructClientResultsUrl(config)
            .Should().Be("https://optimizer.example/client-speedtest");
    }

    [Fact]
    public void ConstructUrls_PreservesExistingDefaults()
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["REVERSE_PROXIED_HOST_NAME"] = "optimizer.example"
        };

        NginxHostedService.ConstructSaveDataUrl(config, "/api/public/speedtest/results")
            .Should().Be("https://optimizer.example/api/public/speedtest/results");
        NginxHostedService.ConstructClientResultsUrl(config)
            .Should().Be("__FROM_SAVE_DATA_URL__");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("//unexpected.example/results")]
    [InlineData("relative/results")]
    public void ConstructSaveDataUrl_RejectsUnsafeConfiguredValues(string value)
    {
        var config = new Dictionary<string, string>
        {
            ["OPENSPEEDTEST_SAVE_DATA_URL"] = value
        };

        var act = () => NginxHostedService.ConstructSaveDataUrl(config, "/api/results");

        act.Should().Throw<InvalidOperationException>();
    }
}

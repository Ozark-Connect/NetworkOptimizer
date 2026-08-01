using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Characterization tests for the speed test host ladder and display URL. These pin the exact
/// behavior the CORS origin list (Program.cs), Client Speed Test, and Client Performance shared
/// before it was extracted - a config shape changing its output here means an existing install's
/// origins or links changed.
/// </summary>
public class OpenSpeedTestSettingsTests
{
    private static OpenSpeedTestSettings Load(Dictionary<string, string?> values, string? detectedIp = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return OpenSpeedTestSettings.Load(configuration, () => detectedIp);
    }

    [Fact]
    public void NothingConfigured_NoHost_UrlFromDetectedIp()
    {
        var settings = Load(new(), detectedIp: "192.0.2.10");

        settings.Host.Should().BeNull();
        settings.Port.Should().Be("3005");
        settings.HttpsEnabled.Should().BeFalse();
        settings.HttpsPort.Should().Be("443");
        settings.FallbackIp.Should().Be("192.0.2.10");
        settings.DisplayUrl.Should().Be("http://192.0.2.10:3005");
    }

    [Fact]
    public void NothingConfigured_NoDetectedIp_NoUrl()
    {
        var settings = Load(new());

        settings.FallbackIp.Should().BeNull();
        settings.DisplayUrl.Should().BeNull();
    }

    [Fact]
    public void HostIp_WinsOverDetection()
    {
        var settings = Load(new() { ["HOST_IP"] = "192.0.2.20" }, detectedIp: "192.0.2.99");

        settings.FallbackIp.Should().Be("192.0.2.20");
        settings.DisplayUrl.Should().Be("http://192.0.2.20:3005");
    }

    [Fact]
    public void HostName_BecomesHost_HttpUrlOnSpeedTestPort()
    {
        var settings = Load(new() { ["HOST_NAME"] = "server1" });

        settings.Host.Should().Be("server1");
        settings.DisplayUrl.Should().Be("http://server1:3005");
    }

    [Fact]
    public void OpenSpeedTestHost_WinsOverHostName()
    {
        var settings = Load(new()
        {
            ["OPENSPEEDTEST_HOST"] = "speedtest.example.com",
            ["HOST_NAME"] = "server1",
        });

        settings.Host.Should().Be("speedtest.example.com");
        settings.DisplayUrl.Should().Be("http://speedtest.example.com:3005");
    }

    [Fact]
    public void CustomPort_UsedInHttpUrl()
    {
        var settings = Load(new() { ["HOST_NAME"] = "server1", ["OPENSPEEDTEST_PORT"] = "3006" });

        settings.Port.Should().Be("3006");
        settings.DisplayUrl.Should().Be("http://server1:3006");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void HttpsEnabled_CaseInsensitive_DefaultPortImplicit(string flag)
    {
        var settings = Load(new()
        {
            ["OPENSPEEDTEST_HOST"] = "speedtest.example.com",
            ["OPENSPEEDTEST_HTTPS"] = flag,
        });

        settings.HttpsEnabled.Should().BeTrue();
        settings.DisplayUrl.Should().Be("https://speedtest.example.com");
    }

    [Fact]
    public void HttpsEnabled_NonStandardPort_Appended()
    {
        var settings = Load(new()
        {
            ["OPENSPEEDTEST_HOST"] = "speedtest.example.com",
            ["OPENSPEEDTEST_HTTPS"] = "true",
            ["OPENSPEEDTEST_HTTPS_PORT"] = "8443",
        });

        settings.DisplayUrl.Should().Be("https://speedtest.example.com:8443");
    }

    [Fact]
    public void ReverseProxiedRung_ActiveWithPortAndHttps()
    {
        // The issue's config: one hostname, app and speed test separated by port
        var settings = Load(new()
        {
            ["REVERSE_PROXIED_HOST_NAME"] = "host.example.com",
            ["REVERSE_PROXIED_PORT"] = "13444",
            ["OPENSPEEDTEST_HTTPS"] = "true",
            ["OPENSPEEDTEST_HTTPS_PORT"] = "13446",
        });

        settings.Host.Should().Be("host.example.com");
        settings.DisplayUrl.Should().Be("https://host.example.com:13446");
    }

    [Fact]
    public void ReverseProxiedRung_BareHostEvenWhenHostNameCarriesPort()
    {
        var settings = Load(new()
        {
            ["REVERSE_PROXIED_HOST_NAME"] = "host.example.com:13444",
            ["REVERSE_PROXIED_PORT"] = "13444",
            ["OPENSPEEDTEST_HTTPS"] = "true",
        });

        settings.Host.Should().Be("host.example.com");
        settings.DisplayUrl.Should().Be("https://host.example.com");
    }

    [Fact]
    public void ReverseProxiedRung_InactiveWithoutReverseProxiedPort()
    {
        // Pre-existing config shape: proxied hostname declared but no REVERSE_PROXIED_PORT.
        // The rung must NOT activate - existing installs keep their direct-IP fallback.
        var settings = Load(new()
        {
            ["REVERSE_PROXIED_HOST_NAME"] = "host.example.com",
            ["OPENSPEEDTEST_HTTPS"] = "true",
        }, detectedIp: "192.0.2.10");

        settings.Host.Should().BeNull();
        settings.DisplayUrl.Should().Be("http://192.0.2.10:3005");
    }

    [Fact]
    public void ReverseProxiedRung_InactiveWithoutHttps()
    {
        var settings = Load(new()
        {
            ["REVERSE_PROXIED_HOST_NAME"] = "host.example.com",
            ["REVERSE_PROXIED_PORT"] = "13444",
        }, detectedIp: "192.0.2.10");

        settings.Host.Should().BeNull();
        settings.DisplayUrl.Should().Be("http://192.0.2.10:3005");
    }

    [Fact]
    public void ReverseProxiedRung_HostNameStillWins()
    {
        var settings = Load(new()
        {
            ["HOST_NAME"] = "server1",
            ["REVERSE_PROXIED_HOST_NAME"] = "host.example.com",
            ["REVERSE_PROXIED_PORT"] = "13444",
            ["OPENSPEEDTEST_HTTPS"] = "true",
        });

        settings.Host.Should().Be("server1");
        settings.DisplayUrl.Should().Be("https://server1");
    }

    [Fact]
    public void Detection_NotInvokedWhenHostSatisfiesUrl()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HOST_NAME"] = "server1" })
            .Build();
        var detectorCalls = 0;
        var settings = OpenSpeedTestSettings.Load(configuration, () => { detectorCalls++; return "192.0.2.10"; });

        settings.DisplayUrl.Should().Be("http://server1:3005");
        detectorCalls.Should().Be(0);
    }

    [Fact]
    public void Detection_InvokedOnceAcrossReads()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var detectorCalls = 0;
        var settings = OpenSpeedTestSettings.Load(configuration, () => { detectorCalls++; return "192.0.2.10"; });

        _ = settings.FallbackIp;
        _ = settings.DisplayUrl;
        detectorCalls.Should().Be(1);
    }

    [Fact]
    public void EmptyStrings_TreatedAsUnset()
    {
        // Configuration[] can return empty string rather than null for missing keys
        var settings = Load(new()
        {
            ["HOST_NAME"] = "",
            ["OPENSPEEDTEST_HOST"] = "",
            ["OPENSPEEDTEST_PORT"] = "",
            ["OPENSPEEDTEST_HTTPS_PORT"] = "",
        }, detectedIp: "192.0.2.10");

        settings.Host.Should().BeNull();
        settings.Port.Should().Be("3005");
        settings.HttpsPort.Should().Be("443");
        settings.DisplayUrl.Should().Be("http://192.0.2.10:3005");
    }
}

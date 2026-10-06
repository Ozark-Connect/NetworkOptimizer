using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// Deploy Known Firmware's options: which device can take which known build, and that each pick
/// carries the same pin a pasted link would.
/// </summary>
public class KnownFirmwareOptionsTests
{
    private const string GatewayMac = "aa:bb:cc:dd:ee:01";
    private const string ApMac = "aa:bb:cc:dd:ee:02";
    private const string PeerMac = "aa:bb:cc:dd:ee:03";

    private static PlannerDevice Device(string mac, DeviceType type, string model, string? version, string name = "Device") =>
        new() { Mac = mac, Name = name, Model = model, Type = type, FromVersion = version };

    private static KnownFirmwareConsole Console(
        string os = "6.0.10", string network = "10.6.106", string? offeredNetwork = null,
        params (string Version, string Url)[] offeredOs) =>
        new("UCGF", os, network, false, offeredOs, offeredNetwork);

    private static SharedFirmwareBuild DeviceBuild(string model, string version, string md5, string? url = null) =>
        new() { Model = model, Channel = "beta", Version = version, Md5Sum = md5, Url = url ?? $"https://fw-download.ubnt.com/data/unifi-firmware/{model}-{version}.bin" };

    [Fact]
    public void DeviceFirmware_OlderThanWhatRuns_IsOfferedAndMarked()
    {
        var options = KnownFirmwareOptions.Build(
            [Device(ApMac, DeviceType.AccessPoint, "U7PRO", "8.0.1.19967")],
            null,
            [DeviceBuild("U7PRO", "8.1.2.20000", "aa"), DeviceBuild("U7PRO", "7.9.0.18000", "bb")],
            [], []);

        options.Should().HaveCount(2);
        options.Single(o => o.Build.Version == "8.1.2.20000").Older.Should().BeFalse();
        options.Single(o => o.Build.Version == "7.9.0.18000").Older.Should().BeTrue();
    }

    [Fact]
    public void DeviceFirmware_TheBuildAlreadyRunning_IsNotOffered()
    {
        var options = KnownFirmwareOptions.Build(
            [Device(ApMac, DeviceType.AccessPoint, "U7PRO", "8.0.1.19967")],
            null,
            [DeviceBuild("U7PRO", "8.0.1.19967", "aa")],
            [], []);

        options.Should().BeEmpty();
    }

    [Fact]
    public void OneImageFiledForAFamily_IsOneBuildWhosePinCoversEveryModel()
    {
        var options = KnownFirmwareOptions.Build(
            [Device(ApMac, DeviceType.AccessPoint, "U7PRO", "8.0.1.19967"), Device(PeerMac, DeviceType.AccessPoint, "U7PIW", "8.0.1.19967")],
            null,
            [DeviceBuild("U7PRO", "8.1.2.20000", "same"), DeviceBuild("U7PIW", "8.1.2.20000", "same")],
            [], []);

        options.Should().HaveCount(2);
        options.Select(o => o.Build.Key).Distinct().Should().ContainSingle();
        var pin = options[0].Build.Pin;
        pin.Kind.Should().Be(FirmwareUrlKind.Device);
        pin.DeviceModels.Should().BeEquivalentTo(["U7PRO", "U7PIW"]);
        pin.UrlFor("U7PIW").Should().Contain("U7PIW");
    }

    [Fact]
    public void CloudGateway_TakesUniFiOsAndNetworkBuilds_NeverDeviceFirmware()
    {
        var options = KnownFirmwareOptions.Build(
            [Device(GatewayMac, DeviceType.Gateway, "UCGF", "4.4.1", "My Gateway")],
            Console(),
            [DeviceBuild("UCGF", "5.0.0.1", "gw")],
            [new SharedUniFiOsBuild { Platform = "UCGF", Channel = "beta", Version = "6.0.11", Url = "https://fw-download.ubnt.com/data/unifi-dream/UCGF-6.0.11.bin" }],
            [new SharedNetworkAppBuild { Channel = "beta", Version = "11.0.81" }]);

        options.Select(o => o.Build.Label).Should().BeEquivalentTo(["UniFi OS 6.0.11", "UniFi Network 11.0.81"]);
        options.Should().OnlyContain(o => o.Device.Label == "[Gateway] My Gateway");
        options.Single(o => o.Build.Kind == FirmwareUrlKind.NetworkApp).Build.Pin.Url
            .Should().Be("https://dl.ui.com/unifi/11.0.81/unifi-native_sysvinit.deb");
    }

    [Fact]
    public void UniFiOsAndNetwork_NoNewerThanWhatRuns_AreNotOffered()
    {
        var options = KnownFirmwareOptions.Build(
            [Device(GatewayMac, DeviceType.Gateway, "UCGF", "4.4.1")],
            Console(os: "6.0.11", network: "11.0.81"),
            [],
            [new SharedUniFiOsBuild { Platform = "UCGF", Channel = "release", Version = "6.0.7", Url = "https://fw-download.ubnt.com/a.bin" }],
            [new SharedNetworkAppBuild { Channel = "release", Version = "10.6.106" }, new SharedNetworkAppBuild { Channel = "beta", Version = "11.0.81" }]);

        options.Should().BeEmpty();
    }

    [Fact]
    public void UniFiOs_OneImageUnderTwoVersionSpellings_IsOneBuild()
    {
        const string url = "https://fw-download.ubnt.com/data/unifi-dream/UCGF-6.0.11.bin";
        var options = KnownFirmwareOptions.Build(
            [Device(GatewayMac, DeviceType.Gateway, "UCGF", "4.4.1")],
            Console(),
            [],
            [
                new SharedUniFiOsBuild { Platform = "UCGF", Channel = "beta", Version = "6.0.11", Url = url },
                new SharedUniFiOsBuild { Platform = "UCGF", Channel = "beta", Version = "v6.0.11+5efda8b", Url = url },
            ],
            []);

        options.Should().ContainSingle().Which.Build.Version.Should().Be("6.0.11");
    }

    [Fact]
    public void TheConsolesOwnOffers_AreIncludedAlongsideTheCatalog()
    {
        var options = KnownFirmwareOptions.Build(
            [Device(GatewayMac, DeviceType.Gateway, "UCGF", "4.4.1")],
            Console(offeredNetwork: "10.7.1", offeredOs: ("6.0.12", "https://fw-download.ubnt.com/data/unifi-dream/UCGF-6.0.12.bin")),
            [], [], []);

        options.Select(o => o.Build.Label).Should().BeEquivalentTo(["UniFi OS 6.0.12", "UniFi Network 10.7.1"]);
    }

    [Fact]
    public void ACellularModem_IsNeverOffered()
    {
        var options = KnownFirmwareOptions.Build(
            [Device(ApMac, DeviceType.CellularModem, "ULTE", "1.0.0")],
            null,
            [DeviceBuild("ULTE", "2.0.0", "lte")],
            [], []);

        options.Should().BeEmpty();
    }
}

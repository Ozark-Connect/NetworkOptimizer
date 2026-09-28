using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// The per-device inform key decrypts a UCI's informs, so it must be readable from stat/device
/// and must never leave again in a re-serialized device payload.
/// </summary>
public class DeviceInformKeyTests
{
    private const string Device = """
        { "mac": "aa:bb:cc:00:11:22", "type": "uci", "model": "UCI", "name": "Cable",
          "adopted": true, "x_authkey": "00112233445566778899aabbccddeeff" }
        """;

    [Fact]
    public void InformKey_IsReadFromStatDevice()
    {
        var device = JsonSerializer.Deserialize<UniFiDeviceResponse>(Device)!;

        device.GetInformKey().Should().Be("00112233445566778899aabbccddeeff");
        device.DeviceType.Should().Be(DeviceType.CableModem);
    }

    [Fact]
    public void InformKey_IsNeverSerialized()
    {
        var device = JsonSerializer.Deserialize<UniFiDeviceResponse>(Device)!;

        var json = JsonSerializer.Serialize(device);

        json.Should().NotContain("x_authkey");
        json.Should().NotContain("00112233445566778899aabbccddeeff");
    }
}

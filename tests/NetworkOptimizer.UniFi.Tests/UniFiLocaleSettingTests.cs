using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class UniFiLocaleSettingTests
{
    // GET get/setting/locale as a UniFi OS console answers it, ids replaced.
    private const string Response =
        """{"meta":{"rc":"ok"},"data":[{"_id":"00000000-0000-0000-0000-000000000001","key":"locale","site_id":"00000000-0000-0000-0000-000000000002","timezone":"America/Denver"}]}""";

    [Fact]
    public void The_locale_setting_carries_the_site_timezone()
    {
        var response = JsonSerializer.Deserialize<UniFiApiResponse<UniFiLocaleSetting>>(Response)!;

        response.Meta.Rc.Should().Be("ok");
        response.Data.Should().ContainSingle(s => s.Key == "locale" && s.Timezone == "America/Denver");
    }

    [Fact]
    public void Sysinfo_carries_the_network_application_timezone()
    {
        var response = JsonSerializer.Deserialize<UniFiSysInfoResponse>(
            """{"meta":{"rc":"ok"},"data":[{"timezone":"America/New_York","version":"11.0.99"}]}""")!;

        response.Data[0].Timezone.Should().Be("America/New_York");
    }
}

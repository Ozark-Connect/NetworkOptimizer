using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.ClientIcons;
using NetworkOptimizer.Web.Services.LanFlowMap;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ClientIconSourceTests
{
    private const string IconId = "0123456789abcdef0123456789abcdef";
    private const string AiBase = "https://image.icon-gen.svc.ui.com/image/v2/" + IconId + "_rev_ABCDEF0123456789";

    private static UniFiAiIcon AiIcon(params int[] sizes) => new()
    {
        IconId = IconId,
        Assets = sizes.Select(s => new UniFiAiIconAsset { Url = $"{AiBase}_{s}x{s}.png", Width = s, Height = s }).ToList(),
    };

    [Fact]
    public void CustomIcon_WinsOverFingerprint()
    {
        var icon = ClientIconSource.Resolve(new UniFiClientResponse { AiIcon = AiIcon(25, 129, 257), DevIdOverride = 5528, DevId = 4841 });

        icon.Should().Be(new ClientIconRef("ai-" + IconId + "_rev_ABCDEF0123456789", AiBase + "_129x129.png", SiteOwned: true));
    }

    [Fact]
    public void CustomIcon_WithoutThe129Size_TakesTheNextLarger()
    {
        ClientIconSource.Resolve(new UniFiClientResponse { AiIcon = AiIcon(25, 101, 257) })!.SourceUrl
            .Should().Be(AiBase + "_257x257.png");
    }

    [Fact]
    public void CustomIcon_OnAnotherHost_IsIgnored()
    {
        var client = new UniFiClientResponse
        {
            AiIcon = new UniFiAiIcon { Assets = [new UniFiAiIconAsset { Url = $"https://example.com/{IconId}_rev_A_129x129.png", Width = 129 }] },
            DevId = 38,
        };

        ClientIconSource.Resolve(client)!.Key.Should().Be("fp-38");
    }

    [Fact]
    public void UniFiDevice_UsesItsProductIcon()
    {
        var icon = ClientIconSource.Resolve(new UniFiClientResponse { IconFilename = "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE" });

        icon.Should().Be(new ClientIconRef("ud-aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            "https://static.ui.com/fingerprint/ui/icons/aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee_129x129.png", SiteOwned: false));
    }

    [Fact]
    public void UniFiDevice_IconOnlyInTheUcoreBlock_IsUsed()
    {
        // Some consoles set the icon only inside unifi_device_info_from_ucore, with no fingerprint id.
        var client = new UniFiClientResponse
        {
            UnifiDeviceInfoFromUcore = new UniFiUcoreDeviceInfo { IconFilename = "12345678-1234-4123-8123-123456789abc", ProductLine = "PROTECT" },
        };

        ClientIconSource.Resolve(client)!.Key.Should().Be("ud-12345678-1234-4123-8123-123456789abc");
    }

    [Fact]
    public void UcoreBlock_DeserializesFromTheConsoleShape()
    {
        const string json = """{"mac":"aa:bb:cc:dd:ee:ff","unifi_device_info_from_ucore":{"icon_filename":"12345678-1234-4123-8123-123456789abc","product_shortname":"UVC G6 Pro Bullet"}}""";
        var client = System.Text.Json.JsonSerializer.Deserialize<UniFiClientResponse>(json)!;

        client.UnifiDeviceInfoFromUcore!.IconFilename.Should().Be("12345678-1234-4123-8123-123456789abc");
        ClientIconSource.Resolve(client)!.Key.Should().Be("ud-12345678-1234-4123-8123-123456789abc");
    }

    [Theory]
    [InlineData(5144, 38, "fp-5144")] // The user's chosen device wins
    [InlineData(null, 38, "fp-38")]
    [InlineData(null, null, null)]
    [InlineData(null, 0, null)]
    public void Fingerprint(int? devIdOverride, int? devId, string? expectedKey)
    {
        ClientIconSource.Resolve(new UniFiClientResponse { DevIdOverride = devIdOverride, DevId = devId })?.Key
            .Should().Be(expectedKey);
    }

    [Fact]
    public void Fingerprint_FetchesEngineZeroAt129()
    {
        ClientIconSource.CatalogUrl("fp-38").Should().Be("https://static.ui.com/fingerprint/0/38_129x129.png");
    }

    [Theory]
    [InlineData("fp-38", true)]
    [InlineData("ud-aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", true)]
    [InlineData("ai-" + IconId + "_rev_ABCDEF0123456789", true)]
    [InlineData("fp-../../etc", false)]
    [InlineData("ai-short", false)]
    [InlineData("xx-38", false)]
    [InlineData(null, false)]
    public void IsValidKey(string? key, bool expected)
    {
        ClientIconSource.IsValidKey(key).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://static.ui.com/fingerprint/0/38_129x129.png", true)]
    [InlineData("https://image.icon-gen.svc.ui.com/image/v2/x.png", true)]
    [InlineData("http://static.ui.com/fingerprint/0/38_129x129.png", false)]
    [InlineData("https://static.ui.com:8443/x.png", false)]
    [InlineData("https://static.ui.com.example.com/x.png", false)]
    [InlineData("https://192.0.2.1/x.png", false)]
    [InlineData("not a url", false)]
    public void IsAllowedSource(string url, bool expected)
    {
        ClientIconSource.IsAllowedSource(url).Should().Be(expected);
    }

    [Fact]
    public void Catalog_FindsAnIconByShortnameOrSku_InAnyCase()
    {
        const string json = """
            {"devices":[
              {"sku":"UAP-AC-Pro","shortnames":["U7PG2","UniFi AC PRO"],"icon":{"id":"33333333-3333-4333-8333-333333333333"}},
              {"sku":"NO-ICON","shortnames":["X1"]},
              {"sku":"DUP","shortnames":["U7PG2"],"icon":{"id":"00000000-0000-0000-0000-000000000000"}}
            ],"version":"1"}
            """;
        var catalog = UbiquitiDeviceCatalog.Parse(json);

        catalog.IconIdFor("u7pg2").Should().Be("33333333-3333-4333-8333-333333333333");
        catalog.IconIdFor(null, "UAP-AC-Pro").Should().Be("33333333-3333-4333-8333-333333333333");
        catalog.IconIdFor("X1").Should().BeNull();
        catalog.IconIdFor("unknown", "").Should().BeNull();
    }

    [Fact]
    public void Catalog_FindsAnIconByFriendlyModelName()
    {
        const string json = """
            {"devices":[
              {"sku":"U7-Pro-Max","shortnames":["UAPA6A5"],"product":{"abbrev":"U7 Pro Max","name":"Access Point U7 Pro Max"},"icon":{"id":"11111111-1111-1111-1111-111111111111"}},
              {"sku":"U6-Mesh","shortnames":["UAL6"],"icon":{"id":"22222222-2222-2222-2222-222222222222"}}
            ]}
            """;
        var catalog = UbiquitiDeviceCatalog.Parse(json);

        catalog.IconIdForModel("U7 Pro Max").Should().Be("11111111-1111-1111-1111-111111111111");
        catalog.IconIdForModel("U6 Mesh").Should().Be("22222222-2222-2222-2222-222222222222"); // dashed like the SKU
        catalog.IconIdForModel("Not A Model").Should().BeNull();
        catalog.IconIdForModel(null).Should().BeNull();
    }

    [Fact]
    public void UniFiDeviceKey_AcceptsOnlyAGuid()
    {
        ClientIconSource.UniFiDeviceKey("33333333-3333-4333-8333-333333333333").Should().Be("ud-33333333-3333-4333-8333-333333333333");
        ClientIconSource.UniFiDeviceKey("../etc/passwd").Should().BeNull();
        ClientIconSource.UniFiDeviceKey(null).Should().BeNull();
    }

    [Fact]
    public void Rank_CustomThenVendorThenGeneric()
    {
        var fingerprints = new UniFiFingerprintDatabase();
        fingerprints.DevIds["38"] = new FingerprintDeviceEntry { Name = "Vendor TV", VendorId = "97" };
        fingerprints.DevIds["5144"] = new FingerprintDeviceEntry { Name = "Linux", VendorId = null };

        ClientIconRegistry.Rank(new ClientIconRef("ai-x", "u", SiteOwned: true), fingerprints).Should().Be(3);
        ClientIconRegistry.Rank(new ClientIconRef("ud-x", "u", SiteOwned: false), fingerprints).Should().Be(2);
        ClientIconRegistry.Rank(new ClientIconRef("fp-38", "u", SiteOwned: false), fingerprints).Should().Be(2);
        ClientIconRegistry.Rank(new ClientIconRef("fp-5144", "u", SiteOwned: false), fingerprints).Should().Be(1);
        ClientIconRegistry.Rank(new ClientIconRef("fp-38", "u", SiteOwned: false), null).Should().Be(1);
    }

    [Fact]
    public void Hub_TakesTheMostSpecificMemberIcon_ThenTheMostShared()
    {
        var icons = new ClientIconMap("s", new Dictionary<string, string>(), rankByMac: new Dictionary<string, int>
        {
            ["00:00:00:00:00:01"] = 1, ["00:00:00:00:00:02"] = 1, ["00:00:00:00:00:03"] = 2, ["00:00:00:00:00:04"] = 3,
        });
        LanNode Member(string mac, string url) => new() { Id = mac, Kind = LanNodeKind.WiredClient, Mac = mac, IconUrl = url };

        Endpoints.LanFlowMapEndpoints.BestIcon(
            [Member("00:00:00:00:00:01", "generic"), Member("00:00:00:00:00:03", "vendor"), Member("00:00:00:00:00:04", "custom")], icons)
            .Should().Be("custom");
        Endpoints.LanFlowMapEndpoints.BestIcon(
            [Member("00:00:00:00:00:01", "generic-a"), Member("00:00:00:00:00:02", "generic-b"), Member("00:00:00:00:00:05", "generic-b")], icons)
            .Should().Be("generic-b");
        Endpoints.LanFlowMapEndpoints.BestIcon([], icons).Should().BeNull();
    }

    [Fact]
    public void EmptyMap_AnswersNullForEveryLookup()
    {
        ClientIconMap.Empty.UrlFor("aa:bb:cc:dd:ee:ff").Should().BeNull();
        ClientIconMap.Empty.DeviceUrlFor("192.0.2.2").Should().BeNull();
        ClientIconMap.Empty.DeviceCatalogUrlFor("aa:bb:cc:dd:ee:ff").Should().BeNull();
    }

    [Fact]
    public void Map_FindsADeviceImageByIp()
    {
        var map = new ClientIconMap("site-a", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["192.0.2.2"] = "/images/devices/u7-pro.png" });

        map.DeviceUrlFor(" 192.0.2.2 ").Should().Be("/images/devices/u7-pro.png");
        map.DeviceUrlFor("192.0.2.3").Should().BeNull();
    }

    [Fact]
    public void Map_MatchesMacInAnyCase_AndCarriesTheSite()
    {
        var map = new ClientIconMap("site-a", new Dictionary<string, string> { ["aa:bb:cc:dd:ee:ff"] = "fp-38" });

        map.UrlFor("AA-BB-CC-DD-EE-FF").Should().Be("/api/client-icons/fp-38?site=site-a");
        map.UrlFor("00:11:22:33:44:55").Should().BeNull();
        map.UrlFor(null).Should().BeNull();
    }
}

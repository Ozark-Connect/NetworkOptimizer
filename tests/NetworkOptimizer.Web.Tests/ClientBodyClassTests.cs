using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.ClientIcons;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ClientBodyClassTests
{
    [Theory]
    [InlineData(ClientDeviceCategory.Smartphone, 0.3, ClientBodyDepth.Thin)]
    [InlineData(ClientDeviceCategory.SmartPlug, 0.3, ClientBodyDepth.Mid)]
    [InlineData(ClientDeviceCategory.Camera, 0.3, ClientBodyDepth.Mid)]
    [InlineData(ClientDeviceCategory.Tablet, 0.45, ClientBodyDepth.Thin)]
    [InlineData(ClientDeviceCategory.Laptop, 0.45, ClientBodyDepth.Thin)]
    [InlineData(ClientDeviceCategory.GameConsole, 0.45, ClientBodyDepth.Mid)]
    [InlineData(ClientDeviceCategory.Desktop, 0.5, ClientBodyDepth.Deep)]
    [InlineData(ClientDeviceCategory.Server, 0.5, ClientBodyDepth.Deep)]
    [InlineData(ClientDeviceCategory.SmartTV, 1.1, ClientBodyDepth.Thin)]
    public void Category_MapsToSizeAndDepth(ClientDeviceCategory category, double sizeM, ClientBodyDepth depth)
    {
        var body = ClientBodyClass.For(category);

        Assert.Equal(sizeM, body.SizeM);
        Assert.Equal(depth, body.Depth);
    }

    [Fact]
    public void UndetectedClient_IsTheSmallestBody()
    {
        Assert.Same(ClientBodyClass.Small, ClientBodyClass.For(ClientDeviceCategory.Unknown));
        foreach (var category in Enum.GetValues<ClientDeviceCategory>())
            Assert.True(ClientBodyClass.For(category).SizeM >= ClientBodyClass.Small.SizeM);
    }

    [Fact]
    public void ATelevision_IsAboutTwiceADesktop_AndHugsTheWall()
    {
        var tv = ClientBodyClass.For(ClientDeviceCategory.SmartTV);
        var desktop = ClientBodyClass.For(ClientDeviceCategory.Desktop);

        Assert.True(tv.WallFlat);
        Assert.False(desktop.WallFlat);
        Assert.InRange(tv.SizeM / desktop.SizeM, 2.0, 2.5);
    }

    [Theory]
    [InlineData(254)] // smart clock
    [InlineData(143)] // picture frame
    [InlineData(203)] // smart display
    public void ASmartTvCategory_ThatIsNotATelevision_IsAMediumPanel(int devTypeId)
    {
        var body = ClientBodyClass.For(ClientDeviceCategory.SmartTV, devTypeId);

        Assert.Equal(0.45, body.SizeM);
        Assert.False(body.WallFlat);
    }

    [Theory]
    [InlineData(131, null)]      // washing machine, by fingerprint type
    [InlineData(118, null)]      // dryer
    [InlineData(null, "Dryer")]  // by name, when the fingerprint does not type it
    [InlineData(48, "Laundry Washer")]
    public void AFloorAppliance_IsLargeAndDeep(int? devTypeId, string? name)
    {
        var body = ClientBodyClass.For(ClientDeviceCategory.SmartAppliance, devTypeId, name);

        Assert.Equal(0.85, body.SizeM);
        Assert.Equal(ClientBodyDepth.Deep, body.Depth);
    }

    [Theory]
    [InlineData(149, "Kettle")]     // smart kettle
    [InlineData(264, "Toothbrush")]
    [InlineData(null, null)]
    public void ASmallAppliance_IsAMediumBox(int? devTypeId, string? name)
    {
        var body = ClientBodyClass.For(ClientDeviceCategory.SmartAppliance, devTypeId, name);

        Assert.Equal(0.45, body.SizeM);
        Assert.Equal(ClientBodyDepth.Mid, body.Depth);
    }

    [Theory]
    [InlineData(ClientDeviceCategory.SmartSpeaker, "Living Room Soundbar")]
    [InlineData(ClientDeviceCategory.MediaPlayer, "Sound Bar")]
    [InlineData(ClientDeviceCategory.Unknown, "soundbar")]
    public void ASoundBar_IsHalfAgainAsWideAsASpeaker_AndHugsTheWall(ClientDeviceCategory category, string name)
    {
        var bar = ClientBodyClass.For(category, name: name);
        var speaker = ClientBodyClass.For(ClientDeviceCategory.SmartSpeaker, name: "Kitchen Speaker");

        Assert.True(bar.WallFlat);
        Assert.InRange(bar.SizeM / speaker.SizeM, 1.5, 1.6);
    }

    [Fact]
    public void Classify_KeepsTheLastBody_WhenAReadCannotSayWhatTheClientIs()
    {
        var tv = ClientBodyClass.For(ClientDeviceCategory.SmartTV);
        var previous = new Dictionary<string, ClientBody> { ["00:11:22:33:44:55"] = tv };
        var rows = new[]
        {
            new UniFiClientResponse { Mac = "00:11:22:33:44:55" },
            new UniFiClientResponse { Mac = "00-11-22-33-44-66" },
        };

        var result = ClientIconRegistry.Classify(previous, rows, fingerprints: null);

        Assert.Same(tv, result["00:11:22:33:44:55"]);
        Assert.Same(ClientBodyClass.Small, result["00:11:22:33:44:66"]);
    }

    [Fact]
    public void Classify_ReadsTheNameThroughTheAuditDetector()
    {
        var rows = new[] { new UniFiClientResponse { Mac = "00:11:22:33:44:55", Name = "Samsung TV" } };

        var result = ClientIconRegistry.Classify(new Dictionary<string, ClientBody>(), rows, fingerprints: null);

        Assert.True(result["00:11:22:33:44:55"].WallFlat);
    }

    [Fact]
    public void Classify_UsesTheFingerprintType_ForATelevisionWithATemplateName()
    {
        // dev_cat 47 is UniFi's "Smart TV" type. The name carries no word the detector reads.
        var rows = new[] { new UniFiClientResponse { Mac = "00:11:22:33:44:55", Name = "[TV] Living Room", DevCat = 47 } };

        var result = ClientIconRegistry.Classify(new Dictionary<string, ClientBody>(), rows, fingerprints: null);

        Assert.Equal(1.1, result["00:11:22:33:44:55"].SizeM);
    }

    [Fact]
    public void Map_AnswersTheSmallestBody_ForAClientItHasNone()
    {
        var laptop = ClientBodyClass.For(ClientDeviceCategory.Laptop);
        var map = new ClientIconMap("site", new Dictionary<string, string>(),
            bodyByMac: new Dictionary<string, ClientBody> { ["00:11:22:33:44:55"] = laptop });

        Assert.Same(laptop, map.BodyFor("00-11-22-33-44-55"));
        Assert.Same(ClientBodyClass.Small, map.BodyFor("00:11:22:33:44:66"));
        Assert.Same(ClientBodyClass.Small, map.BodyFor(null));
    }
}

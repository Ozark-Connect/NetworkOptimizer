using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.ClientIcons;
using NetworkOptimizer.Web.Services.LanFlowMap;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ClientBodyClassTests
{
    [Theory]
    [InlineData(ClientDeviceCategory.Smartphone, ClientBodySize.Small, ClientBodyDepth.Thin)]
    [InlineData(ClientDeviceCategory.SmartPlug, ClientBodySize.Small, ClientBodyDepth.Mid)]
    [InlineData(ClientDeviceCategory.Camera, ClientBodySize.Small, ClientBodyDepth.Mid)]
    [InlineData(ClientDeviceCategory.Tablet, ClientBodySize.Medium, ClientBodyDepth.Thin)]
    [InlineData(ClientDeviceCategory.Laptop, ClientBodySize.Medium, ClientBodyDepth.Thin)]
    [InlineData(ClientDeviceCategory.GameConsole, ClientBodySize.Medium, ClientBodyDepth.Mid)]
    [InlineData(ClientDeviceCategory.SmartTV, ClientBodySize.Large, ClientBodyDepth.Thin)]
    [InlineData(ClientDeviceCategory.Desktop, ClientBodySize.Large, ClientBodyDepth.Deep)]
    [InlineData(ClientDeviceCategory.SmartAppliance, ClientBodySize.Large, ClientBodyDepth.Deep)]
    public void Category_MapsToSizeAndDepth(ClientDeviceCategory category, ClientBodySize size, ClientBodyDepth depth)
    {
        Assert.Equal(size, ClientBodyClass.SizeOf(category));
        Assert.Equal(depth, ClientBodyClass.DepthOf(category));
    }

    [Fact]
    public void UndetectedClient_IsSmallAndThin()
    {
        Assert.Equal(ClientBodySize.Small, ClientBodyClass.SizeOf(ClientDeviceCategory.Unknown));
        Assert.Equal(ClientBodyDepth.Thin, ClientBodyClass.DepthOf(ClientDeviceCategory.Unknown));
    }

    [Fact]
    public void OnlyATv_IsWallFlat()
    {
        foreach (var category in Enum.GetValues<ClientDeviceCategory>())
            Assert.Equal(category == ClientDeviceCategory.SmartTV, ClientBodyClass.IsWallFlat(category));
    }

    [Fact]
    public void Categorize_KeepsTheLastCategory_WhenAReadCannotNameOne()
    {
        var previous = new Dictionary<string, ClientDeviceCategory> { ["00:11:22:33:44:55"] = ClientDeviceCategory.SmartTV };
        var rows = new[]
        {
            new UniFiClientResponse { Mac = "00:11:22:33:44:55" },
            new UniFiClientResponse { Mac = "00-11-22-33-44-66" },
        };

        var result = ClientIconRegistry.Categorize(previous, rows, fingerprints: null);

        Assert.Equal(ClientDeviceCategory.SmartTV, result["00:11:22:33:44:55"]);
        Assert.Equal(ClientDeviceCategory.Unknown, result["00:11:22:33:44:66"]);
    }

    [Fact]
    public void Categorize_ReadsTheNameThroughTheAuditDetector()
    {
        var rows = new[] { new UniFiClientResponse { Mac = "00:11:22:33:44:55", Name = "Samsung TV" } };

        var result = ClientIconRegistry.Categorize(new Dictionary<string, ClientDeviceCategory>(), rows, fingerprints: null);

        Assert.Equal(ClientDeviceCategory.SmartTV, result["00:11:22:33:44:55"]);
    }

    [Fact]
    public void Map_AnswersUnknown_ForAClientItHasNoCategoryFor()
    {
        var map = new ClientIconMap("site", new Dictionary<string, string>(),
            categoryByMac: new Dictionary<string, ClientDeviceCategory> { ["00:11:22:33:44:55"] = ClientDeviceCategory.Laptop });

        Assert.Equal(ClientDeviceCategory.Laptop, map.CategoryFor("00-11-22-33-44-55"));
        Assert.Equal(ClientDeviceCategory.Unknown, map.CategoryFor("00:11:22:33:44:66"));
        Assert.Equal(ClientDeviceCategory.Unknown, map.CategoryFor(null));
    }
}

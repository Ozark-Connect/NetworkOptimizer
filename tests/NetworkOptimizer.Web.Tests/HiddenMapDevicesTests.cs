using NetworkOptimizer.Web.Services.LanFlowMap;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class HiddenMapDevicesTests
{
    [Fact]
    public void Key_IsPerSite()
    {
        Assert.NotEqual(HiddenMapDevices.Key("default"), HiddenMapDevices.Key("site-b"));
    }

    [Fact]
    public void Normalize_LowercasesAndUsesColons_AndKeepsEachMacOnce()
    {
        var result = HiddenMapDevices.Normalize(["AA-BB-CC-DD-EE-FF", "aa:bb:cc:dd:ee:ff", " 00:11:22:33:44:55 "]);

        Assert.Equal(["aa:bb:cc:dd:ee:ff", "00:11:22:33:44:55"], result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a mac")]
    [InlineData("aa:bb:cc:dd:ee")]
    [InlineData("aa:bb:cc:dd:ee:ff:00")]
    [InlineData("<script>")]
    public void Normalize_DropsWhatIsNotAMac(string value)
    {
        Assert.Empty(HiddenMapDevices.Normalize([value, null]));
    }

    [Theory]
    [InlineData("hub-dev-aa:bb:cc:dd:ee:ff-aa:bb:cc:dd:ee:ff|eth5")]
    [InlineData("hub-dev-aa:bb:cc:dd:ee:ff-aa:bb:cc:dd:ee:ff|Port 5")]
    [InlineData("hub-dev-aa:bb:cc:dd:ee:ff-aa:bb:cc:dd:ee:ff|eth0.20")]
    [InlineData("hub-dev-aa:bb:cc:dd:ee:ff-aa:bb:cc:dd:ee:ff|GigabitEthernet1/0/5")]
    public void Normalize_KeepsAGroupNodeId_AsWritten(string id)
    {
        Assert.Equal([id], HiddenMapDevices.Normalize([id]));
    }

    [Fact]
    public void Normalize_KeepsTheIdTheMapServiceBuilds()
    {
        // The exact shape LanFlowMapService gives a group node: hub-{parent node id}-{PortKey}.
        const string parentMac = "aa:bb:cc:dd:ee:ff";
        var id = $"hub-dev-{parentMac}-{parentMac}|eth5";

        Assert.Equal([id], HiddenMapDevices.Normalize([id]));
    }

    [Theory]
    [InlineData("hub-")]
    [InlineData("hub-dev-not-a-mac-5")]
    [InlineData("hub-dev-aa:bb:cc:dd:ee:ff-")]
    [InlineData("hub-dev-aa:bb:cc:dd:ee:ff-aa:bb:cc:dd:ee:ff|")]
    [InlineData("hub-dev-aa:bb:cc:dd:ee:ff-aa:bb:cc:dd:ee:ff|<script>")]
    [InlineData("dev-aa:bb:cc:dd:ee:ff")]
    public void Normalize_DropsAnIdThatIsNotAGroupNode(string id)
    {
        Assert.Empty(HiddenMapDevices.Normalize([id]));
    }

    [Fact]
    public void Normalize_BoundsTheList()
    {
        var many = Enumerable.Range(0, HiddenMapDevices.MaxDevices + 50).Select(i => $"00:11:22:33:{i / 256:x2}:{i % 256:x2}");

        Assert.Equal(HiddenMapDevices.MaxDevices, HiddenMapDevices.Normalize(many).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("\"a string\"")]
    public void Parse_AnswersEmpty_ForNothingStoredOrAValueThatDoesNotParse(string? stored)
    {
        Assert.Empty(HiddenMapDevices.Parse(stored));
    }

    [Fact]
    public void Parse_ReadsWhatNormalizeWrote()
    {
        Assert.Equal(["aa:bb:cc:dd:ee:ff"], HiddenMapDevices.Parse("[\"AA:BB:CC:DD:EE:FF\"]"));
    }
}

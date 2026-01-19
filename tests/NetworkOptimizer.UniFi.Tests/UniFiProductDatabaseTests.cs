using FluentAssertions;
using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class UniFiProductDatabaseTests
{
    #region GetProductName Tests (Official Codes Only)

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    public void GetProductName_NullOrEmpty_ReturnsUnknown(string? modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UDMPRO", "UDM-Pro")]
    [InlineData("UDMPROSE", "UDM-SE")]
    [InlineData("UDMPROMAX", "UDM-Pro-Max")]
    [InlineData("UDM", "UDM")]
    public void GetProductName_DreamMachineFamily_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UCGMAX", "UCG-Max")]
    [InlineData("UDMA6A8", "UCG-Fiber")]
    [InlineData("UDRULT", "UCG-Ultra")]
    public void GetProductName_CloudGateways_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UGW3", "USG-3P")]
    [InlineData("UGW4", "USG-Pro-4")]
    [InlineData("UGWXG", "USG-XG-8")]
    [InlineData("UGWHD4", "USG")]
    public void GetProductName_SecurityGateways_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UXGPRO", "UXG-Pro")]
    [InlineData("UXG", "UXG-Lite")]
    [InlineData("UXGA6AA", "UXG-Fiber")]
    [InlineData("UXGENT", "UXG-Enterprise")]
    [InlineData("UXGB", "UXG-Max")]
    public void GetProductName_NextGenGateways_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UDR", "UDR")]
    [InlineData("UDMA67A", "UDR7")]
    [InlineData("UDMA6B9", "UDR-5G-Max")]
    public void GetProductName_DreamRouters_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UX", "UX")]
    [InlineData("UDMA69B", "UX7")]
    public void GetProductName_UniFiExpress_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USF5P", "USW-Flex")]
    [InlineData("USMINI", "USW-Flex-Mini")]
    [InlineData("USFXG", "USW-Flex-XG")]
    public void GetProductName_FlexSwitches_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USWED35", "USW-Flex-2.5G-5")]
    [InlineData("USWED36", "USW-Flex-2.5G-8")]
    [InlineData("USWED37", "USW-Flex-2.5G-8-PoE")]
    public void GetProductName_Flex25GSwitches_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USM8P", "USW-Ultra")]
    [InlineData("USM8P60", "USW-Ultra-60W")]
    [InlineData("USM8P210", "USW-Ultra-210W")]
    public void GetProductName_UltraSwitches_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("US24PRO2", "USW-Pro-24")]
    [InlineData("US48PRO2", "USW-Pro-48")]
    [InlineData("USLP24P", "USW-Pro-24-PoE")]
    [InlineData("USLP48P", "USW-Pro-48-PoE")]
    public void GetProductName_ProSwitches_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USPM16", "USW-Pro-Max-16")]
    [InlineData("USPM16P", "USW-Pro-Max-16-PoE")]
    [InlineData("USPM24", "USW-Pro-Max-24")]
    [InlineData("USPM48P", "USW-Pro-Max-48-PoE")]
    public void GetProductName_ProMaxSwitches_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("US68P", "USW-Enterprise-8-PoE")]
    [InlineData("US624P", "USW-Enterprise-24-PoE")]
    [InlineData("US648P", "USW-Enterprise-48-PoE")]
    [InlineData("USXG24", "USW-EnterpriseXG-24")]
    public void GetProductName_EnterpriseSwitches_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USL8A", "USW-Aggregation")]
    [InlineData("USAGGPRO", "USW-Pro-Aggregation")]
    [InlineData("USXG", "US-16-XG")]
    public void GetProductName_AggregationSwitches_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("U7PRO", "U7-Pro")]
    [InlineData("U7PROMAX", "U7-Pro-Max")]
    [InlineData("U7PIW", "U7-Pro-Wall")]
    [InlineData("UKPW", "U7-Outdoor")]
    [InlineData("UAPA6A4", "U7-Pro-XGS")]
    [InlineData("UAPA6A6", "U7-Pro-Outdoor")]
    public void GetProductName_WiFi7APs_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UAP6MP", "U6-Pro")]
    [InlineData("UALR6", "U6-LR")]
    [InlineData("UAL6", "U6-Lite")]
    [InlineData("UAPL6", "U6+")]
    [InlineData("UAIW6", "U6-IW")]
    public void GetProductName_WiFi6APs_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("U6ENT", "U6-Enterprise")]
    [InlineData("U6ENTIW", "U6-Enterprise-IW")]
    [InlineData("U6M", "U6-Mesh")]
    public void GetProductName_WiFi6EAPs_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("U7PG2", "UAP-AC-Pro")]
    [InlineData("U7LR", "UAP-AC-LR")]
    [InlineData("U7LT", "UAP-AC-Lite")]
    [InlineData("U7MSH", "UAP-AC-M")]
    [InlineData("U7IW", "UAP-AC-IW")]
    public void GetProductName_ACAPs_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("U2S48", "UAP")]
    [InlineData("U2L48", "UAP-LR")]
    [InlineData("U2IW", "UAP-IW")]
    [InlineData("U2O", "UAP-Outdoor")]
    [InlineData("U5O", "UAP-Outdoor-5")]
    public void GetProductName_LegacyAPs_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UNVR4", "UNVR")]
    [InlineData("UNVRPRO", "UNVR-Pro")]
    [InlineData("UNASPRO", "UNAS-Pro")]
    public void GetProductName_NVRsAndNAS_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("ULTE", "U-LTE")]
    [InlineData("UMBBE630", "U5G-Max")]
    [InlineData("UMBBE631", "U5G-Max-Outdoor")]
    public void GetProductName_CellularDevices_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void GetProductName_UnknownModel_ReturnsOriginalCode()
    {
        // Arrange
        var unknownCode = "UNKNOWN-MODEL-XYZ";

        // Act
        var result = UniFiProductDatabase.GetProductName(unknownCode);

        // Assert
        result.Should().Be(unknownCode);
    }

    [Theory]
    [InlineData("udmpro", "UDM-Pro")]
    [InlineData("Udmpro", "UDM-Pro")]
    [InlineData("UDMPRO", "UDM-Pro")]
    public void GetProductName_CaseInsensitive(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    #endregion

    #region GetProductName Tests (Additional Official Codes)

    [Theory]
    [InlineData("UCKG2", "UCK-G2")]
    [InlineData("UCKP", "UCK-G2-Plus")]
    [InlineData("UCKENT", "CK-Enterprise")]
    public void GetProductName_CloudKeys_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UDW", "UDW")]
    [InlineData("UDMENT", "EFG")]
    public void GetProductName_DreamWallAndFortress_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("U7HD", "UAP-AC-HD")]
    [InlineData("U7SHD", "UAP-AC-SHD")]
    [InlineData("U7NHD", "UAP-nanoHD")]
    [InlineData("UFLHD", "UAP-FlexHD")]
    [InlineData("UHDIW", "UAP-IW-HD")]
    public void GetProductName_HDAPs_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UAPA697", "E7")]
    [InlineData("UAPA698", "E7-Campus")]
    [InlineData("UAPA699", "E7-Audience")]
    public void GetProductName_EnterpriseWiFi7_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UBB", "UBB")]
    [InlineData("UBBXG", "UBB-XG")]
    [InlineData("UDB", "UDB-Pro")]
    public void GetProductName_BuildingBridges_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UP1", "USP-Plug")]
    [InlineData("UP6", "USP-Strip")]
    public void GetProductName_SmartPower_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USPPDUP", "USP-PDU-Pro")]
    [InlineData("USPPDUHD", "USP-PDU-HD")]
    [InlineData("USPRPS", "USP-RPS")]
    public void GetProductName_PowerDistribution_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USWF066", "ECS-Aggregation")]
    [InlineData("USWF067", "ECS-24-PoE")]
    [InlineData("USWF069", "ECS-48-PoE")]
    public void GetProductName_EnterpriseCampus_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UDC48X6", "USW-Leaf")]
    public void GetProductName_DataCenterLeaf_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UAPA6B3", "U7-LR")]
    [InlineData("UAPA693", "U7-Lite")]
    [InlineData("UAPA6A5", "U7-IW")]
    public void GetProductName_WiFi7InternalCodes_ReturnsCorrectName(string modelCode, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductName(modelCode);

        // Assert
        result.Should().Be(expected);
    }

    #endregion

    #region GetProductNameFromShortname Tests (Legacy Codes)

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    public void GetProductNameFromShortname_NullOrEmpty_ReturnsUnknown(string? shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UDM-PRO", "UDM-Pro")]
    [InlineData("UDM-PRO-SE", "UDM-SE")]
    [InlineData("UDM-PRO-MAX", "UDM-Pro-Max")]
    [InlineData("UDMSE", "UDM-SE")]
    public void GetProductNameFromShortname_DreamMachineFamily_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UCGF", "UCG-Fiber")]
    [InlineData("UCG-ULTRA", "UCG-Ultra")]
    public void GetProductNameFromShortname_CloudGateways_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USG", "USG")]
    [InlineData("UGW", "USG")]
    public void GetProductNameFromShortname_SecurityGateways_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UXGLITE", "UXG-Lite")]
    [InlineData("UXGFIBER", "UXG-Fiber")]
    [InlineData("UXG-PRO", "UXG-Pro")]
    public void GetProductNameFromShortname_NextGenGateways_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UDR7", "UDR7")]
    [InlineData("UDR5G", "UDR-5G-Max")]
    public void GetProductNameFromShortname_DreamRouters_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("EXPRESS", "UX")]
    [InlineData("UX7", "UX7")]
    [InlineData("UXMAX", "UX7")]
    public void GetProductNameFromShortname_UniFiExpress_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USWFLEX", "USW-Flex")]
    [InlineData("USWFLEXMINI", "USW-Flex-Mini")]
    [InlineData("USW-FLEX-MINI", "USW-Flex-Mini")]
    public void GetProductNameFromShortname_FlexSwitches_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USM25G5", "USW-Flex-2.5G-5")]
    [InlineData("USM25G8", "USW-Flex-2.5G-8")]
    [InlineData("USM25G8P", "USW-Flex-2.5G-8-PoE")]
    public void GetProductNameFromShortname_Flex25GSwitches_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USWULTRA", "USW-Ultra")]
    public void GetProductNameFromShortname_UltraSwitches_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USWPRO24", "USW-Pro-24")]
    [InlineData("USWPRO24POE", "USW-Pro-24-PoE")]
    [InlineData("USWPRO48", "USW-Pro-48")]
    [InlineData("USWPRO48POE", "USW-Pro-48-PoE")]
    public void GetProductNameFromShortname_ProSwitches_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USWAGGREGATION", "USW-Aggregation")]
    [InlineData("USWAGGPRO", "USW-Pro-Aggregation")]
    [InlineData("US16XG", "US-16-XG")]
    public void GetProductNameFromShortname_AggregationSwitches_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("U7PROXGS", "U7-Pro-XGS")]
    [InlineData("U7PO", "U7-Pro-Outdoor")]
    public void GetProductNameFromShortname_WiFi7APs_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("U6PRO", "U6-Pro")]
    [InlineData("U6LR", "U6-LR")]
    [InlineData("U6LITE", "U6-Lite")]
    [InlineData("U6PLUS", "U6+")]
    public void GetProductNameFromShortname_WiFi6APs_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("U6ENTERPRISEB", "U6-Enterprise")]
    [InlineData("U6ENTERPRISEINWALL", "U6-Enterprise-IW")]
    [InlineData("U6MESH", "U6-Mesh")]
    public void GetProductNameFromShortname_WiFi6EAPs_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UAPPRO", "UAP-AC-Pro")]
    [InlineData("UAPLR", "UAP-AC-LR")]
    [InlineData("UAPLITE", "UAP-AC-Lite")]
    [InlineData("UAPM", "UAP-AC-M")]
    [InlineData("UAPMESH", "UAP-AC-M")]
    public void GetProductNameFromShortname_ACAPs_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("BZ2", "UAP")]
    [InlineData("BZ2LR", "UAP-LR")]
    public void GetProductNameFromShortname_LegacyAPs_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UNVR", "UNVR")]
    [InlineData("UNVR-PRO", "UNVR-Pro")]
    public void GetProductNameFromShortname_NVRs_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("ULTEPRO", "U-LTE")]
    [InlineData("U5GMAX", "U5G-Max")]
    public void GetProductNameFromShortname_CellularDevices_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UCK-G2", "UCK-G2")]
    [InlineData("UCK-G2-PLUS", "UCK-G2-Plus")]
    public void GetProductNameFromShortname_CloudKeys_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("EFG", "EFG")]
    public void GetProductNameFromShortname_Fortress_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("E7", "E7")]
    [InlineData("E7CAMPUS", "E7-Campus")]
    [InlineData("E7AUDIENCE", "E7-Audience")]
    public void GetProductNameFromShortname_EnterpriseWiFi7_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("UDBPRO", "UDB-Pro")]
    public void GetProductNameFromShortname_DeviceBridge_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USPPLUG", "USP-Plug")]
    [InlineData("USPSTRIP", "USP-Strip")]
    public void GetProductNameFromShortname_SmartPower_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("EAS24", "ECS-24-PoE")]
    [InlineData("EAS24P", "ECS-24-PoE")]
    [InlineData("EAS48", "ECS-48-PoE")]
    [InlineData("EAS48P", "ECS-48-PoE")]
    [InlineData("ECSAGG", "ECS-Aggregation")]
    public void GetProductNameFromShortname_EnterpriseCampus_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USW-LEAF", "USW-Leaf")]
    public void GetProductNameFromShortname_DataCenterLeaf_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("G7LR", "U7-LR")]
    [InlineData("G7LT", "U7-Lite")]
    [InlineData("G7IW", "U7-IW")]
    public void GetProductNameFromShortname_WiFi7InternalCodes_ReturnsCorrectName(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void GetProductNameFromShortname_UnknownShortname_ReturnsOriginal()
    {
        // Arrange
        var unknownCode = "UNKNOWN-SHORTNAME-XYZ";

        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(unknownCode);

        // Assert
        result.Should().Be(unknownCode);
    }

    [Theory]
    [InlineData("udm-pro", "UDM-Pro")]
    [InlineData("Udm-Pro", "UDM-Pro")]
    [InlineData("UDM-PRO", "UDM-Pro")]
    public void GetProductNameFromShortname_CaseInsensitive(string shortname, string expected)
    {
        // Act
        var result = UniFiProductDatabase.GetProductNameFromShortname(shortname);

        // Assert
        result.Should().Be(expected);
    }

    #endregion

    #region GetBestProductName Tests

    [Fact]
    public void GetBestProductName_KnownModel_ReturnsModelLookup()
    {
        // Arrange - model lookup takes priority over shortname
        var model = "UDMPRO";
        var shortname = "UDM-PRO";

        // Act
        var result = UniFiProductDatabase.GetBestProductName(model, shortname);

        // Assert - model lookup wins
        result.Should().Be("UDM-Pro");
    }

    [Fact]
    public void GetBestProductName_NoMatchingModel_UsesShortnameLookup()
    {
        // Arrange - when model doesn't match, falls back to shortname lookup
        var model = "unknown-model";
        var shortname = "UDM-PRO";

        // Act
        var result = UniFiProductDatabase.GetBestProductName(model, shortname);

        // Assert
        result.Should().Be("UDM-Pro");
    }

    [Fact]
    public void GetBestProductName_NoLookupMatches_FallsBackToShortname()
    {
        // Arrange
        var model = "unknown-model";
        var shortname = "fallback-shortname";

        // Act
        var result = UniFiProductDatabase.GetBestProductName(model, shortname);

        // Assert
        result.Should().Be("fallback-shortname");
    }

    [Fact]
    public void GetBestProductName_OnlyModel_FallsBackToModel()
    {
        // Arrange
        var model = "unknown-model";
        string? shortname = null;

        // Act
        var result = UniFiProductDatabase.GetBestProductName(model, shortname);

        // Assert
        result.Should().Be("unknown-model");
    }

    [Fact]
    public void GetBestProductName_AllNull_ReturnsUnknown()
    {
        // Act
        var result = UniFiProductDatabase.GetBestProductName(null, null);

        // Assert
        result.Should().Be("Unknown");
    }

    [Fact]
    public void GetBestProductName_OfficialModel_PrefersOverLegacyShortname()
    {
        // Arrange - UDMPRO is official, UDM-PRO is legacy alias
        // Both map to "UDM-Pro" but official should be checked first
        var model = "UDMPRO";
        var shortname = "UDM-PRO";

        // Act
        var result = UniFiProductDatabase.GetBestProductName(model, shortname);

        // Assert
        result.Should().Be("UDM-Pro");
    }

    [Fact]
    public void GetBestProductName_OnlyLegacyShortname_StillWorks()
    {
        // Arrange - Model is not in official, but shortname is in legacy
        var model = "UNKNOWN123";
        var shortname = "UDM-PRO";

        // Act
        var result = UniFiProductDatabase.GetBestProductName(model, shortname);

        // Assert
        result.Should().Be("UDM-Pro");
    }

    #endregion

    #region CanRunIperf3 Tests (Single Parameter)

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void CanRunIperf3_NullOrEmpty_ReturnsTrue(string? productName, bool expected)
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(productName);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("USW-Flex")]
    [InlineData("USW-Flex-Mini")]
    [InlineData("USW-Flex-XG")]
    [InlineData("USW-Flex-2.5G-5")]
    [InlineData("USW-Flex-2.5G-8")]
    [InlineData("USW-Flex-2.5G-8-PoE")]
    public void CanRunIperf3_FlexSwitches_ReturnsFalse(string productName)
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(productName);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("USW-Ultra")]
    [InlineData("USW-Ultra-60W")]
    [InlineData("USW-Ultra-210W")]
    public void CanRunIperf3_UltraSwitches_ReturnsFalse(string productName)
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(productName);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("USW-Lite-8-PoE")]
    [InlineData("USW-Lite-16-PoE")]
    public void CanRunIperf3_LiteSwitches_ReturnsFalse(string productName)
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(productName);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("USW-Industrial")]
    [InlineData("USW-Pro-XG-8-PoE")]
    [InlineData("USW-Pro-Max-16")]
    [InlineData("USW-Pro-Max-16-PoE")]
    public void CanRunIperf3_IndustrialAndProMax_ReturnsFalse(string productName)
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(productName);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("US-8")]
    [InlineData("US-8-60W")]
    [InlineData("US-8-150W")]
    public void CanRunIperf3_LegacyUS8Switches_ReturnsFalse(string productName)
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(productName);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("USW-24-PoE")]
    [InlineData("USW-Enterprise-8-PoE")]
    [InlineData("USW-Aggregation")]
    public void CanRunIperf3_EnterpriseAndAggregation_ReturnsFalse(string productName)
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(productName);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("UAP")]
    [InlineData("UAP-LR")]
    [InlineData("UAP-IW")]
    [InlineData("UAP-Outdoor")]
    [InlineData("UAP-Outdoor+")]
    [InlineData("UAP-Outdoor-5")]
    public void CanRunIperf3_LegacyUAPs_ReturnsFalse(string productName)
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(productName);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("UAP-AC-Pro")]
    [InlineData("UAP-AC-Lite")]
    [InlineData("UAP-AC-LR")]
    [InlineData("UAP-AC-M")]
    [InlineData("UAP-AC-IW")]
    [InlineData("UAP-AC-EDU")]
    [InlineData("UAP-AC-Outdoor")]
    public void CanRunIperf3_ACAPs_ReturnsFalse(string productName)
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(productName);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("UDM-Pro")]
    [InlineData("UDM-SE")]
    [InlineData("USW-Pro-24")]
    [InlineData("USW-Pro-48-PoE")]
    [InlineData("U6-Pro")]
    [InlineData("U7-Pro")]
    public void CanRunIperf3_SupportedDevices_ReturnsTrue(string productName)
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(productName);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("usw-flex-mini")]
    [InlineData("USW-FLEX-MINI")]
    [InlineData("Usw-Flex-Mini")]
    public void CanRunIperf3_CaseInsensitive(string productName)
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(productName);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region CanRunIperf3 Tests (Two Parameters)

    [Fact]
    public void CanRunIperf3_TwoParams_UsesGetBestProductName()
    {
        // Arrange - USW-Flex-Mini doesn't support iperf3
        var model = "USMINI";
        var shortname = "USW-FLEX-MINI";

        // Act
        var result = UniFiProductDatabase.CanRunIperf3(model, shortname);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanRunIperf3_TwoParams_SupportedDevice_ReturnsTrue()
    {
        // Arrange - UDM-Pro supports iperf3
        var model = "UDMPRO";
        var shortname = "UDM-PRO";

        // Act
        var result = UniFiProductDatabase.CanRunIperf3(model, shortname);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanRunIperf3_TwoParams_AllNull_ReturnsTrue()
    {
        // Act
        var result = UniFiProductDatabase.CanRunIperf3(null, null);

        // Assert
        result.Should().BeTrue();  // Unknown device defaults to true
    }

    #endregion

    #region IsCellularModem Tests (Single Parameter)

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsCellularModem_NullOrEmpty_ReturnsFalse(string? modelCode)
    {
        // Act
        var result = UniFiProductDatabase.IsCellularModem(modelCode);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("ULTE")]
    [InlineData("ULTEPUS")]
    [InlineData("ULTEPEU")]
    [InlineData("UMBBE630")]
    [InlineData("UMBBE631")]
    [InlineData("U5GMAX")]
    [InlineData("ULTEPRO")]
    public void IsCellularModem_OfficialModemCodes_ReturnsTrue(string modelCode)
    {
        // Act
        var result = UniFiProductDatabase.IsCellularModem(modelCode);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("ulte")]
    [InlineData("Ulte")]
    [InlineData("ULTE")]
    public void IsCellularModem_CaseInsensitive(string modelCode)
    {
        // Act
        var result = UniFiProductDatabase.IsCellularModem(modelCode);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("UDMPRO")]
    [InlineData("USW-Pro-24")]
    [InlineData("U7-Pro")]
    [InlineData("UAP-AC-Pro")]
    public void IsCellularModem_NonModemDevices_ReturnsFalse(string modelCode)
    {
        // Act
        var result = UniFiProductDatabase.IsCellularModem(modelCode);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region IsCellularModem Tests (Three Parameters)

    [Theory]
    [InlineData("ULTE", null, null)]
    [InlineData(null, "ULTE", null)]
    [InlineData("UMBBE630", "U5GMAX", null)]
    public void IsCellularModem_ThreeParams_ModelOrShortname_ReturnsTrue(string? model, string? shortname, string? deviceType)
    {
        // Act
        var result = UniFiProductDatabase.IsCellularModem(model, shortname, deviceType);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, null, "umbb")]
    [InlineData(null, null, "UMBB")]
    [InlineData("unknown", "unknown", "umbb")]
    public void IsCellularModem_ThreeParams_UmbbDeviceType_ReturnsTrue(string? model, string? shortname, string? deviceType)
    {
        // Act
        var result = UniFiProductDatabase.IsCellularModem(model, shortname, deviceType);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsCellularModem_ThreeParams_AllNull_ReturnsFalse()
    {
        // Act
        var result = UniFiProductDatabase.IsCellularModem(null, null, null);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("UDMPRO", "UDM-PRO", "ugw")]
    [InlineData("USW-Pro-24", null, "usw")]
    [InlineData(null, null, "uap")]
    public void IsCellularModem_ThreeParams_NonModemDevices_ReturnsFalse(string? model, string? shortname, string? deviceType)
    {
        // Act
        var result = UniFiProductDatabase.IsCellularModem(model, shortname, deviceType);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsCellularModem_ThreeParams_RealWorldU5GMax_ReturnsTrue()
    {
        // Arrange - typical U5G-Max API response
        var model = "UMBBE630";
        var shortname = "U5GMAX";
        var deviceType = "umbb";

        // Act
        var result = UniFiProductDatabase.IsCellularModem(model, shortname, deviceType);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsCellularModem_ThreeParams_RealWorldULTE_ReturnsTrue()
    {
        // Arrange - typical U-LTE API response
        var model = "ULTE";
        string? shortname = null;
        var deviceType = "umbb";

        // Act
        var result = UniFiProductDatabase.IsCellularModem(model, shortname, deviceType);

        // Assert
        result.Should().BeTrue();
    }

    #endregion
}

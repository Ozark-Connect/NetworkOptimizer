using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class DisplayFormattersTests
{
    #region StripDevicePrefix Tests

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "  ")] // Whitespace is preserved (not trimmed to empty)
    public void StripDevicePrefix_NullOrWhitespace_ReturnsEmptyOrOriginal(string? input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("[Gateway] Main Network", "Main Network")]
    [InlineData("[Switch] Office", "Office")]
    [InlineData("[AP] Living Room", "Living Room")]
    [InlineData("[Router] Edge", "Edge")]
    [InlineData("[RTR] Edge", "Edge")]
    public void StripDevicePrefix_BracketedPrefix_StripsPrefix(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("(Switch) Office", "Office")]
    [InlineData("(Gateway) Main", "Main")]
    [InlineData("(AP) Bedroom", "Bedroom")]
    public void StripDevicePrefix_ParentheticalPrefix_StripsPrefix(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Switch - Office", "Office")]
    [InlineData("Gateway - Main", "Main")]
    [InlineData("AP - Living Room", "Living Room")]
    public void StripDevicePrefix_DashSeparatedPrefix_StripsPrefix(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Switch: Office", "Office")]
    [InlineData("Gateway: Main", "Main")]
    [InlineData("AP: Living Room", "Living Room")]
    public void StripDevicePrefix_ColonSeparatedPrefix_StripsPrefix(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("No Prefix Here", "No Prefix Here")]
    [InlineData("Just A Name", "Just A Name")]
    [InlineData("Living Room", "Living Room")]
    public void StripDevicePrefix_NoPrefixOrSuffix_ReturnsOriginal(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("[]", "[]")]
    [InlineData("[", "[")]
    [InlineData("(Random) Text", "(Random) Text")] // Random is not a device keyword
    [InlineData("{Random} Text", "{Random} Text")] // Random is not a device keyword
    public void StripDevicePrefix_InvalidPrefixFormat_ReturnsOriginal(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("{AP} Living Room", "Living Room")]
    [InlineData("{Switch} Office", "Office")]
    [InlineData("{Gateway} Main", "Main")]
    public void StripDevicePrefix_CurlyBracePrefix_StripsPrefix(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Office Switch", "Office")]
    [InlineData("Living Room AP", "Living Room")]
    [InlineData("Main Gateway", "Main")]
    [InlineData("Edge Router", "Edge")]
    [InlineData("Edge RTR", "Edge")]
    public void StripDevicePrefix_PlainSuffix_StripsSuffix(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Office [Switch]", "Office")]
    [InlineData("Living Room [AP]", "Living Room")]
    [InlineData("Main [Gateway]", "Main")]
    public void StripDevicePrefix_BracketedSuffix_StripsSuffix(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Office (Switch)", "Office")]
    [InlineData("Living Room (AP)", "Living Room")]
    [InlineData("Main (Gateway)", "Main")]
    public void StripDevicePrefix_ParentheticalSuffix_StripsSuffix(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Office {Switch}", "Office")]
    [InlineData("Living Room {AP}", "Living Room")]
    [InlineData("Main {Gateway}", "Main")]
    public void StripDevicePrefix_CurlyBraceSuffix_StripsSuffix(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("[AP] Living Room AP", "Living Room")] // Both prefix and suffix
    [InlineData("(Switch) Office Switch", "Office")] // Both prefix and suffix
    [InlineData("{Gateway} Main [Gateway]", "Main")] // Mixed prefix and suffix
    public void StripDevicePrefix_BothPrefixAndSuffix_StripsBoth(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("ap - Living Room", "Living Room")] // lowercase
    [InlineData("SWITCH - Office", "Office")] // uppercase
    [InlineData("Office ap", "Office")] // lowercase suffix
    [InlineData("Main GATEWAY", "Main")] // uppercase suffix
    public void StripDevicePrefix_CaseInsensitive_StripsCorrectly(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Office (Model)", "Office (Model)")] // Model is not a device keyword
    [InlineData("Living Room (U6-Pro)", "Living Room (U6-Pro)")] // Model suffix preserved
    [InlineData("Main (UCG-Fiber)", "Main (UCG-Fiber)")] // Model suffix preserved
    public void StripDevicePrefix_NonKeywordSuffix_PreservesSuffix(string input, string expected)
    {
        var result = DisplayFormatters.StripDevicePrefix(input);
        result.Should().Be(expected);
    }

    #endregion

    #region ExtractNetworkName Tests

    [Theory]
    [InlineData(null, "Network")]
    [InlineData("", "Network")]
    [InlineData("  ", "Network")]
    public void ExtractNetworkName_NullOrWhitespace_ReturnsNetwork(string? input, string expected)
    {
        var result = DisplayFormatters.ExtractNetworkName(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("[Gateway] Home Network", "Home Network")]
    [InlineData("[Gateway] Main (UCG-Fiber)", "Main")]
    [InlineData("[Gateway] Office (UDM Pro)", "Office")]
    public void ExtractNetworkName_WithPrefixAndSuffix_ExtractsCleanName(string input, string expected)
    {
        var result = DisplayFormatters.ExtractNetworkName(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void ExtractNetworkName_OnlyBrackets_ReturnsOriginalBecauseNoContentAfterBracket()
    {
        // Note: The current implementation doesn't strip bracket-only prefixes if there's
        // nothing after them. "[Gateway]" returns "[Gateway]" because closeBracket < name.Length - 1 fails.
        var result = DisplayFormatters.ExtractNetworkName("[Gateway]");
        result.Should().Be("[Gateway]");
    }

    #endregion

    #region FormatDeviceName Tests

    [Theory]
    [InlineData("Main Network", true, false, "[Gateway] Main Network")]
    [InlineData("Office", false, false, "[Switch] Office")]
    [InlineData("Living Room", false, true, "[AP] Living Room")]
    public void FormatDeviceName_DifferentTypes_FormatsCorrectly(
        string name, bool isGateway, bool isAP, string expected)
    {
        var result = DisplayFormatters.FormatDeviceName(name, isGateway, isAP);
        result.Should().Be(expected);
    }

    [Fact]
    public void FormatDeviceName_AlreadyPrefixed_StripsAndReapplies()
    {
        var result = DisplayFormatters.FormatDeviceName("[Switch] Office", true, false);
        result.Should().Be("[Gateway] Office");
    }

    [Fact]
    public void FormatDeviceName_GatewayOverridesAP()
    {
        // When both isGateway and isAP are true, Gateway takes precedence
        var result = DisplayFormatters.FormatDeviceName("Device", true, true);
        result.Should().Be("[Gateway] Device");
    }

    #endregion

    #region ParseDeviceOnNetworkDevice Tests

    [Fact]
    public void ParseDeviceOnNetworkDevice_Null_ReturnsEmptyTuple()
    {
        var (clientName, deviceType, networkDeviceName) = DisplayFormatters.ParseDeviceOnNetworkDevice(null);
        clientName.Should().BeEmpty();
        deviceType.Should().BeNull();
        networkDeviceName.Should().BeNull();
    }

    [Fact]
    public void ParseDeviceOnNetworkDevice_Empty_ReturnsEmptyTuple()
    {
        var (clientName, deviceType, networkDeviceName) = DisplayFormatters.ParseDeviceOnNetworkDevice("");
        clientName.Should().BeEmpty();
        deviceType.Should().BeNull();
        networkDeviceName.Should().BeNull();
    }

    [Fact]
    public void ParseDeviceOnNetworkDevice_Whitespace_ReturnsWhitespace()
    {
        // Note: The implementation preserves whitespace - it doesn't trim to empty
        var (clientName, deviceType, networkDeviceName) = DisplayFormatters.ParseDeviceOnNetworkDevice("  ");
        clientName.Should().Be("  ");
        deviceType.Should().BeNull();
        networkDeviceName.Should().BeNull();
    }

    [Fact]
    public void ParseDeviceOnNetworkDevice_NoOnPattern_ReturnsOriginal()
    {
        var (clientName, deviceType, networkDeviceName) =
            DisplayFormatters.ParseDeviceOnNetworkDevice("[IoT] Thermostat");

        clientName.Should().Be("[IoT] Thermostat");
        deviceType.Should().BeNull();
        networkDeviceName.Should().BeNull();
    }

    [Fact]
    public void ParseDeviceOnNetworkDevice_ValidPattern_ParsesCorrectly()
    {
        var (clientName, deviceType, networkDeviceName) =
            DisplayFormatters.ParseDeviceOnNetworkDevice("[IoT] Thermostat on [Switch] Office");

        clientName.Should().Be("[IoT] Thermostat");
        deviceType.Should().Be("Switch");
        networkDeviceName.Should().Be("Office");
    }

    [Fact]
    public void ParseDeviceOnNetworkDevice_WithBandSuffix_StripsBand()
    {
        var (clientName, deviceType, networkDeviceName) =
            DisplayFormatters.ParseDeviceOnNetworkDevice("[IoT] Camera on [AP] Living Room (5 GHz)");

        clientName.Should().Be("[IoT] Camera");
        deviceType.Should().Be("AP");
        networkDeviceName.Should().Be("Living Room");
    }

    [Fact]
    public void ParseDeviceOnNetworkDevice_With24GHzBand_StripsBand()
    {
        var (clientName, deviceType, networkDeviceName) =
            DisplayFormatters.ParseDeviceOnNetworkDevice("Device on [AP] Hallway (2.4 GHz)");

        clientName.Should().Be("Device");
        deviceType.Should().Be("AP");
        networkDeviceName.Should().Be("Hallway");
    }

    #endregion

    #region GetNetworkDeviceLabel Tests

    [Theory]
    [InlineData(null, "Device:")]
    [InlineData("", "Device:")]
    [InlineData("  ", "Device:")]
    public void GetNetworkDeviceLabel_NullOrWhitespace_ReturnsDevice(string? input, string expected)
    {
        var result = DisplayFormatters.GetNetworkDeviceLabel(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("AP", "AP:")]
    [InlineData("ap", "AP:")]
    [InlineData("Ap", "AP:")]
    [InlineData("SWITCH", "Switch:")]
    [InlineData("switch", "Switch:")]
    [InlineData("Switch", "Switch:")]
    [InlineData("GATEWAY", "Gateway:")]
    [InlineData("gateway", "Gateway:")]
    [InlineData("Gateway", "Gateway:")]
    public void GetNetworkDeviceLabel_KnownTypes_ReturnsFormattedLabel(string input, string expected)
    {
        var result = DisplayFormatters.GetNetworkDeviceLabel(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Router", "Router:")]
    [InlineData("Firewall", "Firewall:")]
    [InlineData("Custom", "Custom:")]
    public void GetNetworkDeviceLabel_UnknownTypes_ReturnsWithColon(string input, string expected)
    {
        var result = DisplayFormatters.GetNetworkDeviceLabel(input);
        result.Should().Be(expected);
    }

    #endregion

    #region FormatNetworkWithVlan Tests

    [Fact]
    public void FormatNetworkWithVlan_WithVlanId_FormatsCorrectly()
    {
        var result = DisplayFormatters.FormatNetworkWithVlan("Main Network", 10);
        result.Should().Be("Main Network (10)");
    }

    [Fact]
    public void FormatNetworkWithVlan_NoVlanId_ReturnsNetworkName()
    {
        var result = DisplayFormatters.FormatNetworkWithVlan("Main Network", null);
        result.Should().Be("Main Network");
    }

    [Fact]
    public void FormatNetworkWithVlan_NullNetworkName_ReturnsUnknown()
    {
        var result = DisplayFormatters.FormatNetworkWithVlan(null, 10);
        result.Should().Be("Unknown (10)");
    }

    [Fact]
    public void FormatNetworkWithVlan_NullBoth_ReturnsUnknown()
    {
        var result = DisplayFormatters.FormatNetworkWithVlan(null, null);
        result.Should().Be("Unknown");
    }

    #endregion

    #region FormatVlanDisplay Tests

    [Fact]
    public void FormatVlanDisplay_NativeVlan_ShowsNativeIndicator()
    {
        var result = DisplayFormatters.FormatVlanDisplay(1);
        result.Should().Be("1 (native)");
    }

    [Theory]
    [InlineData(10, "10")]
    [InlineData(100, "100")]
    [InlineData(4094, "4094")]
    public void FormatVlanDisplay_NonNativeVlan_ShowsJustNumber(int vlanId, string expected)
    {
        var result = DisplayFormatters.FormatVlanDisplay(vlanId);
        result.Should().Be(expected);
    }

    #endregion

    #region GetLinkStatus Tests

    [Fact]
    public void GetLinkStatus_NotUp_ReturnsDown()
    {
        var result = DisplayFormatters.GetLinkStatus(false, 1000);
        result.Should().Be("Down");
    }

    [Theory]
    [InlineData(true, 0, "Down")]
    [InlineData(true, 100, "Up 100 MbE")]
    [InlineData(true, 1000, "Up 1 GbE")]
    [InlineData(true, 2500, "Up 2.5 GbE")]
    [InlineData(true, 10000, "Up 10 GbE")]
    [InlineData(true, 25000, "Up 25 GbE")]
    public void GetLinkStatus_VariousSpeeds_FormatsCorrectly(bool isUp, int speed, string expected)
    {
        var result = DisplayFormatters.GetLinkStatus(isUp, speed);
        result.Should().Be(expected);
    }

    #endregion

    #region GetPoeStatus Tests

    [Fact]
    public void GetPoeStatus_WithPower_ShowsWatts()
    {
        var result = DisplayFormatters.GetPoeStatus(15.5, "auto", true);
        result.Should().Be("15.5 W");
    }

    [Fact]
    public void GetPoeStatus_ModeOff_ShowsOff()
    {
        var result = DisplayFormatters.GetPoeStatus(0, "off", true);
        result.Should().Be("off");
    }

    [Fact]
    public void GetPoeStatus_EnabledButNoPower_ShowsOff()
    {
        var result = DisplayFormatters.GetPoeStatus(0, "auto", true);
        result.Should().Be("off");
    }

    [Fact]
    public void GetPoeStatus_NotEnabled_ShowsNA()
    {
        var result = DisplayFormatters.GetPoeStatus(0, "auto", false);
        result.Should().Be("N/A");
    }

    #endregion

    #region GetPortSecurityStatus Tests

    [Theory]
    [InlineData(0, true, "Yes")]
    [InlineData(0, false, "-")]
    [InlineData(1, true, "1 MAC")]
    [InlineData(1, false, "1 MAC")]
    [InlineData(5, true, "5 MAC")]
    [InlineData(5, false, "5 MAC")]
    public void GetPortSecurityStatus_VariousConfigs_FormatsCorrectly(
        int macCount, bool enabled, string expected)
    {
        var result = DisplayFormatters.GetPortSecurityStatus(macCount, enabled);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("auto", "802.1X")]
    [InlineData("mac_based", "802.1X")]
    public void GetPortSecurityStatus_Dot1xSecured_Returns8021X(string dot1xCtrl, string expected)
    {
        var result = DisplayFormatters.GetPortSecurityStatus(0, false, dot1xCtrl);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("force_authorized", "-")]
    [InlineData("force_unauthorized", "-")]
    [InlineData(null, "-")]
    public void GetPortSecurityStatus_NonSecuredDot1x_ReturnsDash(string? dot1xCtrl, string expected)
    {
        var result = DisplayFormatters.GetPortSecurityStatus(0, false, dot1xCtrl);
        result.Should().Be(expected);
    }

    [Fact]
    public void GetPortSecurityStatus_MacCountTakesPriorityOverDot1x()
    {
        // If MACs are configured, show MAC count even with 802.1X
        var result = DisplayFormatters.GetPortSecurityStatus(3, false, "auto");
        result.Should().Be("3 MAC");
    }

    #endregion

    #region GetIsolationStatus Tests

    [Theory]
    [InlineData(true, "Yes")]
    [InlineData(false, "-")]
    public void GetIsolationStatus_BooleanValue_FormatsCorrectly(bool isolation, string expected)
    {
        var result = DisplayFormatters.GetIsolationStatus(isolation);
        result.Should().Be(expected);
    }

    #endregion

    #region DNS Display Methods Tests

    [Fact]
    public void GetWanDnsDisplay_NotConfigured_ReturnsNotConfigured()
    {
        var result = DisplayFormatters.GetWanDnsDisplay(
            new List<string>(),
            new List<string?>(),
            new List<string>(),
            new List<string>(),
            new List<string>(),
            new List<string>(),
            null,
            null,
            false,
            true);

        result.Should().Be("Not Configured");
    }

    [Fact]
    public void GetWanDnsDisplay_MatchedServers_ShowsProviderInfo()
    {
        var result = DisplayFormatters.GetWanDnsDisplay(
            new List<string> { "1.1.1.1", "1.0.0.1" },
            new List<string?> { "dns1.cloudflare.com", "dns2.cloudflare.com" },
            new List<string> { "1.1.1.1", "1.0.0.1" },
            new List<string>(),
            new List<string>(),
            new List<string>(),
            "Cloudflare",
            null,
            true,
            true);

        result.Should().Contain("1.1.1.1");
        result.Should().Contain("Cloudflare");
    }

    [Fact]
    public void GetWanDnsDisplay_WrongOrder_ShowsCorrectPrefix()
    {
        var result = DisplayFormatters.GetWanDnsDisplay(
            new List<string> { "1.0.0.1", "1.1.1.1" },
            new List<string?> { "dns2.cloudflare.com", "dns1.cloudflare.com" },
            new List<string> { "1.0.0.1", "1.1.1.1" },
            new List<string>(),
            new List<string>(),
            new List<string>(),
            "Cloudflare",
            null,
            true,
            false);

        result.Should().Contain("Correct to:");
    }

    [Fact]
    public void GetWanDnsDisplay_WithMismatch_ShowsIncorrect()
    {
        var result = DisplayFormatters.GetWanDnsDisplay(
            new List<string> { "1.1.1.1", "8.8.8.8" },
            new List<string?>(),
            new List<string> { "1.1.1.1" },
            new List<string> { "8.8.8.8" },
            new List<string> { "WAN2" },
            new List<string>(),
            "Cloudflare",
            null,
            false,
            true);

        result.Should().Contain("Incorrect:");
        result.Should().Contain("8.8.8.8");
        result.Should().Contain("WAN2");
    }

    [Fact]
    public void GetWanDnsStatus_NotConfigured_ReturnsNotConfigured()
    {
        var result = DisplayFormatters.GetWanDnsStatus(new List<string>(), false, true);
        result.Should().Be("Not Configured");
    }

    [Fact]
    public void GetWanDnsStatus_MatchedCorrectOrder_ReturnsMatched()
    {
        var result = DisplayFormatters.GetWanDnsStatus(new List<string> { "1.1.1.1" }, true, true);
        result.Should().Be("Matched");
    }

    [Fact]
    public void GetWanDnsStatus_MatchedWrongOrder_ReturnsWrongOrder()
    {
        var result = DisplayFormatters.GetWanDnsStatus(new List<string> { "1.1.1.1" }, true, false);
        result.Should().Be("Wrong Order");
    }

    [Fact]
    public void GetWanDnsStatus_Mismatched_ReturnsMismatched()
    {
        var result = DisplayFormatters.GetWanDnsStatus(new List<string> { "8.8.8.8" }, false, true);
        result.Should().Be("Mismatched");
    }

    [Fact]
    public void GetDeviceDnsDisplay_NoDevices_ReturnsNoDevices()
    {
        var result = DisplayFormatters.GetDeviceDnsDisplay(0, 0, 0, true);
        result.Should().Be("No infrastructure devices to check");
    }

    [Fact]
    public void GetDeviceDnsDisplay_AllPointToGateway_ReturnsCorrectMessage()
    {
        var result = DisplayFormatters.GetDeviceDnsDisplay(5, 5, 0, true);
        result.Should().Contain("5 static IP device(s) point to gateway");
    }

    [Fact]
    public void GetDeviceDnsDisplay_SomeMisconfigured_ShowsCount()
    {
        var result = DisplayFormatters.GetDeviceDnsDisplay(5, 3, 0, false);
        result.Should().Contain("2 of 5 have non-gateway DNS");
    }

    [Fact]
    public void GetDeviceDnsDisplay_WithDhcpDevices_ShowsDhcpCount()
    {
        var result = DisplayFormatters.GetDeviceDnsDisplay(0, 0, 3, true);
        result.Should().Contain("3 use DHCP");
    }

    [Fact]
    public void GetDeviceDnsStatus_NoDevices_ReturnsNoDevices()
    {
        var result = DisplayFormatters.GetDeviceDnsStatus(0, 0, true);
        result.Should().Be("No Devices");
    }

    [Fact]
    public void GetDeviceDnsStatus_Correct_ReturnsCorrect()
    {
        var result = DisplayFormatters.GetDeviceDnsStatus(5, 0, true);
        result.Should().Be("Correct");
    }

    [Fact]
    public void GetDeviceDnsStatus_Misconfigured_ReturnsMisconfigured()
    {
        var result = DisplayFormatters.GetDeviceDnsStatus(5, 0, false);
        result.Should().Be("Misconfigured");
    }

    [Fact]
    public void GetDohStatusDisplay_NotEnabled_ReturnsNotConfigured()
    {
        var result = DisplayFormatters.GetDohStatusDisplay(false, "off", new List<string>());
        result.Should().Be("Not Configured");
    }

    [Fact]
    public void GetDohStatusDisplay_WithProviders_ShowsProviders()
    {
        var result = DisplayFormatters.GetDohStatusDisplay(true, "on", new List<string> { "NextDNS" });
        result.Should().Be("NextDNS");
    }

    [Fact]
    public void GetDohStatusDisplay_AutoMode_ShowsAutoSuffix()
    {
        var result = DisplayFormatters.GetDohStatusDisplay(true, "auto", new List<string> { "Cloudflare" });
        result.Should().Be("Cloudflare (auto mode)");
    }

    [Fact]
    public void GetDohStatusDisplay_WithConfigNames_ShowsConfigNames()
    {
        var result = DisplayFormatters.GetDohStatusDisplay(
            true, "on",
            new List<string> { "NextDNS" },
            new List<string> { "NextDNS-abc123" });
        result.Should().Contain("NextDNS-abc123");
    }

    [Fact]
    public void GetProtectionStatusDisplay_FullProtection_ReturnsFullProtection()
    {
        var result = DisplayFormatters.GetProtectionStatusDisplay(
            true, true, true, true, true, true);
        result.Should().Be("Full Protection");
    }

    [Fact]
    public void GetProtectionStatusDisplay_PartialProtection_ShowsProtectionList()
    {
        var result = DisplayFormatters.GetProtectionStatusDisplay(
            false, true, true, false, true, true);
        result.Should().Contain("DNS53");
        result.Should().Contain("DoT");
        result.Should().Contain("WAN DNS");
        result.Should().Contain("+");
    }

    [Fact]
    public void GetProtectionStatusDisplay_OnlyDoH_ShowsDoHOnly()
    {
        var result = DisplayFormatters.GetProtectionStatusDisplay(
            false, false, false, false, false, true);
        result.Should().Be("DoH Only - No Leak Prevention");
    }

    [Fact]
    public void GetProtectionStatusDisplay_NoProtection_ReturnsNotProtected()
    {
        var result = DisplayFormatters.GetProtectionStatusDisplay(
            false, false, false, false, false, false);
        result.Should().Be("Not Protected");
    }

    #endregion

    #region GetCorrectDnsOrder Tests

    [Fact]
    public void GetCorrectDnsOrder_WithDns2First_ReordersToDns1First()
    {
        var servers = new List<string> { "1.0.0.1", "1.1.1.1" };
        var ptrResults = new List<string?> { "dns2.cloudflare.com", "dns1.cloudflare.com" };

        var result = DisplayFormatters.GetCorrectDnsOrder(servers, ptrResults);

        result.Should().Be("1.1.1.1, 1.0.0.1");
    }

    [Fact]
    public void GetCorrectDnsOrder_AlreadyCorrect_ReturnsSameOrder()
    {
        var servers = new List<string> { "1.1.1.1", "1.0.0.1" };
        var ptrResults = new List<string?> { "dns1.cloudflare.com", "dns2.cloudflare.com" };

        var result = DisplayFormatters.GetCorrectDnsOrder(servers, ptrResults);

        result.Should().Be("1.1.1.1, 1.0.0.1");
    }

    #endregion

    #region FormatOrdinal Tests

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(22, "22nd")]
    [InlineData(23, "23rd")]
    [InlineData(28, "28th")]
    public void FormatOrdinal_ReturnsCorrectSuffix(int number, string expected)
    {
        DisplayFormatters.FormatOrdinal(number).Should().Be(expected);
    }

    #endregion
}

using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// Unit tests for <see cref="InseegoUbusParser"/>. Fixture JSON mirrors the shape of the ubus
/// JSON-RPC responses an Inseego FX4100 returns (verified against a browser trace) with
/// synthetic cell, carrier, and firmware values.
/// </summary>
public class InseegoUbusParserTests
{
    private static readonly ModemPollContext Context = new()
    {
        Id = 1,
        Name = "Test Gateway",
        Host = "192.0.2.1",  // RFC 5737 documentation address
        ModemType = "Inseego FX",
    };

    // Result objects keyed by method, in the shape the gateway returns them.
    private const string ServiceStats = """
        {"tech":17,"roam":0,"rssi":0,"dbm":0,"bar":4,"ecio":0,"pci":123,"sinr":0,"rsrp":-95,"rsrq":-12,"snr":18,"tx_power":0,"radio_temp":0,"oper_name":"TestCarrier","oper_id":"001010","cell_id":"1234567","femto":0,"p_rev":0}
        """;
    private const string ServiceStatus = """{"status":2}""";
    private const string ModelName = """{"model":"FX4100"}""";
    private const string HardwareInfo = """{"hw_version":"1","model":"FX4120","manufacturer":"Inseego","manufacturer_oui":"0015FF"}""";
    private const string SystemVersion = """{"modem_fw_version":"TEST-1.0","mifios_version":"1.2.3.4","webui_version":"1.2.3.4"}""";

    private static Dictionary<int, JsonElement> Results(
        string? serviceStats = ServiceStats,
        string? serviceStatus = ServiceStatus,
        string? modelName = ModelName,
        string? hardwareInfo = HardwareInfo,
        string? systemVersion = SystemVersion)
    {
        var byMethod = new Dictionary<string, string?>
        {
            ["get_cellular_service_stats"] = serviceStats,
            ["get_cellular_service_status"] = serviceStatus,
            ["get_device_model_name"] = modelName,
            ["get_hardware_info"] = hardwareInfo,
            ["get_system_version"] = systemVersion,
        };

        var results = new Dictionary<int, JsonElement>();
        for (var i = 0; i < InseegoUbusParser.PollCalls.Count; i++)
        {
            if (byMethod[InseegoUbusParser.PollCalls[i].Method] is { } json)
                results[i + 1] = JsonDocument.Parse(json).RootElement.Clone();
        }
        return results;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ----- Parse -----

    [Fact]
    public void Parse_FullResults_PopulatesIdentityCarrierAndSignal()
    {
        var stats = InseegoUbusParser.Parse(Results(), Context)!;

        stats.Should().NotBeNull();
        stats.ModemHost.Should().Be("192.0.2.1");
        stats.ModemName.Should().Be("Test Gateway");
        stats.ModemModel.Should().Be("FX4100");
        stats.SoftwareVersion.Should().Be("1.2.3.4");
        stats.Carrier.Should().Be("TestCarrier");
        stats.CarrierMcc.Should().Be("001");
        stats.CarrierMnc.Should().Be("010");
        stats.IsRoaming.Should().BeFalse();
        stats.RegistrationState.Should().Be("registered");

        stats.Lte.Should().NotBeNull();
        stats.Lte!.Rsrp.Should().Be(-95);
        stats.Lte.Rsrq.Should().Be(-12);
        stats.Lte.Snr.Should().Be(18);
        stats.Nr5g.Should().BeNull();
        stats.NetworkMode.Should().Be(CellularNetworkMode.Lte);
    }

    [Fact]
    public void Parse_UnreportedZeroMetrics_BecomeNull()
    {
        // rssi and sinr arrive as 0 on this firmware: it does not measure them.
        var stats = InseegoUbusParser.Parse(Results(), Context)!;

        stats.Lte!.Rssi.Should().BeNull();
    }

    [Fact]
    public void Parse_ServingCell_CarriesPciCellIdAndPlmn()
    {
        var stats = InseegoUbusParser.Parse(Results(), Context)!;

        stats.ServingCell.Should().NotBeNull();
        stats.ServingCell!.IsServing.Should().BeTrue();
        stats.ServingCell.PhysicalCellId.Should().Be(123);
        stats.ServingCell.GlobalCellId.Should().Be("1234567");
        stats.ServingCell.Plmn.Should().Be("001010");
        stats.ServingCell.Signal.Should().BeSameAs(stats.Lte);
        // 1234567 = eNB 4822, sector 135.
        stats.ServingCell.EnbId.Should().Be(1234567 >> 8);
        stats.ServingCell.SectorId.Should().Be(1234567 & 0xFF);
    }

    [Fact]
    public void Parse_NumericCellId_IsAccepted()
    {
        var svc = ServiceStats.Replace("\"cell_id\":\"1234567\"", "\"cell_id\":1234567");

        var stats = InseegoUbusParser.Parse(Results(serviceStats: svc), Context)!;

        stats.ServingCell!.GlobalCellId.Should().Be("1234567");
    }

    [Fact]
    public void Parse_CellIdWiderThan28Bits_IsReportedAsNr()
    {
        // A 36-bit NR cell identity cannot be an LTE ECI, so the signal belongs to NR.
        var svc = ServiceStats.Replace("\"cell_id\":\"1234567\"", "\"cell_id\":\"68719476000\"");

        var stats = InseegoUbusParser.Parse(Results(serviceStats: svc), Context)!;

        stats.Nr5g.Should().NotBeNull();
        stats.Nr5g!.Rsrp.Should().Be(-95);
        stats.Lte.Should().BeNull();
        stats.NetworkMode.Should().Be(CellularNetworkMode.Nr5gSa);
        stats.ServingCell!.GlobalCellId.Should().Be("68719476000");
        // Not a 28-bit ECI, so no eNB/sector split.
        stats.ServingCell.EnbId.Should().BeNull();
    }

    [Fact]
    public void Parse_LargestLteCellId_StaysLte()
    {
        var svc = ServiceStats.Replace("\"cell_id\":\"1234567\"", "\"cell_id\":\"268435455\"");

        var stats = InseegoUbusParser.Parse(Results(serviceStats: svc), Context)!;

        stats.Lte.Should().NotBeNull();
        stats.Nr5g.Should().BeNull();
    }

    [Fact]
    public void Parse_NoRsrp_HasNoSignalButKeepsServingCell()
    {
        var svc = ServiceStats.Replace("\"rsrp\":-95", "\"rsrp\":0");

        var stats = InseegoUbusParser.Parse(Results(serviceStats: svc), Context)!;

        stats.Lte.Should().BeNull();
        stats.Nr5g.Should().BeNull();
        stats.NetworkMode.Should().Be(CellularNetworkMode.Unknown);
        stats.ServingCell!.PhysicalCellId.Should().Be(123);
        stats.ServingCell.Signal.Should().BeNull();
    }

    [Fact]
    public void Parse_ZeroSnrWithRsrp_IsARealReading()
    {
        var svc = ServiceStats.Replace("\"snr\":18", "\"snr\":0");

        var stats = InseegoUbusParser.Parse(Results(serviceStats: svc), Context)!;

        stats.Lte!.Snr.Should().Be(0);
    }

    [Fact]
    public void Parse_MissingSnr_FallsBackToSinr()
    {
        var svc = ServiceStats.Replace("\"snr\":18,", "").Replace("\"sinr\":0", "\"sinr\":9");

        var stats = InseegoUbusParser.Parse(Results(serviceStats: svc), Context)!;

        stats.Lte!.Snr.Should().Be(9);
    }

    [Fact]
    public void Parse_Roaming_IsReported()
    {
        var svc = ServiceStats.Replace("\"roam\":0", "\"roam\":1");

        var stats = InseegoUbusParser.Parse(Results(serviceStats: svc), Context)!;

        stats.IsRoaming.Should().BeTrue();
    }

    [Theory]
    [InlineData("00101", "001", "01")]
    [InlineData("001010", "001", "010")]
    public void Parse_OperId_SplitsMccAndMnc(string operId, string mcc, string mnc)
    {
        var svc = ServiceStats.Replace("\"oper_id\":\"001010\"", $"\"oper_id\":\"{operId}\"");

        var stats = InseegoUbusParser.Parse(Results(serviceStats: svc), Context)!;

        stats.CarrierMcc.Should().Be(mcc);
        stats.CarrierMnc.Should().Be(mnc);
        stats.ServingCell!.Plmn.Should().Be(operId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0010")]
    [InlineData("00101A")]
    public void Parse_MalformedOperId_LeavesMccMncEmpty(string operId)
    {
        var svc = ServiceStats.Replace("\"oper_id\":\"001010\"", $"\"oper_id\":\"{operId}\"");

        var stats = InseegoUbusParser.Parse(Results(serviceStats: svc), Context)!;

        stats.CarrierMcc.Should().BeEmpty();
        stats.CarrierMnc.Should().BeEmpty();
        stats.ServingCell!.Plmn.Should().BeNull();
    }

    [Fact]
    public void Parse_ServiceStatusOtherThanInService_IsShownRaw()
    {
        var stats = InseegoUbusParser.Parse(Results(serviceStatus: """{"status":1}"""), Context)!;

        stats.RegistrationState.Should().Be("state-1");
    }

    [Fact]
    public void Parse_MissingServiceStatus_LeavesRegistrationEmpty()
    {
        var stats = InseegoUbusParser.Parse(Results(serviceStatus: null), Context)!;

        stats.RegistrationState.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WithoutDeviceModelName_FallsBackToHardwareModel()
    {
        var stats = InseegoUbusParser.Parse(Results(modelName: null), Context)!;

        stats.ModemModel.Should().Be("FX4120");
    }

    [Fact]
    public void Parse_WithNoModelAnywhere_FallsBackToConfiguredType()
    {
        var stats = InseegoUbusParser.Parse(Results(modelName: """{"model":""}""", hardwareInfo: null), Context)!;

        stats.ModemModel.Should().Be("Inseego FX");
    }

    [Fact]
    public void Parse_WithoutSystemVersion_LeavesSoftwareVersionNull()
    {
        var stats = InseegoUbusParser.Parse(Results(systemVersion: null), Context)!;

        stats.SoftwareVersion.Should().BeNull();
    }

    [Fact]
    public void Parse_WithoutServiceStats_ReturnsNull()
    {
        InseegoUbusParser.Parse(Results(serviceStats: null), Context).Should().BeNull();
    }

    [Fact]
    public void Parse_ConfiguredHost_WinsOverTunnelHost()
    {
        var tunneled = new ModemPollContext
        {
            Id = 1,
            Name = "Test Gateway",
            Host = "127.0.0.1",
            ConfiguredHost = "192.0.2.1",
        };

        var stats = InseegoUbusParser.Parse(Results(), tunneled)!;

        stats.ModemHost.Should().Be("192.0.2.1");
    }

    // ----- ParseBatchResponse -----

    [Fact]
    public void ParseBatchResponse_MapsResultsByIdRegardlessOfOrder()
    {
        var response = Json("""
            [
              {"jsonrpc":"2.0","id":3,"result":[0,{"model":"FX4100"}]},
              {"jsonrpc":"2.0","id":1,"result":[0,{"rsrp":-95}]}
            ]
            """);

        var results = InseegoUbusParser.ParseBatchResponse(response, out var accessDenied);

        accessDenied.Should().BeFalse();
        results.Should().HaveCount(2);
        results[1].GetProperty("rsrp").GetInt32().Should().Be(-95);
        results[3].GetProperty("model").GetString().Should().Be("FX4100");
    }

    [Fact]
    public void ParseBatchResponse_UbusPermissionDenied_FlagsAccessDenied()
    {
        var response = Json("""
            [
              {"jsonrpc":"2.0","id":1,"result":[6]},
              {"jsonrpc":"2.0","id":2,"result":[0,{"status":2}]}
            ]
            """);

        var results = InseegoUbusParser.ParseBatchResponse(response, out var accessDenied);

        accessDenied.Should().BeTrue();
        results.Keys.Should().Equal(2);
    }

    [Fact]
    public void ParseBatchResponse_RpcAccessDeniedError_FlagsAccessDenied()
    {
        var response = Json("""
            [{"jsonrpc":"2.0","id":1,"error":{"code":-32002,"message":"Access denied"}}]
            """);

        var results = InseegoUbusParser.ParseBatchResponse(response, out var accessDenied);

        accessDenied.Should().BeTrue();
        results.Should().BeEmpty();
    }

    [Fact]
    public void ParseBatchResponse_OtherFailures_AreDroppedWithoutAccessDenied()
    {
        var response = Json("""
            [
              {"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Method not found"}},
              {"jsonrpc":"2.0","id":2,"result":[4]},
              {"jsonrpc":"2.0","id":3,"result":[0]},
              {"jsonrpc":"2.0","id":4,"result":"bogus"},
              {"jsonrpc":"2.0","result":[0,{"status":2}]},
              {"jsonrpc":"2.0","id":5,"result":[0,{"status":2}]}
            ]
            """);

        var results = InseegoUbusParser.ParseBatchResponse(response, out var accessDenied);

        accessDenied.Should().BeFalse();
        results.Keys.Should().Equal(5);
    }

    [Fact]
    public void ParseBatchResponse_NonArray_ReturnsEmpty()
    {
        var results = InseegoUbusParser.ParseBatchResponse(Json("""{"jsonrpc":"2.0","id":1}"""), out var accessDenied);

        accessDenied.Should().BeFalse();
        results.Should().BeEmpty();
    }

    // ----- ParseLoginToken -----

    [Fact]
    public void ParseLoginToken_Authenticated_ReturnsToken()
    {
        var response = Json("""
            {"jsonrpc":"2.0","id":1,"result":[0,{"authenticated":1,"retry_count":5,"is_default":0,"timeout":600,"session_token":"0123456789abcdef0123456789abcdef"}]}
            """);

        InseegoUbusParser.ParseLoginToken(response).Should().Be("0123456789abcdef0123456789abcdef");
    }

    [Fact]
    public void ParseLoginToken_AcceptsBatchWrappedResponse()
    {
        var response = Json("""
            [{"jsonrpc":"2.0","id":1,"result":[0,{"authenticated":1,"session_token":"abc"}]}]
            """);

        InseegoUbusParser.ParseLoginToken(response).Should().Be("abc");
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":[0,{"authenticated":0,"retry_count":4}]}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":[0,{"authenticated":1}]}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":[0,{"authenticated":1,"session_token":""}]}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":[6]}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":{"code":-32002,"message":"Access denied"}}""")]
    public void ParseLoginToken_RejectedOrMalformed_ReturnsNull(string json)
    {
        InseegoUbusParser.ParseLoginToken(Json(json)).Should().BeNull();
    }

    // ----- Request builders -----

    [Fact]
    public void BuildBatchRequest_NumbersCallsFromOneWithSessionAndEmptyArgs()
    {
        var body = InseegoUbusParser.BuildBatchRequest("tok", InseegoUbusParser.PollCalls);

        var arr = Json(body);
        arr.GetArrayLength().Should().Be(InseegoUbusParser.PollCalls.Count);
        for (var i = 0; i < arr.GetArrayLength(); i++)
        {
            var call = arr[i];
            call.GetProperty("jsonrpc").GetString().Should().Be("2.0");
            call.GetProperty("id").GetInt32().Should().Be(i + 1);
            call.GetProperty("method").GetString().Should().Be("call");
            var p = call.GetProperty("params");
            p[0].GetString().Should().Be("tok");
            p[1].GetString().Should().Be(InseegoUbusParser.PollCalls[i].Object);
            p[2].GetString().Should().Be(InseegoUbusParser.PollCalls[i].Method);
            p[3].ValueKind.Should().Be(JsonValueKind.Object);
            p[3].EnumerateObject().Should().BeEmpty();
        }
    }

    [Fact]
    public void BuildLoginRequest_UsesAnonymousSessionAndEscapesPassword()
    {
        const string password = "p\"a@%s\\w{rd}";

        var call = Json(InseegoUbusParser.BuildLoginRequest(password));

        var p = call.GetProperty("params");
        p[0].GetString().Should().Be(InseegoUbusParser.AnonymousSession);
        p[1].GetString().Should().Be("webui.login");
        p[2].GetString().Should().Be("authenticate");
        p[3].GetProperty("password").GetString().Should().Be(password);
    }

    [Fact]
    public void PollCalls_ExcludeTheWanTelemetryCallThatReturnsInvalidJson()
    {
        InseegoUbusParser.PollCalls.Should().NotContain(c => c.Method.StartsWith("get_telemetry_wan_info"));
    }
}

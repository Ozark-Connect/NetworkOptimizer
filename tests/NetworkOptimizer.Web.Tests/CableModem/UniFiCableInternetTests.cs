using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Endpoints;
using NetworkOptimizer.Web.Services.CableModemProviders;
using NetworkOptimizer.Web.Services.CableModemProviders.Uci;
using NetworkOptimizer.Web.Services.Monitoring;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Xunit;

namespace NetworkOptimizer.Web.Tests.CableModem;

/// <summary>
/// UniFi Cable Internet: decoding inform frames, mapping the payload into the shared cable modem
/// model, and reading the DOCSIS event log. Payload field names follow the open-source
/// uci-inform-exporter; the frames here are built with the same framing it decrypts.
/// </summary>
public class UniFiCableInternetTests
{
    private static readonly byte[] Key = Convert.FromHexString("00112233445566778899aabbccddeeff");
    private const string Mac = "aa:bb:cc:00:11:22";

    private const string SamplePayload = """
    {
      "mac": "aa:bb:cc:00:11:22", "model": "UCI", "version": "2.0.2", "serial": "ABC123", "uptime": "86400",
      "ds_table": [
        { "ch_id": "7", "freq": "567", "modulation": "QAM256", "state": "Locked", "pwr": "3.9", "snr": "-40.4",
          "speed": 38, "correctable": "12", "uncorrectable": 3 },
        { "ch_id": 8, "freq": 573, "modulation": "QAM256", "state": "Locked", "pwr": 4.1, "snr": -39.8,
          "correctable": 0, "uncorrectable": 0 }
      ],
      "ofdm_table": [
        { "ch_id": 33, "freq": "850 ", "state": "Locked", "pwr": "2.5", "snr": "-42.1", "speed": 1500,
          "correctable": 4000000000, "uncorrectable": 900000 }
      ],
      "us_table": [
        { "ch_id": 1, "freq": "36.2", "modulation": "QAM64", "state": "Locked", "pwr": "44.5", "speed": 30 }
      ],
      "ofdma_table": [
        { "ch_id": 5, "freq": "60", "state": "Locked", "pwr": "41.0" }
      ],
      "ev_log_docsis": "Time Not Established [Critical] No Ranging Response received - T3 time-out;CM-MAC=aa:bb:cc:00:11:22;\nSat Aug 23 14:03:11 2026 [Critical] Received Response to Broadcast Maintenance Request, But no Unicast Maintenance opportunities received - T4 time out\n2026-08-23 14:05:00 [Notice] US profile assignment change\n",
      "ci_state_table": { "ci_state": "Operational", "ci_reinit_reason": "POWER_ON", "ci_mode": "D3.1" }
    }
    """;

    // ---- Decoder ----

    [Fact]
    public void Decode_GcmZlibFrame_RoundTrips()
    {
        var json = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var frame = BuildGcmFrame(json, Key);

        UciInformDecoder.TryDecode(frame, Key, out var decoded).Should().Be(UciDecodeFailure.None);
        decoded.Should().Equal(json);
    }

    [Fact]
    public void Decode_WrongKey_IsAKeyMismatch()
    {
        var frame = BuildGcmFrame("{}"u8.ToArray(), Key);
        var wrong = Convert.FromHexString("ffeeddccbbaa99887766554433221100");

        UciInformDecoder.TryDecode(frame, wrong, out _).Should().Be(UciDecodeFailure.KeyMismatch);
    }

    [Fact]
    public void Decode_TamperedHeader_IsAKeyMismatch()
    {
        // The header is GCM's additional data, so changing it must fail authentication.
        var frame = BuildGcmFrame("{}"u8.ToArray(), Key);
        frame[4] ^= 0x01;

        UciInformDecoder.TryDecode(frame, Key, out _).Should().Be(UciDecodeFailure.KeyMismatch);
    }

    [Fact]
    public void Decode_CbcZlibFrame_RoundTrips()
    {
        var json = Encoding.UTF8.GetBytes("""{"ds_table":[]}""");
        var frame = BuildCbcFrame(json, Key);

        UciInformDecoder.TryDecode(frame, Key, out var decoded).Should().Be(UciDecodeFailure.None);
        decoded.Should().Equal(json);
    }

    [Fact]
    public void Decode_Snappy_IsUnsupported()
    {
        var frame = BuildGcmFrame("{}"u8.ToArray(), Key, extraFlags: UciInformDecoder.FlagSnappy);
        UciInformDecoder.TryDecode(frame, Key, out _).Should().Be(UciDecodeFailure.Unsupported);
    }

    [Fact]
    public void Decode_NotTnbu_IsMalformed()
    {
        UciInformDecoder.TryDecode(new byte[64], Key, out _).Should().Be(UciDecodeFailure.Malformed);
        UciInformDecoder.TryDecode("TNBU"u8.ToArray(), Key, out _).Should().Be(UciDecodeFailure.Malformed);
    }

    [Fact]
    public void ParseHeader_ReadsTheSenderMac()
    {
        var header = UciInformDecoder.ParseHeader(BuildGcmFrame("{}"u8.ToArray(), Key));
        header!.Mac.Should().Be(Mac);
        header.Flags.Should().Be((ushort)(UciInformDecoder.FlagEncrypted | UciInformDecoder.FlagZlib | UciInformDecoder.FlagGcm));
    }

    [Theory]
    [InlineData("00112233445566778899aabbccddeeff", true)]
    [InlineData(" 00112233445566778899AABBCCDDEEFF ", true)]
    [InlineData("0011", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("zz112233445566778899aabbccddeeff", false)]
    public void ParseKey_AcceptsOnly128BitHex(string? hex, bool valid)
    {
        (UciInformDecoder.ParseKey(hex) != null).Should().Be(valid);
    }

    // ---- Payload and mapping ----

    [Fact]
    public void Map_ReadsEveryTableIntoTheSharedModel()
    {
        var payload = UciInformPayload.Parse(Encoding.UTF8.GetBytes(SamplePayload))!;
        var stats = UciStatsMapper.Map(payload, "Cable", Mac, new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc));

        stats.DeviceModel.Should().Be("UCI");
        stats.FirmwareVersion.Should().Be("2.0.2");
        stats.UptimeSeconds.Should().Be(86400);

        stats.DownstreamChannels.Should().HaveCount(3);
        var first = stats.DownstreamChannels[0];
        first.ChannelId.Should().Be(7);
        first.LockStatus.Should().Be("Locked");
        first.Modulation.Should().Be("QAM256");
        first.Frequency.Should().Be(567_000_000);
        first.Power.Should().Be(3.9);
        first.Snr.Should().Be(40.4, "the UCI reports SC-QAM SNR as a negative number");
        first.Correctables.Should().Be(12);
        first.Uncorrectables.Should().Be(3);
        first.ReportedSpeedMbps.Should().Be(38);

        var ofdm = stats.DownstreamChannels[2];
        ofdm.Modulation.Should().Be("OFDM");
        ofdm.Frequency.Should().Be(850_000_000);
        ofdm.Snr.Should().Be(42.1);
        ofdm.Correctables.Should().Be(0, "OFDM codewords run to billions and stay out of the aggregates");

        stats.TotalCorrectables.Should().Be(12);
        stats.TotalUncorrectables.Should().Be(3);
        stats.LockedDsChannels.Should().Be(3);

        stats.UpstreamChannels.Should().HaveCount(2);
        stats.UpstreamChannels[0].ChannelType.Should().Be("SC-QAM");
        stats.UpstreamChannels[0].Modulation.Should().Be("QAM64");
        stats.UpstreamChannels[0].Frequency.Should().Be(36_200_000);
        stats.UpstreamChannels[1].ChannelType.Should().Be("OFDMA");
        stats.UpstreamPowerAvgDbmv.Should().BeApproximately(42.75, 0.001);

        stats.DocsisState.Should().Be(new CmDocsisState("D3.1", "Operational", "POWER_ON"));
    }

    [Fact]
    public void Parse_NotJsonObject_IsNull()
    {
        UciInformPayload.Parse("[1,2,3]"u8.ToArray()).Should().BeNull();
        UciInformPayload.Parse("garbage"u8.ToArray()).Should().BeNull();
    }

    [Theory]
    [InlineData("567", 567_000_000L)]
    [InlineData("567.25", 567_250_000L)]
    [InlineData("567 MHz", 567_000_000L)]
    [InlineData("0.567 GHz", 567_000_000L)]
    [InlineData("567000000", 567_000_000L)]
    [InlineData("", 0L)]
    [InlineData(null, 0L)]
    [InlineData("n/a", 0L)]
    public void ParseFrequencyHz_HandlesMhzHzAndUnits(string? text, long expected)
    {
        UciStatsMapper.ParseFrequencyHz(text).Should().Be(expected);
    }

    // ---- Event log ----

    [Fact]
    public void EventLog_SplitsTimeLevelAndText_AndClassifies()
    {
        var payload = UciInformPayload.Parse(Encoding.UTF8.GetBytes(SamplePayload))!;
        var events = UciStatsMapper.ParseEventLog(payload.EventLog);

        events.Should().HaveCount(3);

        events[0].Time.Should().BeNull("the modem had not synced its clock");
        events[0].Level.Should().Be("Critical");
        events[0].Kind.Should().Be(CmEventKinds.T3Timeout);
        events[0].Text.Should().StartWith("No Ranging Response received");

        events[1].Time.Should().Be(new DateTime(2026, 8, 23, 14, 3, 11, DateTimeKind.Utc));
        events[1].Kind.Should().Be(CmEventKinds.T4Timeout);

        events[2].Level.Should().Be("Notice");
        events[2].Kind.Should().BeNull();
        events[2].Time.Should().Be(new DateTime(2026, 8, 23, 14, 5, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("Ranging Request Retries exhausted;CM-MAC=x;", CmEventKinds.RangingFailure)]
    [InlineData("Unicast Ranging Received Abort Response - Re-initializing MAC", CmEventKinds.RangingFailure)]
    [InlineData("No Ranging Response received - T3 time-out", CmEventKinds.T3Timeout)]
    [InlineData("SYNC Timing Synchronization failure - Loss of Sync", null)]
    public void Classify_GoesByStandardText(string text, string? expected)
    {
        UciStatsMapper.Classify(text).Should().Be(expected);
    }

    [Fact]
    public void EventLog_PreClockSyncYear_HasNoTime()
    {
        var events = UciStatsMapper.ParseEventLog("Thu Jan 1 00:00:31 1970 [Critical] No Ranging Response received - T3 time-out");
        events.Should().ContainSingle().Which.Time.Should().BeNull();
    }

    // ---- Redaction ----

    [Fact]
    public void Redact_MasksIdentifiers_KeepsTimesAndValues()
    {
        var redacted = UciInformService.Redact(
            """{"mac":"aa:bb:cc:00:11:22","serial":"ABC123","ip":"192.0.2.5","ll":"fe80::d221:f9ff:fe05:2195","ev":"Sat Aug 23 14:03:11 2026","freq":"567.000000"}""");

        redacted.Should().NotContain("aa:bb:cc:00:11:22");
        redacted.Should().NotContain("ABC123");
        redacted.Should().NotContain("192.0.2.5");
        redacted.Should().NotContain("fe80::d221");
        redacted.Should().Contain("14:03:11");
        redacted.Should().Contain("567.000000");
    }

    // ---- Config issues ----

    [Fact]
    public void Issue_RequiredProviderWithoutPasswordAndFailing_IsFlagged()
    {
        var config = new CmConfiguration { Provider = "motorola-hnap", Enabled = true, LastError = "login failed" };
        CmConfigIssues.Describe(config, Provider(CmCredentialRequirement.Required)).Should().Be(CmConfigIssues.MissingPassword);
    }

    [Fact]
    public void Issue_RequiredProviderWithoutPasswordButWorking_IsNotFlagged()
    {
        var config = new CmConfiguration { Provider = "motorola-hnap", Enabled = true, LastError = null };
        CmConfigIssues.Describe(config, Provider(CmCredentialRequirement.Required)).Should().BeNull();
    }

    [Fact]
    public void Issue_OptionalProviderWithoutPassword_IsNotFlagged()
    {
        var config = new CmConfiguration { Provider = "netgear", Enabled = true, LastError = "timeout" };
        CmConfigIssues.Describe(config, Provider(CmCredentialRequirement.Optional)).Should().BeNull();
    }

    [Fact]
    public void Issue_DisabledConfig_IsNotFlagged()
    {
        var config = new CmConfiguration { Provider = "motorola-hnap", Enabled = false, LastError = "login failed" };
        CmConfigIssues.Describe(config, Provider(CmCredentialRequirement.Required)).Should().BeNull();
    }

    [Fact]
    public void Issue_FailingUci_ShowsItsReason()
    {
        var config = new CmConfiguration { Provider = UciInformService.ProviderKey, Enabled = true, LastError = "needs the agent" };
        CmConfigIssues.Describe(config, Provider(CmCredentialRequirement.None)).Should().Be("needs the agent");
    }

    // ---- Credential declarations: must match what each provider does with the fields ----

    [Theory]
    [InlineData(typeof(ArrisSurfboardHnapProvider), CmCredentialRequirement.Required, true)]
    [InlineData(typeof(MotorolaHnapProvider), CmCredentialRequirement.Required, false)]
    [InlineData(typeof(ArrisSurfboardHttpProvider), CmCredentialRequirement.Optional, false)]
    [InlineData(typeof(SagemcomF3896Provider), CmCredentialRequirement.None, false)]
    [InlineData(typeof(TechnicolorCgaProvider), CmCredentialRequirement.Required, true)]
    [InlineData(typeof(NetgearCmProvider), CmCredentialRequirement.Optional, false)]
    [InlineData(typeof(VodafoneStationProvider), CmCredentialRequirement.Required, true)]
    [InlineData(typeof(XfinityGatewayProvider), CmCredentialRequirement.Required, false)]
    [InlineData(typeof(UniFiCableInternetProvider), CmCredentialRequirement.None, false)]
    public void Provider_DeclaresItsCredentialNeeds(Type providerType, CmCredentialRequirement requirement, bool blankMeansAdmin)
    {
        var provider = (ICableModemProvider)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(providerType);
        provider.Credentials.Should().Be(requirement);
        provider.BlankUsernameMeansAdmin.Should().Be(blankMeansAdmin);
    }

    // ---- Alerts ----

    [Fact]
    public async Task Reinit_FirstObservationOnlyBaselines_ThenAReasonChangeAlerts()
    {
        var published = new List<AlertEvent>();
        var bus = new Mock<IAlertEventBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<AlertEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AlertEvent, CancellationToken>((e, _) => published.Add(e))
            .Returns(ValueTask.CompletedTask);
        var evaluator = new CableModemAlertEvaluator(bus.Object, NullLogger<CableModemAlertEvaluator>.Instance);

        await evaluator.EvaluateReinitAsync(1, "Cable", "POWER_ON", 1000);
        await evaluator.EvaluateReinitAsync(1, "Cable", "POWER_ON", 1060);
        published.Should().BeEmpty();

        await evaluator.EvaluateReinitAsync(1, "Cable", "T4_TIMEOUT", 1120);
        published.Should().ContainSingle().Which.EventType.Should().Be("cable_modem.reinit");
    }

    [Fact]
    public async Task Reinit_UptimeGoingBackwards_Alerts()
    {
        var published = new List<AlertEvent>();
        var bus = new Mock<IAlertEventBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<AlertEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AlertEvent, CancellationToken>((e, _) => published.Add(e))
            .Returns(ValueTask.CompletedTask);
        var evaluator = new CableModemAlertEvaluator(bus.Object, NullLogger<CableModemAlertEvaluator>.Instance);

        await evaluator.EvaluateReinitAsync(1, "Cable", "POWER_ON", 5000);
        await evaluator.EvaluateReinitAsync(1, "Cable", "POWER_ON", 30);

        published.Should().ContainSingle().Which.Message.Should().Contain("POWER_ON");
    }

    [Fact]
    public async Task DocsisEvent_PublishesItsKindAsTheEventType()
    {
        var published = new List<AlertEvent>();
        var bus = new Mock<IAlertEventBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<AlertEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AlertEvent, CancellationToken>((e, _) => published.Add(e))
            .Returns(ValueTask.CompletedTask);
        var evaluator = new CableModemAlertEvaluator(bus.Object, NullLogger<CableModemAlertEvaluator>.Instance);

        await evaluator.PublishDocsisEventAsync(1, "Cable", new CmEvent { Kind = CmEventKinds.T3Timeout, Text = "T3", Level = "Critical" });
        await evaluator.PublishDocsisEventAsync(1, "Cable", new CmEvent { Kind = null, Text = "Notice line" });

        published.Should().ContainSingle().Which.EventType.Should().Be("cable_modem.t3_timeout");
    }

    [Theory]
    [InlineData("Critical", "critical")]
    [InlineData("emergency", "critical")]
    [InlineData("Error", "warning")]
    [InlineData("Warning", "warning")]
    [InlineData("Notice", "info")]
    [InlineData(null, "info")]
    public void MarkSeverity_MapsDocsisLevels(string? level, string expected)
    {
        CmChartEndpoints.MarkSeverity(level).Should().Be(expected);
    }

    // ---- helpers ----

    private static ICableModemProvider Provider(CmCredentialRequirement requirement)
    {
        var provider = new Mock<ICableModemProvider>();
        provider.SetupGet(p => p.Credentials).Returns(requirement);
        return provider.Object;
    }

    private static byte[] Header(ushort flags, byte[] iv, int dataLength)
    {
        var header = new byte[UciInformDecoder.HeaderLength];
        "TNBU"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 0);
        Convert.FromHexString(Mac.Replace(":", "")).CopyTo(header, 8);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), flags);
        iv.CopyTo(header, 16);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(32), 1);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(36), dataLength);
        return header;
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
            zlib.Write(data);
        return output.ToArray();
    }

    private static byte[] BuildGcmFrame(byte[] json, byte[] key, ushort extraFlags = 0)
    {
        var flags = (ushort)(UciInformDecoder.FlagEncrypted | UciInformDecoder.FlagZlib | UciInformDecoder.FlagGcm | extraFlags);
        var iv = RandomNumberGenerator.GetBytes(16);
        var plain = Zlib(json);
        var header = Header(flags, iv, plain.Length + 16);

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(key), 128, iv, header));
        var output = new byte[cipher.GetOutputSize(plain.Length)];
        var written = cipher.ProcessBytes(plain, 0, plain.Length, output, 0);
        cipher.DoFinal(output, written);
        return header.Concat(output).ToArray();
    }

    private static byte[] BuildCbcFrame(byte[] json, byte[] key)
    {
        var flags = (ushort)(UciInformDecoder.FlagEncrypted | UciInformDecoder.FlagZlib);
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key;
        var encrypted = aes.EncryptCbc(Zlib(json), iv, PaddingMode.PKCS7);
        return Header(flags, iv, encrypted.Length).Concat(encrypted).ToArray();
    }
}

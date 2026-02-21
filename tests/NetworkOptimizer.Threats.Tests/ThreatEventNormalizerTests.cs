using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class ThreatEventNormalizerTests
{
    private readonly ThreatEventNormalizer _normalizer;

    public ThreatEventNormalizerTests()
    {
        var logger = new Mock<ILogger<ThreatEventNormalizer>>();
        _normalizer = new ThreatEventNormalizer(logger.Object);
    }

    // --- NormalizeV1Events ---

    [Fact]
    public void NormalizeV1Events_ValidJson_ReturnsNormalizedEvents()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                _id = "evt001",
                timestamp = 1700000000000L,
                src_ip = "192.0.2.10",
                src_port = 54321,
                dest_ip = "198.51.100.1",
                dest_port = 443,
                proto = "TCP",
                catname = "Misc Attack",
                alert = new
                {
                    signature_id = 2024001,
                    signature = "ET SCAN Suspicious inbound",
                    category = "Attempted Information Leak",
                    severity = 2,
                    action = "drop"
                }
            }
        });

        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV1Events(element);

        Assert.Single(results);
        var evt = results[0];
        Assert.Equal("evt001", evt.InnerAlertId);
        Assert.Equal("192.0.2.10", evt.SourceIp);
        Assert.Equal(54321, evt.SourcePort);
        Assert.Equal("198.51.100.1", evt.DestIp);
        Assert.Equal(443, evt.DestPort);
        Assert.Equal("TCP", evt.Protocol);
        Assert.Equal(2024001, evt.SignatureId);
        Assert.Equal("ET SCAN Suspicious inbound", evt.SignatureName);
        Assert.Equal("Attempted Information Leak", evt.Category);
        Assert.Equal(ThreatAction.Blocked, evt.Action);
    }

    [Fact]
    public void NormalizeV1Events_MultipleEvents_ReturnsAll()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { _id = "evt001", timestamp = 1700000000000L, src_ip = "192.0.2.10", src_port = 1234, dest_ip = "198.51.100.1", dest_port = 80, proto = "TCP", catname = "Scan", alert = new { signature_id = 1L, signature = "Sig1", category = "SCAN", severity = 3, action = "alert" } },
            new { _id = "evt002", timestamp = 1700000001000L, src_ip = "192.0.2.11", src_port = 5678, dest_ip = "198.51.100.2", dest_port = 22, proto = "TCP", catname = "Attack", alert = new { signature_id = 2L, signature = "Sig2", category = "EXPLOIT", severity = 1, action = "drop" } }
        });

        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV1Events(element);

        Assert.Equal(2, results.Count);
        Assert.Equal("evt001", results[0].InnerAlertId);
        Assert.Equal("evt002", results[1].InnerAlertId);
    }

    [Fact]
    public void NormalizeV1Events_MissingId_SkipsEvent()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { _id = "", timestamp = 1700000000000L, src_ip = "192.0.2.10", src_port = 0, dest_ip = "198.51.100.1", dest_port = 80, proto = "TCP", catname = "", alert = new { signature_id = 1L, signature = "Sig", category = "Cat", severity = 3, action = "alert" } }
        });

        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV1Events(element);

        Assert.Empty(results);
    }

    [Fact]
    public void NormalizeV1Events_NonArrayInput_ReturnsEmpty()
    {
        var json = "{}";
        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV1Events(element);

        Assert.Empty(results);
    }

    [Fact]
    public void NormalizeV1Events_EmptyArray_ReturnsEmpty()
    {
        var json = "[]";
        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV1Events(element);

        Assert.Empty(results);
    }

    [Fact]
    public void NormalizeV1Events_UsesCategory_FromAlert()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                _id = "evt001",
                timestamp = 1700000000000L,
                src_ip = "192.0.2.10",
                src_port = 0,
                dest_ip = "198.51.100.1",
                dest_port = 80,
                proto = "TCP",
                catname = "Fallback Category",
                alert = new { signature_id = 1L, signature = "Sig", category = "Alert Category", severity = 3, action = "alert" }
            }
        });

        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV1Events(element);

        Assert.Equal("Alert Category", results[0].Category);
    }

    // --- NormalizeV2Events ---

    [Fact]
    public void NormalizeV2Events_ValidJson_ReturnsNormalizedEvents()
    {
        var json = JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new
                {
                    _id = "v2evt001",
                    time = 1700000000000L,
                    src_ip = "203.0.113.50",
                    src_port = 9999,
                    dst_ip = "198.51.100.5",
                    dst_port = 22,
                    proto = "TCP",
                    inner_alert_signature_id = 3000001L,
                    inner_alert_signature = "ET EXPLOIT SSH brute force attempt",
                    inner_alert_category = "Attempted Administrator Privilege Gain",
                    inner_alert_severity = 1,
                    inner_alert_action = "drop"
                }
            },
            totalCount = 1,
            isLastPage = true
        });

        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV2Events(element);

        Assert.Single(results);
        var evt = results[0];
        Assert.Equal("v2evt001", evt.InnerAlertId);
        Assert.Equal("203.0.113.50", evt.SourceIp);
        Assert.Equal(9999, evt.SourcePort);
        Assert.Equal("198.51.100.5", evt.DestIp);
        Assert.Equal(22, evt.DestPort);
        Assert.Equal("TCP", evt.Protocol);
        Assert.Equal(3000001, evt.SignatureId);
        Assert.Equal("ET EXPLOIT SSH brute force attempt", evt.SignatureName);
        Assert.Equal("Attempted Administrator Privilege Gain", evt.Category);
        Assert.Equal(ThreatAction.Blocked, evt.Action);
    }

    [Fact]
    public void NormalizeV2Events_NoDataProperty_ReturnsEmpty()
    {
        var json = """{"totalCount": 0}""";
        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV2Events(element);

        Assert.Empty(results);
    }

    [Fact]
    public void NormalizeV2Events_EmptyData_ReturnsEmpty()
    {
        var json = """{"data": [], "totalCount": 0}""";
        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV2Events(element);

        Assert.Empty(results);
    }

    [Fact]
    public void NormalizeV2Events_FallsBackToDestIp_WhenDstIpMissing()
    {
        var json = JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new
                {
                    _id = "v2evt002",
                    time = 1700000000000L,
                    src_ip = "203.0.113.50",
                    src_port = 0,
                    dest_ip = "198.51.100.10",
                    dest_port = 80,
                    proto = "TCP",
                    inner_alert_signature_id = 1L,
                    inner_alert_signature = "Sig",
                    inner_alert_category = "Cat",
                    inner_alert_severity = 3,
                    inner_alert_action = "alert"
                }
            }
        });

        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV2Events(element);

        // When dst_ip is present, it should use that; when missing, falls back to dest_ip
        Assert.Equal("198.51.100.10", results[0].DestIp);
    }

    // --- NormalizeSeverity ---

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 4)]
    [InlineData(3, 2)]
    [InlineData(4, 1)]
    [InlineData(99, 3)]
    [InlineData(0, 3)]
    [InlineData(-1, 3)]
    public void NormalizeSeverity_MapsCorrectly(int suricata, int expected)
    {
        var result = ThreatEventNormalizer.NormalizeSeverity(suricata);
        Assert.Equal(expected, result);
    }

    // --- NormalizeAction ---

    [Theory]
    [InlineData("drop", ThreatAction.Blocked)]
    [InlineData("reject", ThreatAction.Blocked)]
    [InlineData("blocked", ThreatAction.Blocked)]
    [InlineData("alert", ThreatAction.Detected)]
    [InlineData("pass", ThreatAction.Detected)]
    [InlineData("allowed", ThreatAction.Detected)]
    [InlineData("detected", ThreatAction.Detected)]
    [InlineData("unknown", ThreatAction.Detected)]
    [InlineData("", ThreatAction.Detected)]
    [InlineData("DROP", ThreatAction.Blocked)]
    [InlineData("Alert", ThreatAction.Detected)]
    public void NormalizeAction_MapsCorrectly(string action, ThreatAction expected)
    {
        var result = ThreatEventNormalizer.NormalizeAction(action);
        Assert.Equal(expected, result);
    }

    // --- Dedup by InnerAlertId ---

    [Fact]
    public void NormalizeV1Events_DuplicateIds_ReturnsAll()
    {
        // The normalizer itself does not dedup - it returns all events.
        // Dedup is expected at the repository/save layer. Verify both come through.
        var json = JsonSerializer.Serialize(new[]
        {
            new { _id = "dup001", timestamp = 1700000000000L, src_ip = "192.0.2.10", src_port = 0, dest_ip = "198.51.100.1", dest_port = 80, proto = "TCP", catname = "", alert = new { signature_id = 1L, signature = "Sig1", category = "Cat", severity = 3, action = "alert" } },
            new { _id = "dup001", timestamp = 1700000001000L, src_ip = "192.0.2.10", src_port = 0, dest_ip = "198.51.100.1", dest_port = 80, proto = "TCP", catname = "", alert = new { signature_id = 1L, signature = "Sig1", category = "Cat", severity = 3, action = "alert" } }
        });

        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV1Events(element);

        // Normalizer returns both - dedup by InnerAlertId happens upstream
        Assert.Equal(2, results.Count);
        Assert.All(results, e => Assert.Equal("dup001", e.InnerAlertId));
    }

    [Fact]
    public void NormalizeV1Events_DedupByInnerAlertId_CanBeAppliedPostNormalization()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { _id = "dup001", timestamp = 1700000000000L, src_ip = "192.0.2.10", src_port = 0, dest_ip = "198.51.100.1", dest_port = 80, proto = "TCP", catname = "", alert = new { signature_id = 1L, signature = "Sig1", category = "Cat", severity = 3, action = "alert" } },
            new { _id = "dup001", timestamp = 1700000001000L, src_ip = "192.0.2.10", src_port = 0, dest_ip = "198.51.100.1", dest_port = 80, proto = "TCP", catname = "", alert = new { signature_id = 1L, signature = "Sig1", category = "Cat", severity = 3, action = "alert" } },
            new { _id = "dup002", timestamp = 1700000002000L, src_ip = "192.0.2.11", src_port = 0, dest_ip = "198.51.100.2", dest_port = 22, proto = "TCP", catname = "", alert = new { signature_id = 2L, signature = "Sig2", category = "Cat", severity = 1, action = "drop" } }
        });

        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV1Events(element);

        // Apply dedup by InnerAlertId
        var deduped = results
            .GroupBy(e => e.InnerAlertId)
            .Select(g => g.First())
            .ToList();

        Assert.Equal(2, deduped.Count);
        Assert.Contains(deduped, e => e.InnerAlertId == "dup001");
        Assert.Contains(deduped, e => e.InnerAlertId == "dup002");
    }

    [Fact]
    public void NormalizeV1Events_TimestampConversion_IsUtc()
    {
        // 1700000000000 ms = 2023-11-14T22:13:20 UTC
        var json = JsonSerializer.Serialize(new[]
        {
            new { _id = "evt001", timestamp = 1700000000000L, src_ip = "192.0.2.10", src_port = 0, dest_ip = "198.51.100.1", dest_port = 80, proto = "TCP", catname = "", alert = new { signature_id = 1L, signature = "Sig", category = "Cat", severity = 3, action = "alert" } }
        });

        var element = JsonDocument.Parse(json).RootElement;
        var results = _normalizer.NormalizeV1Events(element);

        Assert.Equal(DateTimeKind.Utc, results[0].Timestamp.Kind);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), results[0].Timestamp);
    }
}

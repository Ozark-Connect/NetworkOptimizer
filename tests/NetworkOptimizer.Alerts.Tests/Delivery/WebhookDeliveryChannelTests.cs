using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Alerts.Delivery;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests.Delivery;

public class WebhookDeliveryChannelTests
{
    [Fact]
    public void ComputeHmacSha256_KnownInput_ProducesExpectedHash()
    {
        var payload = "{\"title\":\"Test\"}";
        var secret = "test-secret-key";

        var hash = WebhookDeliveryChannel.ComputeHmacSha256(payload, secret);

        hash.Should().NotBeNullOrEmpty();
        hash.Should().MatchRegex("^[0-9a-f]+$"); // Hex string
    }

    [Fact]
    public void ComputeHmacSha256_SameInputSameSecret_ProducesSameHash()
    {
        var payload = "{\"event\":\"test\"}";
        var secret = "my-secret";

        var hash1 = WebhookDeliveryChannel.ComputeHmacSha256(payload, secret);
        var hash2 = WebhookDeliveryChannel.ComputeHmacSha256(payload, secret);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeHmacSha256_DifferentSecrets_ProduceDifferentHashes()
    {
        var payload = "{\"event\":\"test\"}";

        var hash1 = WebhookDeliveryChannel.ComputeHmacSha256(payload, "secret-1");
        var hash2 = WebhookDeliveryChannel.ComputeHmacSha256(payload, "secret-2");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ComputeHmacSha256_DifferentPayloads_ProduceDifferentHashes()
    {
        var secret = "shared-secret";

        var hash1 = WebhookDeliveryChannel.ComputeHmacSha256("{\"a\":1}", secret);
        var hash2 = WebhookDeliveryChannel.ComputeHmacSha256("{\"b\":2}", secret);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ComputeHmacSha256_EmptyPayload_StillProducesHash()
    {
        var hash = WebhookDeliveryChannel.ComputeHmacSha256("", "secret");

        hash.Should().NotBeNullOrEmpty();
    }

    /// <summary>Answers 200 and keeps the body, so a test can read the payload that was posted.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static (WebhookDeliveryChannel Channel, CapturingHandler Handler) Build()
    {
        var handler = new CapturingHandler();
        return (new WebhookDeliveryChannel(NullLogger<WebhookDeliveryChannel>.Instance, new HttpClient(handler),
            Mock.Of<ISecretDecryptor>()), handler);
    }

    private static readonly DeliveryChannel Hook = new()
    {
        Id = 1,
        Name = "Hook",
        ChannelType = DeliveryChannelType.Webhook,
        ConfigJson = "{\"Url\":\"https://hooks.example.invalid/alerts\"}",
    };

    private static JsonElement Posted(CapturingHandler handler) => JsonDocument.Parse(handler.Body!).RootElement;

    [Fact]
    public async Task Digest_StoredTriggeredAt_IsSentAsUtc()
    {
        // SQLite returns stored times with no kind; they are UTC and must say so on the wire.
        var stored = new DateTime(2026, 11, 4, 13, 41, 58, DateTimeKind.Unspecified);
        var alerts = new List<AlertHistoryEntry> { new() { Title = "A", Source = "test", Severity = AlertSeverity.Warning, TriggeredAt = stored } };
        var (channel, handler) = Build();

        await channel.SendDigestAsync(alerts, Hook, DigestSummary.FromAlerts(alerts));

        Posted(handler).GetProperty("alerts")[0].GetProperty("triggered_at").GetString()
            .Should().Be("2026-11-04T13:41:58.0000000Z");
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public async Task Alert_Timestamp_IsSentAsUtc(DateTimeKind kind)
    {
        var at = new DateTime(2026, 11, 4, 13, 41, 58, kind);
        var alert = new AlertEvent { EventType = "test.event", Source = "test", Title = "A", Timestamp = at };
        var (channel, handler) = Build();

        await channel.SendAsync(alert, new AlertHistoryEntry(), Hook);

        Posted(handler).GetProperty("timestamp").GetString().Should().Be("2026-11-04T13:41:58.0000000Z");
    }
}

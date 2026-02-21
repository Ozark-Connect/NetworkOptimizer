using FluentAssertions;
using NetworkOptimizer.Alerts.Delivery;
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
}

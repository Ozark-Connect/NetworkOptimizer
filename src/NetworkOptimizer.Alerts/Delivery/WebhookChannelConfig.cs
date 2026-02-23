namespace NetworkOptimizer.Alerts.Delivery;

public class WebhookChannelConfig
{
    public string Url { get; set; } = string.Empty;
    public string? Secret { get; set; } // Stored encrypted, used for HMAC-SHA256 signature
    public string? PayloadTemplate { get; set; } // Scriban template; null = default JSON
    public Dictionary<string, string> Headers { get; set; } = new();
}

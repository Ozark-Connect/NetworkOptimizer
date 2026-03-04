namespace NetworkOptimizer.Alerts.Delivery;

public class NtfyChannelConfig
{
    public string ServerUrl { get; set; } = "https://ntfy.sh";
    public string Topic { get; set; } = string.Empty;
    public string? AccessToken { get; set; } // Stored encrypted, for Bearer auth
    public string? Username { get; set; }
    public string? Password { get; set; } // Stored encrypted, for Basic auth
}

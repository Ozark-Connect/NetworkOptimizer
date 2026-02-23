namespace NetworkOptimizer.Alerts.Delivery;

public class EmailChannelConfig
{
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public bool UseStartTls { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty; // Stored encrypted
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Network Optimizer";
    public string ToAddresses { get; set; } = string.Empty; // Comma-separated
}

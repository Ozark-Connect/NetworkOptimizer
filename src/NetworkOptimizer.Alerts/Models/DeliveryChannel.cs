using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts.Models;

/// <summary>
/// Delivery channel types.
/// </summary>
public enum DeliveryChannelType
{
    Email,
    Webhook,
    Slack,
    Discord,
    Teams
}

/// <summary>
/// A configured delivery channel (e.g., an SMTP server, a Slack webhook URL).
/// </summary>
public class DeliveryChannel
{
    public int Id { get; set; }

    /// <summary>
    /// Display name (e.g., "Ops Team Slack", "Client Email").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this channel is active.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Channel type.
    /// </summary>
    public DeliveryChannelType ChannelType { get; set; }

    /// <summary>
    /// JSON-serialized channel-specific configuration (SMTP settings, webhook URL, etc.).
    /// </summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>
    /// Minimum severity for alerts sent to this channel.
    /// </summary>
    public AlertSeverity MinSeverity { get; set; } = AlertSeverity.Warning;

    /// <summary>
    /// Whether digest summaries are sent to this channel.
    /// </summary>
    public bool DigestEnabled { get; set; }

    /// <summary>
    /// Digest schedule (e.g., "daily:08:00", "weekly:monday:08:00").
    /// </summary>
    public string? DigestSchedule { get; set; }

    /// <summary>
    /// When this channel was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this channel was last modified.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}

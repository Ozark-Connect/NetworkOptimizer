using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>Which transport a custom app definition matches.</summary>
public enum CustomAppProtocol
{
    Tcp = 0,
    Udp = 1,
    /// <summary>TCP and UDP.</summary>
    Both = 2,
}

/// <summary>Which end of a flow carries the definition's port.</summary>
public enum CustomAppPortOn
{
    /// <summary>The remote end: the service's port, for most apps.</summary>
    Destination = 0,
    /// <summary>The client's own port.</summary>
    Source = 1,
    /// <summary>Either end, for peer-to-peer traffic where either side can be the server.</summary>
    Either = 2,
}

/// <summary>Which flows a custom app definition applies to by where they go.</summary>
public enum CustomAppScope
{
    Anywhere = 0,
    /// <summary>Between the site's own devices.</summary>
    Lan = 1,
    /// <summary>To the internet (or a VPN).</summary>
    Internet = 2,
}

/// <summary>
/// A site's own name for traffic UniFi Network's DPI does not recognize, matched by protocol, port,
/// and optionally the remote end. Live Traffic checks these first, in <see cref="SortOrder"/>.
/// </summary>
public class CustomAppDefinition
{
    [Key]
    public int Id { get; set; }

    /// <summary>Position in the list: the first definition that matches a flow names it.</summary>
    public int SortOrder { get; set; }

    /// <summary>The app name shown everywhere Live Traffic shows an app.</summary>
    [MaxLength(80)]
    public string Name { get; set; } = "";

    public CustomAppProtocol Protocol { get; set; }

    /// <summary>One port, a range ("5000-5010"), or a comma-separated list of either.</summary>
    [MaxLength(200)]
    public string Ports { get; set; } = "";

    public CustomAppPortOn PortOn { get; set; }

    public CustomAppScope Scope { get; set; }

    /// <summary>A domain suffix, an address, or a CIDR the remote end must match; null matches any.</summary>
    [MaxLength(255)]
    public string? Remote { get; set; }

    /// <summary>A Font Awesome class for the icon; null draws the name's initial.</summary>
    [MaxLength(80)]
    public string? IconClass { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

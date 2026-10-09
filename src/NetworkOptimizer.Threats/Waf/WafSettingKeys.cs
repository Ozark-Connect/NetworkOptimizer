namespace NetworkOptimizer.Threats.Waf;

/// <summary>
/// Instance-wide SystemSettings keys for the netopt-waf connection (main database).
/// </summary>
public static class WafSettingKeys
{
    /// <summary>"true" when Network Optimizer reads the WAF.</summary>
    public const string Enabled = "waf.enabled";

    /// <summary>Base URL of netopt-waf, e.g. http://127.0.0.1:8044.</summary>
    public const string Url = "waf.url";

    /// <summary>WAF_API_TOKEN, stored encrypted.</summary>
    public const string Token = "waf.api_token";

    /// <summary>"{instance}:{next}" read position; not user-facing.</summary>
    public const string Cursor = "waf.cursor";
}

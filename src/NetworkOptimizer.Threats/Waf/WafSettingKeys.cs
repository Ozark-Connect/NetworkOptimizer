namespace NetworkOptimizer.Threats.Waf;

/// <summary>
/// SystemSettings keys for a site's netopt-waf connection (main database). Each site may run its
/// own WAF; use <see cref="ForSite"/>. The default site keeps the bare keys.
/// </summary>
public static class WafSettingKeys
{
    /// <summary>The key for a site. Null or empty is the default site, which keeps the bare key.</summary>
    public static string ForSite(string key, string? site) => string.IsNullOrEmpty(site) ? key : $"{key}:{site}";

    /// <summary>"true" when Network Optimizer reads the WAF.</summary>
    public const string Enabled = "waf.enabled";

    /// <summary>Base URL of netopt-waf, e.g. http://127.0.0.1:8044.</summary>
    public const string Url = "waf.url";

    /// <summary>WAF_API_TOKEN, stored encrypted.</summary>
    public const string Token = "waf.api_token";

    /// <summary>"{instance}:{next}" read position; not user-facing.</summary>
    public const string Cursor = "waf.cursor";
}

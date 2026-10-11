namespace NetworkOptimizer.Threats.Waf;

/// <summary>
/// Copy-ready exclusions for waf-rules/before-crs.conf: switch one CRS rule off for one host and
/// path, the narrowest exclusion that silences a false positive.
/// </summary>
public static class WafExclusion
{
    /// <summary>
    /// A runtime exclusion of <paramref name="ruleId"/> for requests to <paramref name="host"/> whose
    /// path starts with <paramref name="path"/>. Either may be empty to widen it.
    /// </summary>
    public static string ForRule(long ruleId, string? host, string? path)
    {
        host = Clean(host);
        path = Clean(path);
        // A stable ID in the range CRS leaves to users (1000-99999), so pasting it twice is visible.
        var id = 10000 + (int)((uint)StringComparer.Ordinal.GetHashCode($"{ruleId}|{host}|{path}") % 89999u);
        var remove = $"ctl:ruleRemoveById={ruleId}";

        if (host.Length > 0 && path.Length > 0)
            return $"SecRule REQUEST_HEADERS:Host \"@streq {host}\" \"id:{id},phase:1,pass,nolog,chain\"\n" +
                   $"    SecRule REQUEST_URI \"@beginsWith {path}\" \"{remove}\"";
        if (host.Length > 0)
            return $"SecRule REQUEST_HEADERS:Host \"@streq {host}\" \"id:{id},phase:1,pass,nolog,{remove}\"";
        if (path.Length > 0)
            return $"SecRule REQUEST_URI \"@beginsWith {path}\" \"id:{id},phase:1,pass,nolog,{remove}\"";
        return $"SecRuleRemoveById {ruleId}";
    }

    /// <summary>The path part of a stored WAF event's "METHOD /path" service string.</summary>
    public static string? PathOf(string? service)
    {
        if (string.IsNullOrEmpty(service)) return null;
        var space = service.IndexOf(' ');
        return space < 0 ? service : service[(space + 1)..];
    }

    // Quotes and backslashes would end the SecLang string early; whitespace never belongs in either.
    private static string Clean(string? value) =>
        new string((value ?? "").Where(c => c != '"' && c != '\\' && !char.IsWhiteSpace(c)).ToArray());
}

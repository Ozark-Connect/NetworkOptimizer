namespace NetworkOptimizer.Web.Services;

/// <summary>
/// One entry in the family/model filter: a dash prefix of the site's SKUs.
/// </summary>
/// <param name="Prefix">The SKU prefix, e.g. "U7-Pro".</param>
/// <param name="Depth">How many kept options sit above this one.</param>
/// <param name="Count">Devices whose SKU is the prefix or starts with it plus a dash.</param>
public sealed record DeviceModelFilterOption(string Prefix, int Depth, int Count)
{
    /// <summary>The option text, indented by depth with non-breaking spaces (a select collapses plain ones).</summary>
    public string Label => new string(' ', Depth * 3) + $"{Prefix} ({Count})";
}

/// <summary>
/// Builds the LAN Speed Test family/model filter from the SKUs present on a site. Every dash prefix
/// of every SKU is a candidate; one with no device of its own and a single child is dropped, so a
/// lone UAP-IW-HD shows as one option rather than UAP, UAP-IW, and UAP-IW-HD with the same count.
/// </summary>
public static class DeviceModelFilter
{
    /// <summary>
    /// The options for <paramref name="skus"/> (one entry per device, color variant already folded,
    /// as <c>UniFiProductDatabase.GetModelFamily</c> returns it), in tree order.
    /// </summary>
    public static List<DeviceModelFilterOption> BuildOptions(IEnumerable<string?> skus)
    {
        var exact = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var roots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in skus)
        {
            var sku = raw?.Trim();
            if (string.IsNullOrEmpty(sku)) continue;

            exact[sku] = exact.GetValueOrDefault(sku) + 1;

            var segments = sku.Split('-');
            string? parent = null;
            for (var i = 1; i <= segments.Length; i++)
            {
                var prefix = string.Join('-', segments, 0, i);
                if (!children.ContainsKey(prefix))
                    children[prefix] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                if (parent == null) roots.Add(prefix);
                else children[parent].Add(prefix);
                parent = prefix;
            }
        }

        var options = new List<DeviceModelFilterOption>();
        foreach (var root in roots)
            Walk(root, 0, exact, children, options);
        return options;
    }

    /// <summary>Whether a device with <paramref name="sku"/> falls under the <paramref name="prefix"/> option.</summary>
    public static bool Matches(string? sku, string prefix) =>
        !string.IsNullOrEmpty(sku)
        && (sku.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || sku.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase));

    private static int Walk(string node, int depth, Dictionary<string, int> exact,
        Dictionary<string, SortedSet<string>> children, List<DeviceModelFilterOption> options)
    {
        var own = exact.GetValueOrDefault(node);
        var kids = children[node];
        var kept = own > 0 || kids.Count != 1;

        // Reserve the slot so the parent lists ahead of its children; its count is known after them.
        var slot = options.Count;
        if (kept) options.Add(null!);

        var total = own;
        foreach (var child in kids)
            total += Walk(child, kept ? depth + 1 : depth, exact, children, options);

        if (kept) options[slot] = new DeviceModelFilterOption(node, depth, total);
        return total;
    }
}

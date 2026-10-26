using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// Drift (spec "Drift"): the diff between the latest snapshot and the Baseline. A snapshot Baseline
/// compares every collection; a template Baseline compares only its collections and only the
/// objects it defines, matched across sites by key and name.
/// </summary>
public static class DriftCalculator
{
    /// <summary>Drifted objects as change records (fields: Baseline value to current value).</summary>
    public static List<ConfigChangeObject> Drift(ConfigState baseline, ConfigState current, PlanOptions options)
    {
        // Template: what the template defines, compared with the site's matching objects.
        var diff = options.SameSite
            ? ConfigDiffEngine.Compare(baseline, current)
            : ConfigDiffEngine.Compare(baseline, Restrict(current, baseline), sameSite: false);
        var beforeNames = ConfigIdentityContext.ForBefore(baseline, current);
        var afterNames = new ConfigIdentityContext(current.NamesById());
        var objects = diff.Objects.Where(o => !o.Generated && o.OwnedBy == null).ToList();
        if (!options.SameSite || options.DeleteMissing == false)
            objects = objects.Where(o => o.Action != ConfigChangeAction.Created).ToList();
        return objects.Select(o => ChangeControlSiteEngine.ToRecord(o, beforeNames, afterNames)).ToList();
    }

    /// <summary>The current state limited to the template's collections.</summary>
    private static ConfigState Restrict(ConfigState current, ConfigState template)
    {
        var restricted = new ConfigState { ReadAt = current.ReadAt, NetworkAppVersion = current.NetworkAppVersion };
        foreach (var key in template.Collections.Keys)
            if (current.Collections.TryGetValue(key, out var list)) restricted.Collections[key] = list;
        return restricted;
    }
}

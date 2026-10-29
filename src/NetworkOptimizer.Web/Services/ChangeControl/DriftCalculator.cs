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
        // Template: what the template defines, compared with the site's matching objects. Its references
        // (the networks its PPSKs name) match through Compare but are not the template's, so they never drift.
        if (!options.SameSite) baseline = AsAPlanWouldWrite(baseline, current, options);
        var diff = options.SameSite
            ? ConfigDiffEngine.Compare(baseline, current)
            : ConfigDiffEngine.Compare(baseline, Restrict(current, baseline), sameSite: false);
        var beforeNames = ConfigIdentityContext.ForBefore(baseline, current);
        var afterNames = new ConfigIdentityContext(current.NamesById());
        var objects = diff.Objects.Where(o => !o.Generated && o.OwnedBy == null
            && (options.SameSite || options.Collections == null || options.Collections.Contains(o.Collection))).ToList();
        if (!options.SameSite || options.DeleteMissing == false)
            objects = objects.Where(o => o.Action != ConfigChangeAction.Created).ToList();
        // A snapshot Baseline's policy order too, as Compare shows it and Return to Baseline restores it. A template
        // Baseline matches policies across sites by name, so its order is not compared.
        if (options.SameSite)
            objects.AddRange(diff.PolicyOrder.Select(order => ChangeControlSiteEngine.PolicyOrderObject(order, afterNames)));
        return objects.Select(o => ChangeControlSiteEngine.ToRecord(o, beforeNames, afterNames)).ToList();
    }

    /// <summary>
    /// The template as a plan onto this site would write it: a field the template does not carry is
    /// the site's own (PlanBuilder: unspecified, not unset), and merged lists hold the site's entries
    /// too. A WLAN pared down to its PPSKs is then drift only for PPSKs the site lacks.
    /// </summary>
    private static ConfigState AsAPlanWouldWrite(ConfigState template, ConfigState current, PlanOptions options)
    {
        var written = template.Clone();
        var templateCtx = new ConfigIdentityContext(written.NamesById());
        var currentCtx = new ConfigIdentityContext(current.NamesById());
        foreach (var def in ConfigCollections.All.Where(d => written.Collections.ContainsKey(d.Key) && current.Collections.ContainsKey(d.Key)))
        {
            var match = ObjectMatcher.Match(def, written.Get(def.Key), current.Get(def.Key), templateCtx, currentCtx, matchById: false);
            foreach (var pair in match.Pairs.Where(p => p.Left != null && p.Right != null))
            {
                foreach (var (key, value) in pair.Right!)
                    if (!pair.Left!.ContainsKey(key)) pair.Left[key] = value?.DeepClone();
                if (options.MergeLists) PlanBuilder.MergeListsInto(def, pair.Left!, pair.Right!);
            }
        }
        return written;
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

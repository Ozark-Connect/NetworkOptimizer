using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

public sealed partial class ChangeControlService
{
    public async Task<int> BuildAuditFixPlanAsync(string issueType, CancellationToken cancellationToken = default)
    {
        if (!DnsFixes.Types.Contains(issueType))
            throw new InvalidOperationException("Network Optimizer cannot fix this finding for you yet.");
        // Read again now: the finding on screen can be older than the config.
        var live = await Engine.ReadLiveAsync(cancellationToken);
        if (ZoneSetup.IsLegacy(live))
            throw new InvalidOperationException("This site still runs the legacy firewall. Fix it for me needs the zone-based firewall.");
        var fix = DnsFixes.Build(live, issueType)
            ?? throw new InvalidOperationException("Nothing to fix: every network this can cover is covered, or the fix needs a choice only you can make.");
        var plan = await Plans.CreateAsync(ConfigPlanKind.AuditFix, $"{fix.Title} on {await SiteNameAsync(cancellationToken)}", fix.Title, null, null,
            fix.Desired, new PlanOptions { Collections = new HashSet<string> { "firewall_policy", "nat", "firewallgroup" }, DeleteMissing = false },
            Actor, cancellationToken, doc => doc.Notes.AddRange(fix.Notes));
        return plan.Id;
    }
}

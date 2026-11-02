using NetworkOptimizer.Web.Services.ChangeControl;

namespace NetworkOptimizer.Web.Components.Shared.ChangeControl;

/// <summary>
/// The Set Up Zones board's choices, kept while Review Plan is open so Back returns to them, as
/// <see cref="CcEditDrafts"/> keeps Edit's. Per circuit, in memory only.
/// </summary>
public sealed class CcZoneDrafts
{
    /// <summary>The board's choices and the plan Review Plan built from them.</summary>
    public sealed record Draft(ZoneChoices Choices, int PlanId);

    private readonly Dictionary<string, Draft> _drafts = [];

    /// <summary>Keeps a site's board choices, replacing any it kept before.</summary>
    public void Keep(string site, Draft draft) => _drafts[site] = draft;

    /// <summary>Takes back a site's board choices, once: a later visit opens on the suggestion.</summary>
    public Draft? Take(string site) => _drafts.Remove(site, out var draft) ? draft : null;
}

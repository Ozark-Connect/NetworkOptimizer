namespace NetworkOptimizer.Web.Components.Shared.ChangeControl;

/// <summary>
/// A Migrate / Restore wizard's choices and the plan Review Plan built from them, kept while Review Plan is open so
/// Back returns to them and a plan built again replaces this one, as <see cref="CcEditDrafts"/> does for Edit. Keyed
/// by task and site. Per circuit, in memory only.
/// </summary>
public sealed class CcPlanDrafts
{
    /// <summary>The wizard's choices (null when the URL already carries them) and the plan built from them.</summary>
    public sealed record Draft(object? Choices, int PlanId);

    private readonly Dictionary<string, Draft> _drafts = [];

    /// <summary>Keeps a wizard's choices for a site, replacing any it kept before.</summary>
    public void Keep(string task, string site, Draft draft) => _drafts[$"{task}|{site}"] = draft;

    /// <summary>Takes back a wizard's choices for a site, once: a later visit opens fresh.</summary>
    public Draft? Take(string task, string site) => _drafts.Remove($"{task}|{site}", out var draft) ? draft : null;
}

namespace NetworkOptimizer.Web.Components.Shared.ChangeControl;

/// <summary>
/// The Edit tab's unsaved edits, kept while Review Plan is open so Back returns to them. Per circuit, in
/// memory only: the YAML can hold secrets, so it never goes to browser storage.
/// </summary>
public sealed class CcEditDrafts
{
    /// <summary>
    /// One editor's edits: what it started from, the edited text by group, the groups from Upload YAML,
    /// and the plan Review Plan built from them.
    /// </summary>
    public sealed record Draft(string StartFrom, IReadOnlyDictionary<string, string> Edited, IReadOnlySet<string> Uploaded, int PlanId);

    private readonly Dictionary<string, Draft> _drafts = [];

    /// <summary>Keeps an editor's edits, replacing any it kept before.</summary>
    public void Keep(string scope, Draft draft) => _drafts[scope] = draft;

    /// <summary>Takes back an editor's edits, once: a later visit opens fresh.</summary>
    public Draft? Take(string scope) => _drafts.Remove(scope, out var draft) ? draft : null;
}

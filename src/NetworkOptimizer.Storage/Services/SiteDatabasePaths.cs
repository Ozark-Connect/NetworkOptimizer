namespace NetworkOptimizer.Storage.Services;

/// <summary>
/// Resolves per-site SQLite database paths. The default site uses the main
/// database file unchanged; every other site gets its own database under
/// a sites/{slug}/ folder next to the main database.
/// </summary>
public class SiteDatabasePaths
{
    /// <summary>Path of the main database file (registry + default site data).</summary>
    public string MainDbPath { get; }

    /// <summary>Root folder holding one subfolder per non-default site.</summary>
    public string SitesRoot { get; }

    /// <summary>
    /// Slug of the default site. Site creation reserves it, so no managed site can ever take it -
    /// which means a path request for it always means the main database, whatever the caller
    /// believes about the flag.
    /// </summary>
    public const string DefaultSiteSlug = "main";

    public SiteDatabasePaths(string mainDbPath)
    {
        MainDbPath = mainDbPath;
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(mainDbPath))
            ?? throw new ArgumentException($"Cannot resolve data folder from '{mainDbPath}'", nameof(mainDbPath));
        SitesRoot = Path.Combine(dataDir, "sites");
    }

    /// <summary>Data folder for a non-default site, created on demand elsewhere.</summary>
    public string GetSiteDataDir(string slug) => Path.Combine(SitesRoot, slug);

    /// <summary>
    /// Database file path for a site; the default site maps to the main database.
    ///
    /// The slug decides that as well as the flag. Callers that only ever handle managed sites pass
    /// isDefault: false as a constant, which was correct until something started asking about the
    /// default site through the same path - then it pointed at sites/main/network_optimizer.db, a
    /// file that cannot exist, and the caller concluded the site was not provisioned. There is no
    /// case where the default slug should resolve anywhere but the main database, so it no longer
    /// depends on the caller getting the flag right.
    /// </summary>
    public string GetSiteDbPath(string slug, bool isDefault) =>
        isDefault || string.Equals(slug, DefaultSiteSlug, StringComparison.OrdinalIgnoreCase)
            ? MainDbPath
            : Path.Combine(GetSiteDataDir(slug), "network_optimizer.db");
}

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

    public SiteDatabasePaths(string mainDbPath)
    {
        MainDbPath = mainDbPath;
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(mainDbPath))
            ?? throw new ArgumentException($"Cannot resolve data folder from '{mainDbPath}'", nameof(mainDbPath));
        SitesRoot = Path.Combine(dataDir, "sites");
    }

    /// <summary>Data folder for a non-default site, created on demand elsewhere.</summary>
    public string GetSiteDataDir(string slug) => Path.Combine(SitesRoot, slug);

    /// <summary>Database file path for a site; the default site maps to the main database.</summary>
    public string GetSiteDbPath(string slug, bool isDefault) =>
        isDefault ? MainDbPath : Path.Combine(GetSiteDataDir(slug), "network_optimizer.db");
}

namespace NetworkOptimizer.Diagnostics.Analyzers;

/// <summary>
/// UniFi application IDs for QoS rule matching.
/// These IDs come from UniFi's built-in app identification engine.
/// </summary>
internal static class StreamingAppIds
{
    /// <summary>
    /// Streaming video apps (category_id 4 - Streaming Media)
    /// </summary>
    public static readonly HashSet<int> StreamingVideo = new()
    {
        262256, // YouTube
        262276, // Netflix
        262179, // Hulu
        262417, // Disney+
        262446, // HBO Max
        262337, // Amazon Video
        262163, // Twitch
        262228, // Crunchyroll
        262268, // Vudu
        262274, // Spotify
        262219, // Pandora
        262224, // SoundCloud
        262418, // Peacock
    };

    /// <summary>
    /// Cloud storage/sync apps (category_id 3 - File Sharing)
    /// </summary>
    public static readonly HashSet<int> CloudStorage = new()
    {
        196623, // Google Drive
        196629, // OneDrive
        196692, // Dropbox
        196676, // Box
        196758, // iCloud
        196764, // Backblaze
    };

    /// <summary>
    /// Large download apps (game stores, OS updates)
    /// </summary>
    public static readonly HashSet<int> LargeDownloads = new()
    {
        524399, // Valve Steam
        852104, // Epic Games
        524350, // Battle.net
        524510, // GOG
        917513, // Xbox Live
    };

    /// <summary>
    /// Human-readable names for app IDs used in recommendations.
    /// </summary>
    public static readonly Dictionary<int, string> AppNames = new()
    {
        [262256] = "YouTube",
        [262276] = "Netflix",
        [262179] = "Hulu",
        [262417] = "Disney+",
        [262446] = "HBO Max",
        [262337] = "Amazon Video",
        [262163] = "Twitch",
        [262228] = "Crunchyroll",
        [262268] = "Vudu",
        [262274] = "Spotify",
        [262219] = "Pandora",
        [262224] = "SoundCloud",
        [262418] = "Peacock",
        [196623] = "Google Drive",
        [196629] = "OneDrive",
        [196692] = "Dropbox",
        [196676] = "Box",
        [196758] = "iCloud",
        [196764] = "Backblaze",
        [524399] = "Steam",
        [852104] = "Epic Games",
        [524350] = "Battle.net",
        [524510] = "GOG",
        [917513] = "Xbox Live",
    };

    /// <summary>
    /// Minimum number of apps that must be targeted for each category to count as "covered".
    /// Streaming has many popular services so we require more coverage.
    /// Cloud storage: even one service covered is sufficient since most people use 1-2.
    /// </summary>
    public const int MinStreamingForCoverage = 3;
    public const int MinCloudForCoverage = 1;
    public const int MinDownloadsForCoverage = 2;
}

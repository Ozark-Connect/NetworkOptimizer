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
        262219, // Amazon Prime Music
        262224, // Web Streaming
        262418, // Apple TV+
        262392, // TikTok
        262350, // Plex.tv
        262420, // fuboTV
        262186, // Apple Music
        262154, // iTunes/App Store
        262174, // BBC iPlayer
    };

    /// <summary>
    /// Cloud storage/sync apps (category_id 3 - File Sharing)
    /// </summary>
    public static readonly HashSet<int> CloudStorage = new()
    {
        196623, // Google Drive
        196629, // OneDrive
        196692, // Dropbox
        196758, // iCloud
        196764, // Backblaze
    };

    /// <summary>
    /// Large download apps (game stores, OS updates)
    /// </summary>
    public static readonly HashSet<int> LargeDownloads = new()
    {
        524399, // Valve Steam
        524567, // Epic Games
        524356, // Battle.net
        524350, // Xbox
        524430, // Sony PlayStation
        917513, // Windows Update
        852104, // Microsoft Store
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
        [262219] = "Amazon Prime Music",
        [262224] = "Web Streaming",
        [262418] = "Apple TV+",
        [262392] = "TikTok",
        [262350] = "Plex",
        [262420] = "fuboTV",
        [262186] = "Apple Music",
        [262154] = "iTunes/App Store",
        [262174] = "BBC iPlayer",
        [196623] = "Google Drive",
        [196629] = "OneDrive",
        [196692] = "Dropbox",
        [196758] = "iCloud",
        [196764] = "Backblaze",
        [524399] = "Steam",
        [524567] = "Epic Games",
        [524356] = "Battle.net",
        [524350] = "Xbox",
        [524430] = "PlayStation",
        [917513] = "Windows Update",
        [852104] = "Microsoft Store",
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

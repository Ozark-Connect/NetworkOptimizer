namespace NetworkOptimizer.Web;

/// <summary>
/// Whose download a speed test's two figures are read as. The stored fields never change meaning:
/// <c>DownloadMbps</c> is client to server, as iperf3 measured it. A surface picks the frame.
/// </summary>
public enum SpeedPerspective
{
    /// <summary>The speed test server's: From Device, then To Device.</summary>
    Server,
    /// <summary>The site's: Download from the internet, then Upload to it.</summary>
    Wan,
    /// <summary>The tested device's: Download is what it receives, so the server's pair swapped.</summary>
    Device,
}

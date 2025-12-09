namespace NetworkOptimizer.Storage;

/// <summary>
/// Configuration settings for InfluxDB storage
/// </summary>
public class StorageConfiguration
{
    public string Url { get; set; } = "http://localhost:8086";
    public string Token { get; set; } = string.Empty;
    public string Organization { get; set; } = "NetworkOptimizer";
    public string Bucket { get; set; } = "network_metrics";
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; set; } = 3;
    public int BatchFlushIntervalSeconds { get; set; } = 5;
    public int MaxBufferSize { get; set; } = 1000;
}

/// <summary>
/// Configuration settings for SQLite local storage
/// </summary>
public class SqliteConfiguration
{
    public string DatabasePath { get; set; } = "networkoptimizer.db";
    public bool EnableSensitiveDataLogging { get; set; } = false;
    public int CommandTimeout { get; set; } = 30;
}

using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for reading and writing system-wide settings
/// </summary>
public class SystemSettingsService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SystemSettingsService> _logger;

    // Default values
    public const int DefaultIperf3Duration = 10;
    public const int DefaultIperf3Port = 5201;

    // Per-device-type defaults
    public const int DefaultIperf3GatewayParallelStreams = 3;
    public const int DefaultIperf3UniFiParallelStreams = 3;
    public const int DefaultIperf3OtherParallelStreams = 10;

    public SystemSettingsService(IServiceProvider serviceProvider, ILogger<SystemSettingsService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Get a setting value by key
    /// </summary>
    public async Task<string?> GetAsync(string key)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        return await repository.GetSystemSettingAsync(key);
    }

    /// <summary>
    /// Get a setting value as int with default
    /// </summary>
    public async Task<int> GetIntAsync(string key, int defaultValue)
    {
        var value = await GetAsync(key);
        if (string.IsNullOrEmpty(value) || !int.TryParse(value, out var result))
            return defaultValue;
        return result;
    }

    /// <summary>
    /// Set a setting value
    /// </summary>
    public async Task SetAsync(string key, string? value)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await repository.SaveSystemSettingAsync(key, value);
        _logger.LogInformation("System setting {Key} updated to {Value}", key, value);
    }

    /// <summary>
    /// Set a setting value as int
    /// </summary>
    public Task SetIntAsync(string key, int value) => SetAsync(key, value.ToString());

    /// <summary>
    /// Get iperf3 test duration setting
    /// </summary>
    public Task<int> GetIperf3DurationAsync() =>
        GetIntAsync(SystemSettingKeys.Iperf3Duration, DefaultIperf3Duration);

    /// <summary>
    /// Set iperf3 test duration setting
    /// </summary>
    public Task SetIperf3DurationAsync(int value) =>
        SetIntAsync(SystemSettingKeys.Iperf3Duration, value);

    /// <summary>
    /// Get iperf3 gateway parallel streams setting
    /// </summary>
    public Task<int> GetIperf3GatewayParallelStreamsAsync() =>
        GetIntAsync(SystemSettingKeys.Iperf3GatewayParallelStreams, DefaultIperf3GatewayParallelStreams);

    /// <summary>
    /// Set iperf3 gateway parallel streams setting
    /// </summary>
    public Task SetIperf3GatewayParallelStreamsAsync(int value) =>
        SetIntAsync(SystemSettingKeys.Iperf3GatewayParallelStreams, value);

    /// <summary>
    /// Get iperf3 UniFi device parallel streams setting
    /// </summary>
    public Task<int> GetIperf3UniFiParallelStreamsAsync() =>
        GetIntAsync(SystemSettingKeys.Iperf3UniFiParallelStreams, DefaultIperf3UniFiParallelStreams);

    /// <summary>
    /// Set iperf3 UniFi device parallel streams setting
    /// </summary>
    public Task SetIperf3UniFiParallelStreamsAsync(int value) =>
        SetIntAsync(SystemSettingKeys.Iperf3UniFiParallelStreams, value);

    /// <summary>
    /// Get iperf3 other device parallel streams setting
    /// </summary>
    public Task<int> GetIperf3OtherParallelStreamsAsync() =>
        GetIntAsync(SystemSettingKeys.Iperf3OtherParallelStreams, DefaultIperf3OtherParallelStreams);

    /// <summary>
    /// Set iperf3 other device parallel streams setting
    /// </summary>
    public Task SetIperf3OtherParallelStreamsAsync(int value) =>
        SetIntAsync(SystemSettingKeys.Iperf3OtherParallelStreams, value);

    /// <summary>
    /// Get all iperf3 settings as a DTO
    /// </summary>
    public async Task<Iperf3Settings> GetIperf3SettingsAsync()
    {
        return new Iperf3Settings
        {
            DurationSeconds = await GetIperf3DurationAsync(),
            GatewayParallelStreams = await GetIperf3GatewayParallelStreamsAsync(),
            UniFiParallelStreams = await GetIperf3UniFiParallelStreamsAsync(),
            OtherParallelStreams = await GetIperf3OtherParallelStreamsAsync()
        };
    }

    /// <summary>
    /// Save all iperf3 settings from a DTO
    /// </summary>
    public async Task SaveIperf3SettingsAsync(Iperf3Settings settings)
    {
        await SetIperf3DurationAsync(settings.DurationSeconds);
        await SetIperf3GatewayParallelStreamsAsync(settings.GatewayParallelStreams);
        await SetIperf3UniFiParallelStreamsAsync(settings.UniFiParallelStreams);
        await SetIperf3OtherParallelStreamsAsync(settings.OtherParallelStreams);
    }
}

/// <summary>
/// DTO for iperf3 test settings
/// </summary>
public class Iperf3Settings
{
    public int DurationSeconds { get; set; } = SystemSettingsService.DefaultIperf3Duration;
    public int GatewayParallelStreams { get; set; } = SystemSettingsService.DefaultIperf3GatewayParallelStreams;
    public int UniFiParallelStreams { get; set; } = SystemSettingsService.DefaultIperf3UniFiParallelStreams;
    public int OtherParallelStreams { get; set; } = SystemSettingsService.DefaultIperf3OtherParallelStreams;
}

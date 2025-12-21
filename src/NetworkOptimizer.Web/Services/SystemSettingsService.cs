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
    public const int DefaultIperf3ParallelStreams = 3;
    public const int DefaultIperf3Duration = 10;
    public const int DefaultIperf3Port = 5201;

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
    /// Get iperf3 parallel streams setting
    /// </summary>
    public Task<int> GetIperf3ParallelStreamsAsync() =>
        GetIntAsync(SystemSettingKeys.Iperf3ParallelStreams, DefaultIperf3ParallelStreams);

    /// <summary>
    /// Set iperf3 parallel streams setting
    /// </summary>
    public Task SetIperf3ParallelStreamsAsync(int value) =>
        SetIntAsync(SystemSettingKeys.Iperf3ParallelStreams, value);

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
    /// Get all iperf3 settings as a DTO
    /// </summary>
    public async Task<Iperf3Settings> GetIperf3SettingsAsync()
    {
        return new Iperf3Settings
        {
            ParallelStreams = await GetIperf3ParallelStreamsAsync(),
            DurationSeconds = await GetIperf3DurationAsync()
        };
    }

    /// <summary>
    /// Save all iperf3 settings from a DTO
    /// </summary>
    public async Task SaveIperf3SettingsAsync(Iperf3Settings settings)
    {
        await SetIperf3ParallelStreamsAsync(settings.ParallelStreams);
        await SetIperf3DurationAsync(settings.DurationSeconds);
    }
}

/// <summary>
/// DTO for iperf3 test settings
/// </summary>
public class Iperf3Settings
{
    public int ParallelStreams { get; set; } = SystemSettingsService.DefaultIperf3ParallelStreams;
    public int DurationSeconds { get; set; } = SystemSettingsService.DefaultIperf3Duration;
}

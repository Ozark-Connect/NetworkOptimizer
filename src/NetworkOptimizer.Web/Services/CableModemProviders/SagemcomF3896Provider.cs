using System.Globalization;
using System.Net;
using System.Text.Json;
using NetworkOptimizer.Core;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for the Sagemcom F3896LG, sold by Virgin Media as the Hub 5 and by
/// Ziggo as the SmartWifi modem. Its web UI reads DOCSIS state from a JSON REST API under
/// /rest/v1/cablemodem that answers without signing in, over HTTP or HTTPS.
/// </summary>
[VendorSpecific("Sagemcom", "F3896LG /rest/v1 JSON API: OFDM/OFDMA levels arrive in tenths")]
public sealed class SagemcomF3896Provider : ICableModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "sagemcom-f3896lg";

    /// <inheritdoc/>
    public string DisplayName => "Sagemcom F3896LG: Virgin Media Hub 5, Ziggo SmartWifi (HTTP)";

    internal const string DefaultBasePath = "/rest/v1/cablemodem";
    private const string LocalizationPath = "/rest/v1/system/localization";
    private const string FallbackModel = "Sagemcom";
    private const int TimeoutSeconds = 15;

    private readonly ILogger<SagemcomF3896Provider> _logger;

    public SagemcomF3896Provider(ILogger<SagemcomF3896Provider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<PollResult<CableModemStats>> PollAsync(
        CmPollContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Sagemcom F3896LG poll requested but Host is empty (config {Id})", context.Id);
            return PollResult<CableModemStats>.Failed("No address is configured for this device.");
        }

        try
        {
            var stats = await FetchStatsAsync(context, cancellationToken);

            _logger.LogDebug(
                "Sagemcom F3896LG {Name} polled: {Model}, {DsCount} DS channels, {UsCount} US channels",
                context.Name, stats.DeviceModel,
                stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);

            return PollResult<CableModemStats>.Ok(stats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Sagemcom F3896LG {Name} at {Host}", context.Name, context.ConfiguredHost ?? context.Host);
            return PollResult<CableModemStats>.Failed(DescribeFailure(ex, context.ConfiguredHost ?? context.Host));
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string Message)> TestConnectionAsync(
        CmPollContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (false, "Host is empty");

        try
        {
            var stats = await FetchStatsAsync(context, cancellationToken);
            return (true, $"Connected to {stats.DeviceModel} - " +
                $"{stats.DownstreamChannels.Count} downstream, {stats.UpstreamChannels.Count} upstream channels detected");
        }
        catch (Exception ex)
        {
            return (false, DescribeFailure(ex, context.ConfiguredHost ?? context.Host));
        }
    }

    private async Task<CableModemStats> FetchStatsAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        var baseUrl = BuildBaseUrl(context);
        var basePath = string.IsNullOrWhiteSpace(context.StatusPagePath)
            ? DefaultBasePath
            : context.StatusPagePath.TrimEnd('/');

        using var client = CreateClient();

        var downstreamTask = client.GetStringAsync($"{baseUrl}{basePath}/downstream", cancellationToken);
        var upstreamTask = client.GetStringAsync($"{baseUrl}{basePath}/upstream", cancellationToken);
        var modelTask = ReadDeviceModelAsync(client, baseUrl, context.Name, cancellationToken);
        await Task.WhenAll(downstreamTask, upstreamTask, modelTask);

        using var downstream = JsonDocument.Parse(downstreamTask.Result);
        using var upstream = JsonDocument.Parse(upstreamTask.Result);

        // A modem with no channels still returns the section with an empty array, so a missing
        // section means this is not the F3896LG API.
        if (!HasSection(downstream.RootElement, "downstream") || !HasSection(upstream.RootElement, "upstream"))
            throw new InvalidDataException("Response did not contain DOCSIS channel data");

        return Parse(downstream.RootElement, upstream.RootElement, context, modelTask.Result);
    }

    private static bool HasSection(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var section) &&
        section.ValueKind == JsonValueKind.Object;

    private static string DescribeFailure(Exception ex, string host) =>
        ex is JsonException or InvalidDataException
            ? HttpFailureSummary.ForResponse(null, host)
            : HttpFailureSummary.Describe(ex, host);

    /// <summary>
    /// The model is display-only, so a failure here falls back to the vendor name rather
    /// than failing a poll whose channel data arrived.
    /// </summary>
    private async Task<string> ReadDeviceModelAsync(
        HttpClient client, string baseUrl, string name, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await client.GetStringAsync(baseUrl + LocalizationPath, cancellationToken));
            return FormatDeviceModel(document.RootElement);
        }
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Sagemcom F3896LG {Name}: could not read the model", name);
            return FallbackModel;
        }
    }

    /// <summary>
    /// Vendor and model for the stats card, from /rest/v1/system/localization. The model
    /// code alone does not name the maker, so only the known F3896LG is prefixed with it;
    /// any other code keeps the ISP's product name beside it ("Hub 5 (F3897LG)").
    /// </summary>
    internal static string FormatDeviceModel(JsonElement root)
    {
        var localization = root.TryGetProperty("localization", out var inner) ? inner : root;
        var modelName = GetString(localization, "modelName").Trim();
        var productName = GetString(localization, "productName").Trim();

        if (modelName.Equals("F3896LG", StringComparison.OrdinalIgnoreCase))
            return $"Sagemcom {modelName}";

        if (modelName.Length > 0)
            return productName.Length > 0 ? $"{productName} ({modelName})" : modelName;

        return FallbackModel;
    }

    internal static CableModemStats Parse(
        JsonElement downstreamRoot, JsonElement upstreamRoot, CmPollContext context, string deviceModel)
    {
        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.ConfiguredHost ?? context.Host,
            DeviceName = context.Name,
            DeviceModel = deviceModel,
        };

        foreach (var channel in EnumerateChannels(downstreamRoot, "downstream"))
        {
            // OFDM reports rxMer and power in tenths (410 = 41.0 dB); SC-QAM reports whole units.
            var isOfdm = GetString(channel, "channelType").Equals("ofdm", StringComparison.OrdinalIgnoreCase);
            var scale = isOfdm ? 10.0 : 1.0;

            stats.DownstreamChannels.Add(new DsChannel
            {
                ChannelId = (int)(GetNumber(channel, "channelId") ?? 0),
                LockStatus = FormatLockStatus(channel),
                Modulation = isOfdm ? "OFDM" : FormatModulation(GetString(channel, "modulation")),
                // OFDM has no center frequency here, only a first-active-subcarrier index.
                Frequency = isOfdm ? 0 : (long)(GetNumber(channel, "frequency") ?? 0),
                Power = GetNumber(channel, "power") / scale,
                Snr = (GetNumber(channel, "rxMer") ?? GetNumber(channel, "snr")) / scale,
                Correctables = (long)(GetNumber(channel, "correctedErrors") ?? 0),
                Uncorrectables = (long)(GetNumber(channel, "uncorrectedErrors") ?? 0),
            });
        }

        foreach (var channel in EnumerateChannels(upstreamRoot, "upstream"))
        {
            // OFDMA power arrives in tenths (412 = 41.2 dBmV), like OFDM downstream.
            var channelType = GetString(channel, "channelType").ToUpperInvariant();
            var isOfdma = channelType == "OFDMA";

            stats.UpstreamChannels.Add(new UsChannel
            {
                ChannelId = (int)(GetNumber(channel, "channelId") ?? 0),
                LockStatus = FormatLockStatus(channel),
                ChannelType = channelType,
                Frequency = isOfdma ? 0 : (long)(GetNumber(channel, "frequency") ?? 0),
                Power = GetNumber(channel, "power") / (isOfdma ? 10.0 : 1.0),
                // Reported in ksym/s (5120); stored in sym/s like every other provider.
                SymbolRate = (long)((GetNumber(channel, "symbolRate") ?? 0) * 1000),
            });
        }

        return stats;
    }

    private static IEnumerable<JsonElement> EnumerateChannels(JsonElement root, string direction)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(direction, out var section) ||
            section.ValueKind != JsonValueKind.Object ||
            !section.TryGetProperty("channels", out var channels) ||
            channels.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var channel in channels.EnumerateArray())
        {
            if (channel.ValueKind == JsonValueKind.Object)
                yield return channel;
        }
    }

    /// <summary>
    /// lockStatus is a JSON boolean; the rest of the app counts the DOCSIS word "Locked".
    /// </summary>
    private static string FormatLockStatus(JsonElement channel) =>
        channel.TryGetProperty("lockStatus", out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
                                    || value.GetString()?.Equals("locked", StringComparison.OrdinalIgnoreCase) == true,
            _ => false,
        }
            ? "Locked"
            : "Not Locked";

    /// <summary>"qam_256" becomes "QAM256", the spelling the HTML-scraped providers carry.</summary>
    internal static string FormatModulation(string raw) =>
        raw.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double? GetNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            // The HTTPS side presents a self-signed certificate.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
    }

    private static string BuildBaseUrl(CmPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var scheme = port == 443 ? "https" : "http";
        var suffix = port is 80 or 443 ? "" : $":{port}";
        return $"{scheme}://{context.Host}{suffix}";
    }
}

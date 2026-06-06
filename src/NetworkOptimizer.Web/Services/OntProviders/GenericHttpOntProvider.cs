using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// Fallback skeleton provider for ONT brands not yet supported with dedicated parsing.
/// Returns null from PollAsync (no scraping logic) but provides basic HTTP connectivity testing.
/// </summary>
public class GenericHttpOntProvider : IOntProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GenericHttpOntProvider> _logger;

    public GenericHttpOntProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<GenericHttpOntProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string ProviderKey => "generic-http-ont";
    public string DisplayName => "Generic HTTP ONT";

    public Task<OntStats?> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Generic HTTP ONT provider cannot poll {Host} - needs configuration for a specific ONT model",
            context.Host);

        return Task.FromResult<OntStats?>(null);
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("OntProvider");
            client.Timeout = TimeSpan.FromSeconds(10);

            var port = context.Port > 0 ? context.Port : 80;
            var scheme = port == 443 ? "https" : "http";
            var url = port == 80 || port == 443
                ? $"{scheme}://{context.Host}/"
                : $"{scheme}://{context.Host}:{port}/";

            using var response = await client.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (true, $"HTTP {(int)response.StatusCode} - device is reachable");
            }

            return (false, $"HTTP {(int)response.StatusCode} from {context.Host}");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Connection timed out");
        }
        catch (Exception ex)
        {
            return (false, $"Unexpected error: {ex.Message}");
        }
    }
}

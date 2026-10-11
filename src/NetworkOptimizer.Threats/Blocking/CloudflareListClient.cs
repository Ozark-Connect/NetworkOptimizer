using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>
/// Keeps an account-level Cloudflare IP list that a custom rule blocks on
/// (<c>ip.src in $netopt_blocklist</c>). The token needs Account Filter Lists Edit.
/// </summary>
public sealed class CloudflareListClient
{
    /// <summary>The list Network Optimizer owns; rules reference it by this name.</summary>
    public const string ListName = "netopt_blocklist";

    public const string RuleExpression = "ip.src in $" + ListName;

    private const string ApiBase = "https://api.cloudflare.com/client/v4";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public CloudflareListClient(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Finds the list by id, then by name, and creates it when neither exists.</summary>
    public async Task<CloudflareList> EnsureListAsync(string token, string accountId, string? listId, CancellationToken ct)
    {
        var lists = await SendAsync<List<CloudflareList>>(HttpMethod.Get, token, $"/accounts/{accountId}/rules/lists", null, ct);
        var existing = lists.FirstOrDefault(l => !string.IsNullOrEmpty(listId) && l.Id == listId)
                       ?? lists.FirstOrDefault(l => l.Name == ListName);
        if (existing != null)
        {
            if (existing.Kind != "ip")
                throw new CloudflareException($"A Cloudflare list named {ListName} exists but does not hold IP addresses.");
            return existing;
        }
        return await SendAsync<CloudflareList>(HttpMethod.Post, token, $"/accounts/{accountId}/rules/lists",
            JsonContent.Create(new
            {
                name = ListName,
                kind = "ip",
                description = "Sources blocked by Network Optimizer. Managed automatically; edits are overwritten."
            }), ct);
    }

    /// <summary>Reads the list, including how many rules reference it.</summary>
    public Task<CloudflareList> GetListAsync(string token, string accountId, string listId, CancellationToken ct) =>
        SendAsync<CloudflareList>(HttpMethod.Get, token, $"/accounts/{accountId}/rules/lists/{listId}", null, ct);

    /// <summary>Replaces every item in the list and waits for Cloudflare to finish.</summary>
    public async Task ReplaceItemsAsync(string token, string accountId, string listId,
        IReadOnlyList<CloudflareListItem> items, CancellationToken ct)
    {
        var op = await SendAsync<CloudflareOperationRef>(HttpMethod.Put, token,
            $"/accounts/{accountId}/rules/lists/{listId}/items", JsonContent.Create(items), ct);

        var deadline = DateTime.UtcNow + OperationTimeout;
        while (true)
        {
            var status = await SendAsync<CloudflareOperation>(HttpMethod.Get, token,
                $"/accounts/{accountId}/rules/lists/bulk_operations/{op.OperationId}", null, ct);
            if (status.Status == "completed") return;
            if (status.Status == "failed")
                throw new CloudflareException($"Cloudflare could not update the list: {status.Error ?? "no reason given"}.");
            if (DateTime.UtcNow > deadline)
                throw new CloudflareException("Cloudflare is still updating the list; the next sync checks again.");
            await _delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string token, string path, HttpContent? content, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(method, ApiBase + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudflareException($"No answer from Cloudflare within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new CloudflareException($"Could not reach Cloudflare: {ex.Message}");
        }

        using (response)
        {
            CloudflareEnvelope<T>? body = null;
            try
            {
                body = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<T>>(cts.Token);
            }
            catch (System.Text.Json.JsonException)
            {
                // Fall through to the status-based message.
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CloudflareException("Cloudflare rejected the token. It needs Account Filter Lists Edit on this account.");
            if (body is { Success: true, Result: not null })
                return body.Result;

            var reason = body?.Errors.FirstOrDefault()?.Message;
            throw new CloudflareException(reason != null
                ? $"Cloudflare: {reason}"
                : $"Cloudflare answered HTTP {(int)response.StatusCode}.");
        }
    }
}

/// <summary>Cloudflare list sync as a block target, with the site's own account and token.</summary>
public sealed class CloudflareBlockTarget(CloudflareListClient client, IThreatSettingsAccessor settings, string? site) : IBlockTarget
{
    public BlockTargetKind Kind => BlockTargetKind.Cloudflare;
    public bool SyncEveryCycle => false;

    public async Task<BlockTargetResult> SyncAsync(IReadOnlyList<ThreatBlock> desired, CancellationToken cancellationToken)
    {
        var token = await settings.GetDecryptedSettingAsync(BlockingSettingKeys.ForSite(BlockingSettingKeys.CloudflareToken, site), cancellationToken);
        var account = await settings.GetSettingAsync(BlockingSettingKeys.ForSite(BlockingSettingKeys.CloudflareAccountId, site), cancellationToken);
        var listId = await settings.GetSettingAsync(BlockingSettingKeys.ForSite(BlockingSettingKeys.CloudflareListId, site), cancellationToken);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(listId))
            return BlockTargetResult.Failure("Set up Cloudflare in Automatic Blocking first.");
        try
        {
            var items = desired.Select(b => new CloudflareListItem(b.SourceIp,
                $"Network Optimizer: {b.Reason}, until {b.ExpiresAt:yyyy-MM-dd HH:mm} UTC")).ToList();
            await client.ReplaceItemsAsync(token, account, listId, items, cancellationToken);
            return BlockTargetResult.Success(items.Count);
        }
        catch (CloudflareException ex)
        {
            return BlockTargetResult.Failure(ex.Message);
        }
    }
}

public sealed class CloudflareList
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("num_items")] public int NumItems { get; set; }
    /// <summary>Rules that use the list; 0 means nothing blocks on it yet.</summary>
    [JsonPropertyName("num_referencing_filters")] public int NumReferencingFilters { get; set; }
}

public sealed record CloudflareListItem(
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("comment")] string Comment);

internal sealed class CloudflareEnvelope<T>
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("errors")] public List<CloudflareError> Errors { get; set; } = [];
    [JsonPropertyName("result")] public T? Result { get; set; }
}

internal sealed class CloudflareError
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

internal sealed class CloudflareOperationRef
{
    [JsonPropertyName("operation_id")] public string OperationId { get; set; } = "";
}

internal sealed class CloudflareOperation
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>A Cloudflare API failure with a message fit to show the user.</summary>
public sealed class CloudflareException(string message) : Exception(message);

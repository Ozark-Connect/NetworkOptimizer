using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Generic wrapper for UniFi API responses
/// UniFi returns most data in this format: { "meta": {...}, "data": [...] }
/// </summary>
public class UniFiApiResponse<T>
{
    [JsonPropertyName("meta")]
    public UniFiMeta Meta { get; set; } = new();

    [JsonPropertyName("data")]
    public List<T> Data { get; set; } = new();
}

public class UniFiMeta
{
    [JsonPropertyName("rc")]
    public string Rc { get; set; } = string.Empty; // "ok" for success

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }
}

/// <summary>
/// Login request payload
/// </summary>
public class UniFiLoginRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("remember")]
    public bool Remember { get; set; } = false;

    [JsonPropertyName("strict")]
    public bool Strict { get; set; } = true;
}

/// <summary>
/// Login response
/// </summary>
public class UniFiLoginResponse
{
    [JsonPropertyName("meta")]
    public UniFiMeta Meta { get; set; } = new();

    [JsonPropertyName("data")]
    public List<object> Data { get; set; } = new();

    [JsonPropertyName("unique_id")]
    public string? UniqueId { get; set; }
}

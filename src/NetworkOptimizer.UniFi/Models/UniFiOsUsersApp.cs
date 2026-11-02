using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Envelope of the UniFi OS Users app (/proxy/users/api/v2/...): { code, codeS, msg, data }.
/// </summary>
public class UniFiOsUsersAppResponse<T>
{
    /// <summary>1 on success.</summary>
    [JsonPropertyName("code")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Code { get; set; }

    /// <summary>"SUCCESS" on success.</summary>
    [JsonPropertyName("codeS")]
    public string? CodeS { get; set; }

    /// <summary>The returned records.</summary>
    [JsonPropertyName("data")]
    public List<T>? Data { get; set; }

    /// <summary>Total record count on paged lists (users); absent on unpaged ones (keys).</summary>
    [JsonPropertyName("total")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Total { get; set; }
}

/// <summary>
/// A console account from GET /proxy/users/api/v2/users (UniFi OS Admins &amp; Users).
/// </summary>
public class UniFiOsUser
{
    [JsonPropertyName("unique_id")]
    public string? UniqueId { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>"ACTIVE" for an enabled account.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>True for an account that exists only on this console (no UI.com / SSO identity).</summary>
    [JsonPropertyName("only_local_account")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool OnlyLocalAccount { get; set; }

    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("create_time")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long CreateTime { get; set; }

    /// <summary>Unix seconds of the last sign-in; 0 when the account never signed in.</summary>
    [JsonPropertyName("login_time")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long LoginTime { get; set; }

    /// <summary>Unix seconds; 0 on every account observed so far, so <see cref="LoginTime"/> is the signal.</summary>
    [JsonPropertyName("last_activity_time")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long LastActivityTime { get; set; }

    /// <summary>Admin roles. Empty for a user who is not a console admin.</summary>
    [JsonPropertyName("roles")]
    public List<UniFiOsRole>? Roles { get; set; }

    /// <summary>The name to show: full name, then username.</summary>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(FullName) ? FullName : !string.IsNullOrWhiteSpace(Username) ? Username : UniqueId ?? "Unknown";
}

/// <summary>
/// A role assigned to a console account.
/// </summary>
public class UniFiOsRole
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>"owner", "super_administrator", or "custom_administrator_{id}".</summary>
    [JsonPropertyName("system_key")]
    public string? SystemKey { get; set; }
}

/// <summary>
/// A console API key from GET /proxy/users/api/v2/keys.
/// </summary>
public class UniFiOsApiKey
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("created_at")]
    [JsonConverter(typeof(FlexibleDateTimeOffsetConverter))]
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>Null when the key was never used.</summary>
    [JsonPropertyName("last_used_at")]
    [JsonConverter(typeof(FlexibleDateTimeOffsetConverter))]
    public DateTimeOffset? LastUsedAt { get; set; }

    [JsonPropertyName("creator_user_id")]
    public string? CreatorUserId { get; set; }
}

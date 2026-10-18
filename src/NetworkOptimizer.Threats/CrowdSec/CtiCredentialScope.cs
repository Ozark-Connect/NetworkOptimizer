using NetworkOptimizer.Threats.Interfaces;

namespace NetworkOptimizer.Threats.CrowdSec;

/// <summary>The instance-wide CrowdSec CTI settings, in the main database.</summary>
public static class CrowdSecSettingKeys
{
    public const string Enabled = "crowdsec.enabled";
    /// <summary>The CTI API key, encrypted.</summary>
    public const string ApiKey = "crowdsec.api_key";
    public const string DailyQuota = "crowdsec.daily_quota";
    /// <summary>The CrowdSec free tier's daily lookups, used when no quota is saved.</summary>
    public const int DefaultDailyQuota = 30;
}

/// <summary>A CTI API key and the daily lookups its plan allows.</summary>
public sealed record CtiCredential(string ApiKey, int DailyQuota);

/// <summary>
/// The CrowdSec CTI key a site looks up with. The public build has one key for the instance; a
/// multi-tenant host gives each tenant its own by wrapping this interface. The answers go to the
/// shared <see cref="ICtiReputationCache"/> whichever key paid for them.
/// </summary>
public interface ICtiCredentialScope
{
    /// <summary>The key and quota for <paramref name="site"/> (null is the default site), or null when CTI has no key.</summary>
    Task<CtiCredential?> GetAsync(string? site, CancellationToken cancellationToken = default);
}

/// <summary>One key and quota for every site on the instance.</summary>
public sealed class InstanceCtiCredentialScope(IThreatSettingsAccessor settings) : ICtiCredentialScope
{
    public async Task<CtiCredential?> GetAsync(string? site, CancellationToken cancellationToken = default)
    {
        var key = await settings.GetDecryptedSettingAsync(CrowdSecSettingKeys.ApiKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(key)) return null;
        var quota = int.TryParse(await settings.GetSettingAsync(CrowdSecSettingKeys.DailyQuota, cancellationToken), out var q) && q >= 1
            ? q : CrowdSecSettingKeys.DefaultDailyQuota;
        return new CtiCredential(key, quota);
    }
}

using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>
/// Purchase and billing links for the licensing UI, and the rules for when each shows.
/// </summary>
public static class LicenseBilling
{
    /// <summary>The public licensing and pricing page, where a license is bought.</summary>
    public const string LicensingUrl = "https://ozarkconnect.net/network-optimizer/licensing";

    /// <summary>The billing page: emails a link to the subscription's billing portal.</summary>
    public const string BillingUrl = "https://ozarkconnect.net/network-optimizer/billing";

    /// <summary>
    /// True when any Active key is a Term (subscription) key, paid up or lapsed: a lapsed
    /// subscriber renews through billing. A perpetual key has no subscription to manage.
    /// </summary>
    public static bool HasSubscriptionKey(IEnumerable<LicenseKeyRecord> keys) =>
        keys.Any(k => k.Status == LicenseKeyStatuses.Active && k.Model == LicenseKeyModels.Term);

    /// <summary>
    /// True when a site is in grace because its covering Term key is past its paid-through date.
    /// An uncovered site's grace has no subscription behind it.
    /// </summary>
    public static bool AnySubscriptionInGrace(LicenseSnapshot? snapshot) =>
        snapshot?.States.Values.Any(s => s.State == SiteLicenseState.Grace && s.Reason == LicenseRestrictionReason.KeyExpired) ?? false;

    /// <summary>
    /// True when every site in grace is there because its Term key is past its paid-through date.
    /// Only then does the past-due wording of <see cref="GraceSentence"/> describe all of them.
    /// </summary>
    public static bool AllGraceIsSubscription(LicenseSnapshot? snapshot)
    {
        var grace = snapshot?.States.Values.Where(s => s.State == SiteLicenseState.Grace).ToList();
        return grace is { Count: > 0 } && grace.All(s => s.Reason == LicenseRestrictionReason.KeyExpired);
    }

    /// <summary>
    /// The grace sentence when a site lost coverage rather than payment (removed key, too few slots).
    /// </summary>
    public static string CoverageGraceSentence(int siteCount, int days)
    {
        var sites = siteCount == 1 ? "1 site loses" : $"{siteCount} sites lose";
        var dayText = days == 1 ? "1 day" : $"{days} days";
        return $"{sites} operations in {dayText} - renew or reassign in Settings > Application > Licensing.";
    }

    /// <summary>The License attention banner's grace sentence, with singular forms for one site and one day.</summary>
    public static string GraceSentence(int siteCount, int days)
    {
        var one = siteCount == 1;
        var dayText = days == 1 ? "1 day" : $"{days} days";
        var siteText = one ? "1 Site because its" : $"{siteCount} Sites because their";
        var them = one ? "it" : "them";
        return $"In {dayText}, operations stop on {siteText} license is past due. " +
               $"Pay the open invoice or renew the subscription to keep {them} running, " +
               $"or move {them} to another license key in Settings > Application > Licensing.";
    }
}

using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.CableModemProviders.Uci;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// The configuration problem, if any, a cable modem's monitoring cards should show without being
/// asked. Only failures a user can fix from what the card says qualify, and only while polls are
/// actually failing, so a working modem is never flagged.
/// </summary>
public static class CmConfigIssues
{
    /// <summary>Shown when a provider that needs a password has none and its polls fail.</summary>
    public const string MissingPassword = "No password is set. Add it in Settings.";

    /// <summary>The issue to show for <paramref name="config"/>, or null when there is none.</summary>
    public static string? Describe(CmConfiguration config, ICableModemProvider? provider)
    {
        if (!config.Enabled || string.IsNullOrEmpty(config.LastError))
            return null;

        if (provider?.Credentials == CmCredentialRequirement.Required && string.IsNullOrEmpty(config.Password))
            return MissingPassword;

        // A UCI's failures are all setup (agent missing or too old, no key, no informs), and its
        // reason already says what to do.
        if (config.Provider == UciInformService.ProviderKey)
            return config.LastError;

        return null;
    }
}

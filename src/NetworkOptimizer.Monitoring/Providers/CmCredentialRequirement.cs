namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Whether a cable modem provider needs a password to read the modem. Drives which credential
/// fields Settings shows and what a save requires. The username is never required: it saves as
/// typed, and <see cref="ICableModemProvider.BlankUsernameMeansAdmin"/> says what a blank one does.
/// </summary>
public enum CmCredentialRequirement
{
    /// <summary>The provider reads no credentials at all; Settings hides the fields.</summary>
    None,

    /// <summary>Some models in the provider's family need a login and some do not.</summary>
    Optional,

    /// <summary>The provider cannot read the modem without a password.</summary>
    Required,
}

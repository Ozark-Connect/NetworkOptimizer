namespace NetworkOptimizer.Threats.Blocking;

/// <summary>A preset of public block lists for one kind of site.</summary>
public enum BlockingProfile
{
    /// <summary>Nothing exposed: the gateway already drops unsolicited inbound, so the value is outbound.</summary>
    Home,

    /// <summary>Port forwards or a reverse proxy: attacker and scanner lists protect what is exposed.</summary>
    SelfHosting,

    /// <summary>Commercial use: only lists whose terms allow it.</summary>
    Business,

    /// <summary>Exposed services used only by known people: Self-Hosting plus Tor exits.</summary>
    Lockdown,
}

/// <summary>
/// The public block lists each <see cref="BlockingProfile"/> turns on. Every profile blocks its
/// command-server lists outbound. A token list is included only when the site has its token.
/// </summary>
public static class BlockingProfiles
{
    public static readonly IReadOnlyList<BlockingProfile> All =
        [BlockingProfile.Home, BlockingProfile.SelfHosting, BlockingProfile.Business, BlockingProfile.Lockdown];

    public static string Name(BlockingProfile profile) => profile switch
    {
        BlockingProfile.Home => "Home",
        BlockingProfile.SelfHosting => "Self-Hosting",
        BlockingProfile.Business => "Business",
        _ => "Lockdown",
    };

    public static string Description(BlockingProfile profile) => profile switch
    {
        BlockingProfile.Home => "Nothing exposed to the internet: stops infected devices reaching their command servers.",
        BlockingProfile.SelfHosting => "Port forwards or a reverse proxy: adds attacker and scanner lists in front of what you expose.",
        BlockingProfile.Business => "Commercial use: only lists whose terms allow it.",
        _ => "Exposed services used only by people you know: Self-Hosting plus Tor exit relays.",
    };

    /// <summary>The profile's lists, in the order the Settings card shows them.</summary>
    /// <param name="hasToken">Whether the site has the token for a list that needs one.</param>
    public static IReadOnlyList<string> Lists(BlockingProfile profile, Func<string, bool> hasToken)
    {
        // abuse.ch's terms are not-for-profit; Q-Feeds Community allows internal business use.
        var ids = profile switch
        {
            BlockingProfile.Home => new[] { PublicBlockLists.SpamhausDrop, PublicBlockLists.ThreatFox, PublicBlockLists.QFeeds },
            BlockingProfile.Business => [PublicBlockLists.SpamhausDrop, PublicBlockLists.QFeeds, PublicBlockLists.DShield],
            BlockingProfile.SelfHosting =>
            [
                PublicBlockLists.SpamhausDrop, PublicBlockLists.ThreatFox, PublicBlockLists.QFeeds, PublicBlockLists.CinsArmy,
                PublicBlockLists.IpsumLevel3, PublicBlockLists.BlocklistDeStrong, PublicBlockLists.DShield,
            ],
            _ =>
            [
                PublicBlockLists.SpamhausDrop, PublicBlockLists.ThreatFox, PublicBlockLists.QFeeds, PublicBlockLists.CinsArmy,
                PublicBlockLists.IpsumLevel3, PublicBlockLists.BlocklistDeStrong, PublicBlockLists.DShield, PublicBlockLists.TorExits,
            ],
        };
        return PublicBlockLists.All
            .Where(l => ids.Contains(l.Id) && (!l.NeedsToken || hasToken(l.Id)))
            .Select(l => l.Id).ToList();
    }

    /// <summary>
    /// The profile for a site: Self-Hosting when it exposes anything (port forwards, or a WAF in front of
    /// its own services), Home when it exposes nothing. Null forwards means they could not be read.
    /// </summary>
    public static (BlockingProfile Profile, string Reason) Recommend(int? portForwards, bool wafConnected)
    {
        if (portForwards is null)
            return (BlockingProfile.SelfHosting, "its port forwards could not be read");
        if (portForwards > 0)
            return (BlockingProfile.SelfHosting, portForwards == 1 ? "1 port forward" : $"{portForwards} port forwards");
        if (wafConnected)
            return (BlockingProfile.SelfHosting, "a Web Application Firewall in front of its services");
        return (BlockingProfile.Home, "no port forwards");
    }
}

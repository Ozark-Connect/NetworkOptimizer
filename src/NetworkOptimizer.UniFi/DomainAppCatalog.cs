namespace NetworkOptimizer.UniFi;

/// <summary>
/// Names the application behind a domain, for flows the gateway's DPI has not named (the console
/// writes a flow's record only once it ends, so a long-running flow has none while it runs). Matched
/// on the domain or any parent of it. Names are UniFi Network's own DPI names wherever it has the
/// application, so <see cref="DpiCatalog.IconsForName"/> finds the same icons.
/// </summary>
public static class DomainAppCatalog
{
    private static readonly Dictionary<string, string> ByDomain = Build(new()
    {
        ["YouTube"] = ["youtube.com", "googlevideo.com", "ytimg.com", "youtu.be", "youtube-nocookie.com", "youtubei.googleapis.com"],
        ["Netflix"] = ["netflix.com", "netflix.net", "nflxvideo.net", "nflximg.net", "nflxso.net", "nflxext.com"],
        ["Disney+"] = ["disneyplus.com", "disney-plus.net", "dssott.com", "bamgrid.com", "dssedge.com"],
        ["Hulu"] = ["hulu.com", "hulustream.com", "huluim.com"],
        ["Amazon Video"] = ["primevideo.com", "aiv-cdn.net", "aiv-delivery.net", "amazonvideo.com", "pv-cdn.net"],
        ["HBO Max"] = ["max.com", "hbomax.com", "hbomaxcdn.com", "hbo.com"],
        ["Twitch"] = ["twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net", "twitchsvc.net"],
        ["Crunchyroll"] = ["crunchyroll.com", "vrv.co"],
        ["ESPN"] = ["espn.com", "espncdn.com", "espn.net"],
        ["Plex.tv"] = ["plex.tv", "plex.direct", "plex.bz"],
        ["Roku"] = ["roku.com", "rokutime.com"],
        ["Vimeo"] = ["vimeo.com", "vimeocdn.com"],
        ["Spotify"] = ["spotify.com", "scdn.co", "spotifycdn.com", "spotifycdn.net", "audio-ak-spotify-com.akamaized.net"],
        ["Apple Music"] = ["music.apple.com", "itunes.apple.com"],
        ["Pandora"] = ["pandora.com", "p-cdn.us", "p-cdn.com"],
        ["SoundCloud"] = ["soundcloud.com", "sndcdn.com"],
        ["Apple TV+"] = ["tv.apple.com", "hls.itunes.apple.com"],
        ["iCloud"] = ["icloud.com", "icloud-content.com", "apple-cloudkit.com", "icloud.apple.com", "me.com", "mac.com", "icloud.com.cn"],
        ["iTunes/App Store"] = ["mzstatic.com", "apps.apple.com"],
        ["FaceTime"] = ["facetime.apple.com"],
        // Everything else Apple runs: its own domains (the specific apps above win), its CDNs, and APNs.
        ["Apple"] = ["apple.com", "aaplimg.com", "cdn-apple.com", "apple-dns.net", "apple-dns.cn", "itunes.com", "apple.news",
            "apple-mapkit.com", "push-apple.com.akadns.net", "safebrowsing.apple", "apple.com.cn"],
        ["Valve Steam"] = ["steampowered.com", "steamcontent.com", "steamserver.net", "steamstatic.com", "steamcommunity.com", "steam-chat.com"],
        ["Xbox"] = ["xboxlive.com", "xbox.com", "xboxservices.com"],
        ["Sony PlayStation"] = ["playstation.net", "playstation.com", "sonyentertainmentnetwork.com"],
        ["Epic Games"] = ["epicgames.com", "epicgames.dev", "unrealengine.com"],
        ["Fortnite"] = ["fortnite.com"],
        ["Battle.net"] = ["battle.net", "blizzard.com", "blzstatic.cn"],
        ["Minecraft"] = ["minecraft.net", "mojang.com"],
        ["Roblox Game"] = ["roblox.com", "rbxcdn.com"],
        ["Windows Update"] = ["windowsupdate.com", "update.microsoft.com", "delivery.mp.microsoft.com", "dl.delivery.mp.microsoft.com"],
        ["Microsoft Teams"] = ["teams.microsoft.com", "teams.live.com", "skype.com"],
        ["Microsoft Office 365"] = ["office.com", "office.net", "office365.com", "sharepoint.com", "microsoftonline.com"],
        ["Outlook.com"] = ["outlook.com", "outlook.live.com"],
        ["OneDrive"] = ["onedrive.com", "onedrive.live.com", "1drv.com", "1drv.ms"],
        ["Zoom"] = ["zoom.us", "zoom.com", "zoomgov.com"],
        ["Webex"] = ["webex.com"],
        ["Slack"] = ["slack.com", "slack-edge.com", "slack-msgs.com"],
        ["Dropbox"] = ["dropbox.com", "dropboxusercontent.com", "dropboxapi.com"],
        ["Backblaze"] = ["backblaze.com", "backblazeb2.com"],
        ["Google Drive"] = ["drive.google.com", "docs.google.com", "drive.usercontent.google.com"],
        ["Gmail"] = ["mail.google.com", "gmail.com"],
        ["Google Play"] = ["play.google.com", "play.googleapis.com"],
        ["Facebook"] = ["facebook.com", "fbcdn.net", "facebook.net", "fb.com"],
        ["Facebook Messenger"] = ["messenger.com"],
        ["Instagram"] = ["instagram.com", "cdninstagram.com"],
        ["WhatsApp"] = ["whatsapp.com", "whatsapp.net"],
        ["TikTok"] = ["tiktok.com", "tiktokcdn.com", "tiktokv.com", "tiktokcdn-us.com", "byteoversea.com", "ibytedtos.com"],
        ["Snapchat"] = ["snapchat.com", "sc-cdn.net", "snap-dev.net", "snapkit.com"],
        ["Reddit"] = ["reddit.com", "redd.it", "redditmedia.com", "redditstatic.com"],
        ["X"] = ["x.com", "twitter.com", "twimg.com", "t.co"],
        ["Pinterest"] = ["pinterest.com", "pinimg.com"],
        ["LinkedIn"] = ["linkedin.com", "licdn.com"],
        ["Discord"] = ["discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media"],
        ["Telegram"] = ["telegram.org", "t.me"],
        ["GitHub"] = ["github.com", "githubusercontent.com", "githubassets.com"],
        ["ChatGPT"] = ["chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com"],
        ["Claude"] = ["claude.ai", "claude.com", "anthropic.com", "claudeusercontent.com"],
        ["Speedtest.net"] = ["speedtest.net", "ookla.com"],
        ["LastPass"] = ["lastpass.com"],
        ["Waze"] = ["waze.com"],
        ["Ring Always Home"] = ["ring.com"],
    });

    private static Dictionary<string, string> Build(Dictionary<string, string[]> byApp)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (app, domains) in byApp)
        {
            foreach (var d in domains) map[d] = app;
        }
        return map;
    }

    /// <summary>
    /// Networks whose every address belongs to one app, by the start of the owner's name as GeoLite2
    /// spells it ("APPLE-ENGINEERING", "Apple Inc."), for a flow with no domain to go on.
    /// </summary>
    private static readonly (string Owner, string App)[] AppByOwner =
    [
        ("Apple", "Apple"),
        ("Backblaze", "Backblaze"),
    ];

    /// <summary>The app a remote network's owner names, matched on a whole word; null when none.</summary>
    public static string? OwnerApp(string? owner) =>
        owner == null ? null
        : AppByOwner.FirstOrDefault(o => owner.StartsWith(o.Owner, StringComparison.OrdinalIgnoreCase)
            && (owner.Length == o.Owner.Length || !char.IsLetterOrDigit(owner[o.Owner.Length]))).App;

    /// <summary>
    /// Content owners that place caches inside ISPs' networks: their hostname suffixes, the ASNs and
    /// organization word they announce their own addresses from, and (Netflix) the hostname mark an
    /// embedded node carries.
    /// </summary>
    private static readonly (string Name, string[] Suffixes, string[] Apps, int[] OwnerAsns, string OwnerWord, string? EmbeddedMark)[] CdnCaches =
    [
        ("Google Global Cache", ["googlevideo.com"], ["YouTube", "YouTube Kids"], [15169, 36040, 43515], "Google", null),
        ("Netflix Open Connect", ["oca.nflxvideo.net"], ["Netflix"], [2906], "Netflix", "-isp"),
        ("Meta FNA", ["fna.fbcdn.net"], ["Facebook", "Instagram", "WhatsApp", "Facebook Messenger"], [32934], "Facebook", null),
        // Akamai serves every kind of app, so only its hostnames say a flow is Akamai's.
        ("Akamai", ["akamaized.net", "akamaihd.net"], [], [20940, 16625, 21342], "Akamai", null),
    ];

    /// <summary>
    /// The cache a flow reaches inside the ISP's network: its hostname, or UniFi Network's DPI name for it,
    /// belongs to a content owner, but its address originates from someone else's ASN (an embedded Google
    /// Global Cache announces from the ISP's). The DPI name covers a cache node the client resolved out
    /// of view. Null for the owner's own network, an unknown ASN, or a flow no owner claims. The owner's
    /// name in the address's organization also counts as its own: no ASN list is complete.
    /// </summary>
    public static string? EmbeddedCache(string? host, int? asn, string? asnOrg, string? app = null)
    {
        var name = host?.TrimEnd('.');
        foreach (var (cache, suffixes, apps, ownerAsns, ownerWord, mark) in CdnCaches)
        {
            var byHost = !string.IsNullOrEmpty(name) && suffixes.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));
            var byApp = app != null && apps.Contains(app, StringComparer.OrdinalIgnoreCase);
            if (!byHost && !byApp) continue;
            // Netflix names an embedded node itself; that needs no ASN.
            if (byHost && mark != null && name!.Contains(mark, StringComparison.OrdinalIgnoreCase)) return cache;
            if (asn is not > 0 || ownerAsns.Contains(asn.Value)) return null;
            if (asnOrg != null && asnOrg.Contains(ownerWord, StringComparison.OrdinalIgnoreCase)) return null;
            return cache;
        }
        return null;
    }

    /// <summary>
    /// The app a cache-owner's hostname or DPI name says an address serves ("YouTube" for a
    /// googlevideo.com node), whatever network the address is in; null for anything else. Akamai serves
    /// every kind of app, so it never names one.
    /// </summary>
    public static string? CacheApp(string? host, string? app)
    {
        var name = host?.TrimEnd('.');
        foreach (var (_, suffixes, apps, _, _, _) in CdnCaches)
        {
            if (apps.Length == 0) continue;
            if (app != null && apps.Contains(app, StringComparer.OrdinalIgnoreCase)) return app;
            if (!string.IsNullOrEmpty(name) && suffixes.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("." + s, StringComparison.OrdinalIgnoreCase)))
                return AppFor(name) is { } named && apps.Contains(named, StringComparer.OrdinalIgnoreCase) ? named : apps[0];
        }
        return null;
    }

    /// <summary>Mobile carriers by the start of their network owner's name, as GeoLite2 spells it.</summary>
    private static readonly string[] MobileCarriers =
    [
        "AT&T", "T-Mobile", "Verizon", "Sprint", "US Cellular", "UScellular", "Cellco", "Rogers", "Bell Mobility", "TELUS",
        "Vodafone", "EE Limited", "Telefonica", "O2", "Orange", "Deutsche Telekom", "Three", "Hutchison", "Telstra", "Optus",
    ];

    /// <summary>
    /// Wi-Fi Calling: a phone's IPsec tunnel (IKE on UDP 500, NAT-T on 4500) to its carrier's gateway. Named
    /// only when the far end belongs to a mobile carrier; IPsec to anyone else stays a VPN.
    /// </summary>
    public static string? WifiCalling(string proto, int remotePort, string? owner) =>
        proto == "udp" && remotePort is 500 or 4500 && owner != null
            && MobileCarriers.Any(c => owner.StartsWith(c, StringComparison.OrdinalIgnoreCase)
                && (owner.Length == c.Length || !char.IsLetterOrDigit(owner[c.Length])))
            ? "Wi-Fi Calling"
            : null;

    /// <summary>
    /// Local protocols known by their port, named only between your own devices: on the internet the
    /// same ports mean something else (8009 is also Apache AJP).
    /// </summary>
    public static string? LanApp(string proto, int remotePort, string? remoteVendor = null) => LanDeviceApp(proto, remotePort, remoteVendor) ?? (proto, remotePort) switch
    {
        ("tcp", 8009) => "Google Cast",   // a Cast device's control channel
        ("tcp", 6444) => "Midea",         // Midea appliances' local LAN protocol (Home Assistant's Midea LAN)
        ("tcp", 8899) => "AP Agent",      // Network Optimizer polling its AP Agent (ApAgentPaths.AgentPort)
        _ => null,
    };

    /// <summary>
    /// A device's own protocol, told by its port and its MAC vendor together. Specific enough to beat
    /// UniFi Network's DPI, which calls a DIRIGERA hub's HomeKit session "Web File Transfer".
    /// </summary>
    public static string? LanDeviceApp(string proto, int remotePort, string? remoteVendor) => (proto, remotePort) switch
    {
        // LG webOS's control channel (SSAP websocket; 3001 is TLS). Dev servers use these ports too
        // (Rails, Next.js, BrowserSync), so only to a device whose MAC vendor is LG.
        ("tcp", 3000 or 3001) when remoteVendor != null && remoteVendor.StartsWith("LG", StringComparison.OrdinalIgnoreCase) => "LG webOS TV",
        // An IKEA DIRIGERA hub advertises its HomeKit (HAP) server on 8000: a home hub's session to it.
        // 8000 is also every dev server and Hikvision's SDK port, so only to an IKEA device.
        ("tcp", 8000) when remoteVendor != null && remoteVendor.StartsWith("IKEA", StringComparison.OrdinalIgnoreCase) => "HomeKit",
        _ => null,
    };

    /// <summary>
    /// ICMP (v4 or v6) is ICMP whoever answers it: a ping to Cloudflare is not the Cloudflare app.
    /// Null for any other protocol.
    /// </summary>
    public static string? ProtocolApp(string? proto) =>
        proto != null && proto.StartsWith("icmp", StringComparison.OrdinalIgnoreCase) ? "ICMP" : null;

    /// <summary>
    /// An Ookla Speedtest server: hosted by ISPs on their own domains, so no domain list holds them, but
    /// they run on TCP 8080 under a name whose first label starts with "speed" (speedtest.isp.net, speed1.isp.com).
    /// </summary>
    public static string? SpeedtestApp(string? hostName, string proto, int remotePort)
    {
        if (proto != "tcp" || remotePort != 8080 || string.IsNullOrEmpty(hostName)) return null;
        return hostName.StartsWith("speed", StringComparison.OrdinalIgnoreCase) && hostName.Contains('.') ? "Speedtest.net" : null;
    }

    /// <summary>
    /// Tailscale's direct (WireGuard) path: UDP 41641 on either end, source or destination, whatever the
    /// other port is (a DERP or STUN server, a peer's random port). Ahead of DPI, which reads it as STUN or WireGuard.
    /// </summary>
    public static string? TailscaleApp(string proto, int clientPort, int remotePort) =>
        proto == "udp" && (clientPort == 41641 || remotePort == 41641) ? "Tailscale" : null;

    /// <summary>Tools known by their port anywhere, LAN or internet: iperf3 on 5201, TCP and UDP (its UDP mode).</summary>
    public static string? PortApp(string proto, int port) =>
        port == 5201 && proto is "tcp" or "udp" ? "iperf3" : null;

    /// <summary>The application a domain belongs to, matching the domain or its nearest listed parent; null when none.</summary>
    public static string? AppFor(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        var name = domain.Trim().TrimEnd('.');
        while (true)
        {
            if (ByDomain.TryGetValue(name, out var app)) return app;
            var dot = name.IndexOf('.');
            if (dot < 0 || name.IndexOf('.', dot + 1) < 0) return null;
            name = name[(dot + 1)..];
        }
    }
}

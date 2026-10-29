using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Components.Shared.ChangeControl;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// Renders the Change Control pages with the real components and illustrative data to static HTML,
/// for headless screenshots against the mockups (NO is never run locally). Does nothing unless
/// CC_RENDER_DIR is set.
/// </summary>
public class ChangeControlRenderHarness
{
    private static readonly DateTime Now = DateTime.UtcNow.Date.AddHours(18);

    [Fact]
    public async Task RenderPages()
    {
        var dir = Environment.GetEnvironmentVariable("CC_RENDER_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        foreach (var tab in new[] { "overview", "changes", "snapshots", "edit", "migrate", "settings" })
        {
            var html = await RenderAsync<CcPage>(new Dictionary<string, object?> { ["Tab"] = tab },
                tab == "migrate" ? $"change-control/migrate?task=replace&old={SwOld}&new={SwNew}" : null);
            await File.WriteAllTextAsync(Path.Combine(dir, $"{tab}.html"), Shell(html));
        }
        var detail = await RenderAsync<CcChangeDetail>(new Dictionary<string, object?> { ["Detail"] = Detail() });
        await File.WriteAllTextAsync(Path.Combine(dir, "detail.html"), Shell($"<div class=\"change-control\"><div class=\"card\">{detail}</div></div>"));
        var migrateAp = await RenderAsync<CcPage>(new Dictionary<string, object?> { ["Tab"] = "migrate" }, "change-control/migrate?task=replace&old=" + ApOld + "&new=" + ApNew);
        await File.WriteAllTextAsync(Path.Combine(dir, "migrate-ap.html"), Shell(migrateAp));
        foreach (var task in new[] { "restore", "adopt", "zones" })
            await File.WriteAllTextAsync(Path.Combine(dir, $"migrate-{task}.html"),
                Shell(await RenderAsync<CcPage>(new Dictionary<string, object?> { ["Tab"] = "migrate" }, $"change-control/migrate?task={task}")));
        var plan = await RenderAsync<CcReviewPlan>(new Dictionary<string, object?> { ["PlanId"] = 7 });
        await File.WriteAllTextAsync(Path.Combine(dir, "plan.html"), Shell(plan));
    }

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters, string? path = null) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Fleet().Object);
        services.AddSingleton(Site().Object);
        var templates = new Mock<IConfigTemplateService>();
        templates.Setup(t => t.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new TemplateView { Id = 1, Name = "Branch Standard", Description = "VLANs, guest WLAN, and firewall groups for branch sites", SourceSiteSlug = "main",
                Collections = ["networkconf", "wlanconf", "firewallgroup"], UpdatedAt = DateTime.UtcNow.AddDays(-3) },
            new TemplateView { Id = 2, Name = "Store DNS", SourceSiteSlug = "main", Collections = ["static_dns"], UpdatedAt = DateTime.UtcNow.AddDays(-12) },
        ]);
        services.AddSingleton(templates.Object);
        var site = new SiteContextService(new HttpContextAccessor(), new SiteDatabasePaths(Path.Combine(Path.GetTempPath(), "cc.db")));
        site.OverrideSite("main");
        services.AddSingleton(site);
        services.AddSingleton<NavigationManager>(new FakeNav(path ?? "change-control"));
        services.AddSingleton<IJSRuntime>(Mock.Of<IJSRuntime>());
        services.AddSingleton((SiteSwitchService)RuntimeHelpers.GetUninitializedObject(typeof(SiteSwitchService)));
        services.AddScoped<CcNavigator>();
        services.AddAuthorizationCore(o =>
        {
            foreach (var p in new[] { Policies.SiteViewer, Policies.SiteOperator, Policies.SiteAdmin, Policies.RequireViewer, Policies.RequireAdmin })
                o.AddPolicy(p, b => b.RequireAssertion(_ => true));
        });
        services.AddSingleton<AuthenticationStateProvider>(new FakeAuth());
        await using var sp = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(sp, sp.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment child = b =>
            {
                b.OpenComponent<T>(0);
                b.AddMultipleAttributes(1, parameters!);
                b.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<CascadingAuthenticationState>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["ChildContent"] = child }));
            await root.QuiescenceTask;
            return root.ToHtmlString();
        });
    }

    private sealed class FakeNav : NavigationManager
    {
        public FakeNav(string path) => Initialize("http://localhost/", "http://localhost/" + path);
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class FakeAuth : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Alex")], "test"))));
    }

    private static string Shell(string body)
    {
        var wwwroot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/NetworkOptimizer.Web/wwwroot")).Replace('\\', '/');
        string Nav(string text, bool active = false, bool sub = false) =>
            $"<li class=\"nav-item{(sub ? " nav-sub-item" : "")}\"><a class=\"nav-link{(active ? " active" : "")}\" href=\"#\">{(sub ? "<span class=\"nav-sub-icon\">└</span>" : "<span class=\"nav-icon-img\" style=\"display:inline-block;width:18px;height:18px\"></span>")}<span class=\"nav-text\">{text}</span></a></li>";
        var nav = string.Concat(
            Nav("Dashboard"), Nav("Sites"), Nav("Monitoring"), Nav("Network Tools", sub: true), Nav("Client Performance"),
            Nav("Config Optimizer"), Nav("Performance Tweaks", sub: true), Nav("Change Control", true), Nav("Firmware Rollout", sub: true),
            Nav("Wi-Fi Optimizer"), Nav("Security Audit"), Nav("Threat Intelligence"), Nav("Adaptive SQM"), Nav("Alerts &amp; Schedule"), Nav("Settings"));
        body = body.Replace("src=\"/", "src=\"");
        return $"""
            <!DOCTYPE html><html><head><meta charset="utf-8"><base href="file:///{wwwroot}/">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <link rel="stylesheet" href="css/app.css"></head>
            <body><div class="app-container"><div class="sidebar"><nav class="nav-menu"><ul class="nav-list">{nav}</ul></nav></div>
            <div class="main-content"><main class="page-content"><article class="content">{body}</article></main></div></div></body></html>
            """;
    }

    // ---------- Illustrative data, from the mockups ----------

    private static List<(DateTime, double?)> Wave(DateTime from, DateTime to, double baseValue, double amp, double period, int points = 160, int seed = 1)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, points).Select(i =>
        {
            var t = from + TimeSpan.FromTicks((to - from).Ticks * i / (points - 1));
            return (t, (double?)(baseValue + amp * Math.Sin(i / period) + rnd.NextDouble() * amp * 0.4));
        }).ToList();
    }

    private static readonly FleetSiteView[] Sites =
    [
        new() { Slug = "main", Name = "HQ", Detail = "UCG-Fiber, 22 devices", Online = true, Changes7d = 5, Changes24h = 2, Regressions = 2, Unattributed = 1, DriftObjects = 6, NetworkAppVersion = "11.0.21", LastSnapshotAt = Now.AddHours(-2) },
        new() { Slug = "client-a", Name = "Client A", Detail = "UCG-Ultra, 6 devices", Online = true, Changes7d = 1, Changes24h = 1, Regressions = 1, Unattributed = 1, NetworkAppVersion = "10.6.106", LastSnapshotAt = Now.AddHours(-4) },
        new() { Slug = "branch-2", Name = "Branch 2", Detail = "UDM-Pro, 9 devices", Online = false, AgentOffline = true, Changes7d = 1, DriftObjects = 0, NetworkAppVersion = "11.0.21", LastSnapshotAt = Now.AddHours(-4) },
        new() { Slug = "home-lab", Name = "Home Lab", Detail = "UXG-Fiber, 14 devices", Online = true, DriftObjects = 0, NetworkAppVersion = "10.6.106", LastSnapshotAt = Now.AddHours(-4) },
    ];

    private static List<ChangeRowView> Changes() =>
    [
        new() { Id = 7, SiteSlug = "client-a", SiteName = "Client A", OccurredAt = Now.AddHours(-25), Kind = ConfigChangeKind.Config, Summary = "Port profile Cameras: native network changed", ActorKind = ConfigChangeActorKind.NoAdminRecorded, Who = "Unattributed", RegressionCount = 1, IncidentsAfter = 1, PreviousSnapshotId = 5, SnapshotId = 6 },
        new() { Id = 6, SiteSlug = "main", SiteName = "HQ", OccurredAt = Now.AddHours(-39), Kind = ConfigChangeKind.DeviceFirmware, Summary = "Firmware Rollout: 4 switches, 7.1.26 to 7.1.30", ActorKind = ConfigChangeActorKind.Firmware, Who = "Firmware Rollout" },
        new() { Id = 5, SiteSlug = "main", SiteName = "HQ", OccurredAt = Now.AddHours(-56), Kind = ConfigChangeKind.Config, Summary = "WLAN Home: security WPA2 to WPA2/WPA3", ActorKind = ConfigChangeActorKind.Person, Who = "Alex", Tags = ["WLAN security"], IncidentsAfter = 1, PreviousSnapshotId = 3, SnapshotId = 4 },
        new() { Id = 4, SiteSlug = "branch-2", SiteName = "Branch 2", OccurredAt = Now.AddHours(-88), Kind = ConfigChangeKind.NetworkAppUpgrade, Summary = "Network app 10.6.106 to 11.0.21", ActorKind = ConfigChangeActorKind.Firmware, Who = "Firmware Rollout" },
        new() { Id = 3, SiteSlug = "main", SiteName = "HQ", OccurredAt = Now.AddHours(-136), Kind = ConfigChangeKind.Config, Summary = "14 objects changed during Network app upgrade", ActorKind = ConfigChangeActorKind.LikelyUniFi, Who = "UniFi (likely)", RegressionCount = 2, PreviousSnapshotId = 1, SnapshotId = 2 },
        new() { Id = 2, SiteSlug = "main", SiteName = "HQ", OccurredAt = Now.AddHours(-136.2), Kind = ConfigChangeKind.NetworkAppUpgrade, Summary = "Network app 10.6.106 to 11.0.21", ActorKind = ConfigChangeActorKind.Firmware, Who = "Firmware Rollout" },
        new() { Id = 1, SiteSlug = "main", SiteName = "HQ", OccurredAt = Now.AddHours(-153), Kind = ConfigChangeKind.Config, Summary = "Port profile Trunk-Core: tagged networks changed", ActorKind = ConfigChangeActorKind.Person, Who = "Alex", PreviousSnapshotId = 1, SnapshotId = 1 },
    ];

    private static Mock<IChangeControlFleetService> Fleet()
    {
        var m = new Mock<IChangeControlFleetService>();
        m.Setup(f => f.GetSitesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Sites.ToList());
        m.Setup(f => f.GetChangesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Changes());
        m.Setup(f => f.GetTimelineAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, IReadOnlyCollection<string>? _, CancellationToken _) => Sites.Select((s, i) => new TimelineSiteRow
            {
                Site = s,
                Sparkline = Wave(from, to, 30, 4, 6, seed: i + 1),
                Markers = Changes().Where(c => c.SiteSlug == s.Slug).ToList(),
                Bands = s.Slug == "main" ? [new TimelineBand(Now.AddHours(-56).AddMinutes(15), Now.AddHours(-52), "incident", "8 Wi-Fi clients offline")]
                    : s.Slug == "client-a" ? [new TimelineBand(Now.AddHours(-24.8), Now.AddHours(-20), "incident", "Cameras offline")] : [],
                ChangeCount = Changes().Count(c => c.SiteSlug == s.Slug && c.Kind == ConfigChangeKind.Config),
                RegressionCount = s.Regressions,
            }).ToList());
        m.Setup(f => f.GetLanesAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, DateTime from, DateTime to, CancellationToken _) =>
            [
                new() { Key = "latency", Title = "WAN Latency", Subtitle = "Idle vs loaded, ms", SeriesAName = "idle", SeriesBName = "loaded", SeriesA = Wave(from, to, 12, 2, 5), SeriesB = Wave(from, to, 34, 5, 4, seed: 2), LatestA = "12", LatestB = "35" },
                new() { Key = "loss", Title = "WAN Loss", Subtitle = "Idle vs loaded, %", SeriesAName = "idle", SeriesBName = "loaded", SeriesA = Wave(from, to, 0.01, 0.01, 3), SeriesB = Wave(from, to, 0.05, 0.08, 2, seed: 3), LatestA = "0.0", LatestB = "0.1" },
                new() { Key = "throughput", Title = "WAN Throughput", Subtitle = "Mbps", SeriesAName = "down", SeriesBName = "up", SeriesA = Wave(from, to, 220, 90, 6), SeriesB = Wave(from, to, 60, 15, 6, seed: 4), LatestA = "312", LatestB = "74" },
                new() { Key = "cpu", Title = "Gateway CPU", Subtitle = "%", SeriesAName = "CPU", SeriesA = Wave(from, to, 18, 2, 7), LatestA = "19" },
                new() { Key = "clients", Title = "Clients", Subtitle = "Wi-Fi", SeriesAName = "Wi-Fi", SeriesA = Wave(from, to, 55, 8, 8), LatestA = "57" },
            ]);
        m.Setup(f => f.GetChangeDetailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Detail());
        m.Setup(f => f.GetOverviewAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OverviewView
        {
            Sites = Sites.ToList(),
            Attention =
            [
                new() { Kind = "Regression", SiteSlug = "client-a", SiteName = "Client A", Title = "Cameras reach Internal", Detail = "Port profile Cameras changed Oct 20, 4:42 PM. Unattributed. 1 incident after.", ChangeId = 7 },
                new() { Kind = "Approval", SiteSlug = "main", SiteName = "HQ", Title = "Roll Back HQ to Oct 15, 9:00 AM", Detail = "Submitted by Alex. Scheduled Oct 24, 2:00 AM once approved. Possible lockout.", PlanId = 7 },
                new() { Kind = "Regression", SiteSlug = "main", SiteName = "HQ", Title = "2 regressions from the Network app upgrade", Detail = "14 objects changed during Network app upgrade", ChangeId = 3, FromSnapshotId = 1, ToSnapshotId = 2 },
                new() { Kind = "Gap", SiteSlug = "branch-2", SiteName = "Branch 2", Title = "Agent offline for 2 hours", Detail = "Change detection is paused. Network Optimizer catches up from the UniFi audit log when it reconnects." },
            ],
            Plans =
            [
                new() { Id = 7, SiteSlug = "main", SiteName = "HQ", Name = "Roll Back HQ to Oct 15", Status = ConfigPlanStatus.PendingApproval, Detail = "9 objects, scheduled Oct 24, 2:00 AM" },
                new() { Id = 9, SiteSlug = "home-lab", SiteName = "Home Lab", Name = "Zone setup: Home Lab", Status = ConfigPlanStatus.Watching, Detail = "Commit-confirmed until 6:05 PM", StartedAt = DateTime.UtcNow.AddMinutes(-3), WatchUntil = DateTime.UtcNow.AddMinutes(2) },
            ],
            Upgrades =
            [
                new("branch-2", "Branch 2", Now.AddHours(-88), "10.6.106", "11.0.21", 0, 0, null),
                new("main", "HQ", Now.AddHours(-136), "10.6.106", "11.0.21", 14, 2, 3),
            ],
        });
        return m;
    }

    private static ChangeDetailView Detail() => new()
    {
        Row = Changes()[2],
        Objects =
        [
            new() { Collection = "wlanconf", CollectionName = "WLANs", ObjectId = "w1", Name = "Home", Action = ConfigChangeAction.Updated,
                Fields = [new() { Label = "Security protocol", Old = "WPA2", New = "WPA2/WPA3" }, new() { Label = "PMF", Old = "Disabled", New = "Optional" }] },
        ],
        MadeBy = "Alex", MadeVia = "UniFi web app", MadeFromIp = "203.0.113.24",
        BeforeAfter = [new("Loaded latency", "34", "35 ms", false), new("Loaded loss", "0.0", "0.0%", false), new("Gateway CPU", "18", "19%", false)],
        IncidentsAfter = [new() { Id = 1, Title = "8 Wi-Fi clients offline", StartedAt = Now.AddHours(-56).AddMinutes(15), ResolvedAt = Now.AddHours(-52) }],
        CanRollBack = true,
    };

    // ---------- Replace Device fixtures (mockups 9 and 11) ----------

    private const string SwOld = "aa:bb:cc:00:01:01";
    private const string SwNew = "aa:bb:cc:00:01:02";
    private const string ApOld = "aa:bb:cc:00:02:01";
    private const string ApNew = "aa:bb:cc:00:02:02";

    private static JsonObject Port(int idx, string media, bool poe = false, double watts = 0, bool up = false, bool uplink = false) => new()
    {
        ["port_idx"] = idx,
        ["media"] = media,
        ["port_poe"] = poe,
        ["poe_enable"] = watts > 0,
        ["poe_power"] = watts.ToString("0.00"),
        ["up"] = up,
        ["is_uplink"] = uplink,
    };

    private static JsonObject Dev(string mac, string name, string model, string type, string version, JsonObject meta, JsonObject? extra = null)
    {
        var d = new JsonObject { ["_id"] = mac, ["mac"] = mac, ["name"] = name, ["model"] = model, ["type"] = type, ["version"] = version, [ConfigCollections.DeviceMetaField] = meta };
        foreach (var (k, v) in extra ?? []) d[k] = v?.DeepClone();
        return d;
    }

    private static ConfigState MigrateContext()
    {
        var s = new ConfigState();
        s.Collections["networkconf"] = [new JsonObject { ["_id"] = "n-iot", ["name"] = "IoT" }, new JsonObject { ["_id"] = "n-cam", ["name"] = "Cameras" },
            new JsonObject { ["_id"] = "n-off", ["name"] = "Office" }, new JsonObject { ["_id"] = "n-mgmt", ["name"] = "Management" }];
        s.Collections["portconf"] = [new JsonObject { ["_id"] = "p-trunk", ["name"] = "Trunk-Core" }, new JsonObject { ["_id"] = "p-ap", ["name"] = "AP Trunk" }];
        s.Collections["wlanconf"] = [new JsonObject { ["_id"] = "w1", ["name"] = "Home", ["wlan_bands"] = new JsonArray("2g", "5g", "6g") }];
        s.Collections["device"] = [new JsonObject { ["mac"] = "aa:bb:cc:00:09:01", ["name"] = "Back Yard AP" }, new JsonObject { ["mac"] = "aa:bb:cc:00:09:02", ["name"] = "Front Yard AP", ["mesh_uplink_1"] = ApOld },
            new JsonObject { ["mac"] = "aa:bb:cc:00:09:03", ["name"] = "UNVR" }, new JsonObject { ["mac"] = "aa:bb:cc:00:09:04", ["name"] = "NAS" }];
        return s;
    }

    private static JsonObject OldSwitch()
    {
        var up = new HashSet<int> { 1, 2, 5, 6, 7, 8, 9, 10, 13, 14, 18, 20, 22, 23, 24 };
        var watts = new Dictionary<int, double> { [1] = 21, [2] = 12, [5] = 5, [6] = 5 };
        var ports = Enumerable.Range(1, 24).Select(i => Port(i, "GE", true, watts.GetValueOrDefault(i), up.Contains(i))).Append(Port(25, "SFP", up: true, uplink: true)).Append(Port(26, "SFP"));
        JsonObject O(int i, string? net = null, string? prof = null, bool iso = false) => new() { ["port_idx"] = i, ["native_networkconf_id"] = net, ["portconf_id"] = prof, ["isolation"] = iso };
        var overrides = new JsonArray(O(25, prof: "p-trunk"), O(1, prof: "p-ap"), O(2, prof: "p-ap"), O(5, "n-cam"), O(6, "n-cam"), O(22, "n-cam", iso: true), O(23, "n-off"), O(24, "n-off"));
        foreach (var i in Enumerable.Range(7, 6)) overrides.Add(O(i, "n-iot"));
        foreach (var i in Enumerable.Range(13, 9)) overrides.Add(O(i, "n-off"));
        var meta = new JsonObject
        {
            ["shortname"] = "USW-24-PoE",
            ["type"] = "usw",
            ["total_used_power"] = 47,
            ["total_max_power"] = 250,
            ["state"] = 0,
            ["switch_caps"] = new JsonObject { ["etherlight_caps"] = 0, ["max_aggregate_sessions"] = 8 },
            ["ports"] = new JsonArray(ports.Select(p => (JsonNode?)p).ToArray()),
            ["lldp"] = new JsonArray(
                new JsonObject { ["local_port_idx"] = 1, ["chassis_id"] = "aa:bb:cc:00:09:01" },
                new JsonObject { ["local_port_idx"] = 2, ["chassis_id"] = "aa:bb:cc:00:09:02" },
                new JsonObject { ["local_port_idx"] = 22, ["chassis_id"] = "aa:bb:cc:00:09:03" },
                new JsonObject { ["local_port_idx"] = 23, ["chassis_id"] = "aa:bb:cc:00:09:04" }),
            ["uplink"] = new JsonObject { ["uplink_device_name"] = "Core Switch", ["uplink_remote_port"] = 9 },
        };
        return Dev(SwOld, "Garage Switch", "US24P250", "usw", "7.1.26", meta, new JsonObject
        {
            ["port_overrides"] = overrides,
            ["stp_priority"] = "12288",
            ["jumboframe_enabled"] = true,
            ["mgmt_network_id"] = "n-mgmt",
            ["config_network"] = new JsonObject { ["type"] = "static", ["ip"] = "192.0.2.12" },
            ["link_aggregation_groups"] = new JsonArray(new JsonObject { ["lag_idx"] = 1, ["aggregate_members"] = new JsonArray(23, 24), ["type"] = "lag" }),
        });
    }

    private static JsonObject NewSwitch()
    {
        var ports = Enumerable.Range(1, 12).Select(i => Port(i, "GE", true)).Concat(Enumerable.Range(13, 4).Select(i => Port(i, "2.5GE", true)))
            .Concat([Port(17, "SFP+", up: true, uplink: true), Port(18, "SFP+")]);
        var meta = new JsonObject
        {
            ["shortname"] = "USW-Pro-Max-16-PoE",
            ["type"] = "usw",
            ["total_max_power"] = 180,
            ["state"] = 1,
            ["switch_caps"] = new JsonObject { ["etherlight_caps"] = 655, ["max_aggregate_sessions"] = 8 },
            ["ports"] = new JsonArray(ports.Select(p => (JsonNode?)p).ToArray()),
        };
        return Dev(SwNew, "USW-Pro-Max-16-PoE", "USPM16P", "usw", "7.1.30", meta);
    }

    private static JsonObject OldAp() => Dev(ApOld, "Back Yard AP", "UAPA6A9", "uap", "7.0.66", new JsonObject
    {
        ["shortname"] = "U7-Outdoor",
        ["type"] = "uap",
        ["state"] = 1,
        ["supported_afc_regions"] = new JsonArray("US"),
        ["radios"] = new JsonArray(new JsonObject { ["radio"] = "ng" }, new JsonObject { ["radio"] = "na" }, new JsonObject { ["radio"] = "6e" }),
    }, new JsonObject
    {
        ["outdoor_mode_override"] = "on",
        ["led_override"] = "on",
        ["mesh_uplink_1"] = "",
        ["radio_table"] = new JsonArray(
            new JsonObject { ["radio"] = "ng", ["name"] = "wifi0", ["channel"] = 6, ["ht"] = 20, ["tx_power_mode"] = "medium" },
            new JsonObject { ["radio"] = "na", ["name"] = "wifi1", ["channel"] = 100, ["ht"] = 160, ["tx_power_mode"] = "custom", ["tx_power"] = 26 },
            new JsonObject { ["radio"] = "6e", ["name"] = "wifi2", ["channel"] = 37, ["ht"] = 320, ["tx_power_mode"] = "auto", ["ap_pwr_type"] = "SP" }),
    });

    private static JsonObject NewAp() => Dev(ApNew, "U7 Pro (bench)", "UAPA6A4", "uap", "7.0.66", new JsonObject
    {
        ["shortname"] = "U7-Pro",
        ["type"] = "uap",
        ["state"] = 1,
        ["supported_afc_regions"] = new JsonArray(),
        ["radios"] = new JsonArray(
            new JsonObject { ["radio"] = "ng", ["name"] = "wifi0", ["min_txpower"] = 6, ["max_txpower"] = 22 },
            new JsonObject { ["radio"] = "na", ["name"] = "wifi1", ["has_dfs"] = true, ["has_ht160"] = true, ["min_txpower"] = 6, ["max_txpower"] = 23 },
            new JsonObject { ["radio"] = "6e", ["name"] = "wifi2", ["has_ht160"] = true, ["has_eht320"] = true, ["min_txpower"] = 6, ["max_txpower"] = 23, ["ap_pwr_type"] = "LPI" }),
    });

    private static readonly List<NetworkOptimizer.Audit.Models.NetworkInfo> ZoneNetworks =
    [
        new() { Id = "n1", Name = "Home", VlanId = 1, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.Home },
        new() { Id = "n10", Name = "Work", VlanId = 10, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.Corporate },
        new() { Id = "n15", Name = "Gaming", VlanId = 15, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.Gaming },
        new() { Id = "n20", Name = "Management", VlanId = 20, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.Management },
        new() { Id = "n30", Name = "IoT", VlanId = 30, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.IoT },
        new() { Id = "n31", Name = "IoT", VlanId = 31, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.IoT },
        new() { Id = "n35", Name = "Media", VlanId = 35, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.Media },
        new() { Id = "n40", Name = "Cameras", VlanId = 40, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.Security },
        new() { Id = "n50", Name = "Printers", VlanId = 50, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.Printer },
        new() { Id = "n60", Name = "Servers", VlanId = 60, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.Server },
        new() { Id = "n70", Name = "Guest", VlanId = 70, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.Guest },
        new() { Id = "n80", Name = "Lab", VlanId = 80, Purpose = NetworkOptimizer.Audit.Models.NetworkPurpose.Unknown },
    ];

    private static ConfigState ZoneLive()
    {
        var s = new ConfigState();
        JsonObject Z(string id, string name, string key, params string[] nets) => new() { ["_id"] = id, ["name"] = name, ["zone_key"] = key, ["network_ids"] = new JsonArray(nets.Select(n => (JsonNode?)n).ToArray()) };
        s.Collections["firewall_zone"] = [Z("zi", "Internal", "internal", ZoneNetworks.Where(n => n.Id != "n70").Select(n => n.Id).ToArray()), Z("ze", "External", "external", "wan"), Z("zh", "Hotspot", "hotspot", "n70")];
        s.Collections["firewall_policy"] = [];
        return s;
    }

    private static Mock<IChangeControlService> Site()
    {
        var m = new Mock<IChangeControlService>();
        m.Setup(s => s.GetReplaceDevicesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ReplaceDevicesView
        {
            SnapshotAt = Now.AddHours(-30),
            Old = [new() { Mac = SwOld, Name = "Garage Switch", Model = "USW-24-PoE", Type = "usw" }, new() { Mac = ApOld, Name = "Back Yard AP", Model = "U7-Outdoor", Type = "uap", Online = true }],
            New = [new() { Mac = SwNew, Name = "USW-Pro-Max-16-PoE", Model = "USW-Pro-Max-16-PoE", Type = "usw", Online = true }, new() { Mac = ApNew, Name = "U7 Pro (bench)", Model = "U7-Pro", Type = "uap", Online = true }],
        });
        m.Setup(s => s.GetReplacementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string o, string n, IReadOnlyDictionary<string, string>? c, IReadOnlyCollection<string>? a, CancellationToken _) =>
                o == SwOld ? DeviceReplacement.Propose(OldSwitch(), NewSwitch(), MigrateContext(), c, a?.ToHashSet())
                : DeviceReplacement.Propose(OldAp(), NewAp(), MigrateContext(), c, a?.ToHashSet()));
        m.Setup(s => s.GetZoneSetupAsync(It.IsAny<ZoneChoices>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ZoneChoices c, bool check, CancellationToken _) =>
            {
                var live = ZoneLive();
                var board = ZoneSetup.Build(live, ZoneNetworks, c.Mode, c.Moves, c.AddedZones, c.Exceptions);
                if (check)
                {
                    board.Checks = [new("IoT networks isolated from Internal", "Pass", "ok"), new("Cameras have no internet", "Pass", "ok"),
                        new("Management isolated", "Pass", "ok"), new("Guest reaches internet only", "Pass", "ok"), new("Printers and Lab stay in their current zone", "Not checked", "warn")];
                    board.ChecksPass = true;
                }
                board.LegacySnapshotId = 3;
                board.LegacySnapshotLabel = "Sep 2, 2026 9:14 PM";
                return board;
            });
        m.Setup(s => s.GetConsoleBackupsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ConsoleBackupRecord { Id = 1, FileName = "autobackup_a.unf", TakenAt = Now.AddHours(-11), SizeBytes = 463_000, NetworkAppVersion = "11.0.21" },
            new ConsoleBackupRecord { Id = 2, FileName = "autobackup_b.unf", TakenAt = Now.AddDays(-2), SizeBytes = 455_000, NetworkAppVersion = "11.0.21", FirmwareRollout = true },
        ]);
        m.Setup(s => s.GetRestoreSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new() { Id = 41, TakenAt = Now.AddHours(-30), Label = "Oct 22, 6:00 AM, Daily", NetworkAppVersion = "11.0.21" },
            new() { Id = 37, TakenAt = Now.AddDays(-6), Label = "Oct 17, 4:00 AM, Baseline", NetworkAppVersion = "10.6.106" },
        ]);
        m.Setup(s => s.GetPendingAdoptionsAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new() { Mac = "aa:bb:cc:00:03:01", Model = "U7-Pro", Pending = true, InSnapshot = true, Ip = "192.0.2.40" },
            new() { Mac = "aa:bb:cc:00:03:02", Model = "USW-Lite-8-PoE", Pending = true, InSnapshot = true, Ip = "192.0.2.41" },
        ]);
        m.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ChangeControlSettings { ApprovalsEnabled = true });
        m.Setup(s => s.IsSiteAdminAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        m.Setup(s => s.GetStorageAsync(It.IsAny<CancellationToken>())).ReturnsAsync((38L << 20, 3_200_000L));
        m.Setup(s => s.GetAlertRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            ("Change Control: Security Regression", "change.security_regression", true),
            ("Change Control: Management Path Change, Devices Offline", "change.management_path", true),
            ("Change Control: Unattributed Change, With Impact", "change.unattributed", true),
            ("Change Control: Drift With a Security Regression", "change.drift_regression", true),
            ("Change Control: Every Change", "change.any", false),
            ("Change Control: Daily Digest", "change.digest", false),
        ]);
        var rows = new List<SnapshotRowView>
        {
            new() { Id = 8, TakenAt = Now.AddHours(-25), Trigger = "Unattributed", TriggerDot = "unattributed", ChangedObjects = 1, IssueCount = 2 },
            new() { Id = 7, TakenAt = Now.AddHours(-39), Trigger = "Before Firmware Rollout", TriggerDot = "firmware", IssueCount = 2, Pinned = true },
            new() { Id = 6, TakenAt = Now.AddHours(-56), Trigger = "Alex", TriggerDot = "person", ChangedObjects = 1, IssueCount = 2 },
            new() { Id = 5, TakenAt = Now.AddHours(-99), Trigger = "Alex", TriggerDot = "person", ChangedObjects = 1, IssueCount = 2 },
            new() { Id = 4, TakenAt = Now.AddHours(-136), Trigger = "UniFi (likely)", TriggerDot = "unattributed", ChangedObjects = 14, IssueCount = 3, RegressionCount = 2 },
            new() { Id = 3, TakenAt = Now.AddHours(-136.3), Trigger = "Before Network app upgrade", TriggerDot = "firmware", IssueCount = 1, Pinned = true },
            new() { Id = 1, TakenAt = Now.AddHours(-153), Trigger = "Daily", IssueCount = 1, Pinned = true, IsBaseline = true },
        };
        m.Setup(s => s.GetSnapshotsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SnapshotListView
        {
            Rows = rows,
            Count = 412,
            StoredBytes = 38L << 20,
            LiveIssueCount = 2,
            Settings = new ChangeControlSettings(),
        });
        m.Setup(s => s.GetDriftAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DriftView());
        m.Setup(s => s.GetEditBaseAsync(It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(
            ("""
            wlans:
              - name: Home
                security: wpapsk
                wpa_mode: wpa2
                wpa3_support: true
                wpa3_transition: true
                pmf_mode: optional
                x_passphrase: <keep current>
                network: Default
                mlo_enabled: true
                wlan_bands: [2g, 5g, 6g]
              - name: IoT
                security: wpapsk
                x_passphrase: <keep current>
                network: IoT
                wlan_bands: [2g]

            """, new Dictionary<string, int> { ["setting"] = 41, ["networkconf"] = 17, ["wlanconf"] = 3, ["firewall_zone"] = 9, ["firewall_policy"] = 115, ["firewallgroup"] = 28, ["portconf"] = 16, ["static_dns"] = 108, ["device"] = 22, ["user"] = 86 }, "{}"));
        m.Setup(s => s.GetPlanAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Plan());
        return m;
    }

    private static PlanView Plan()
    {
        var doc = new PlanDocument
        {
            Lockouts =
            [
                new() { Kind = "possible", Title = "Garage Switch may lose its management path", Detail = "Core Switch port 9 drops the Management network. Garage Switch is managed over that port.", Devices = ["Garage Switch"], OperationKeys = ["portconf:update:Trunk-Core:p1"] },
            ],
            Regressions = [new() { Kind = "Regression", Title = "IoT can reach Internal", Detail = "Rolling back removes firewall policy Block IoT to Internal.", Severity = "Recommended", Key = "r1" }],
            Fixes = [new() { Kind = "Fix", Title = "VPN to Internal allows all" }, new() { Kind = "Fix", Title = "Cameras reach the internet" }],
            ImpactRan = true,
            WatchDevices = true,
            WatchInternet = true,
            Collections = ["firewall_policy", "wlanconf", "portconf", "static_dns", "networkconf", "device"],
            Operations =
            [
                new() { Key = "fp:delete", Collection = "firewall_policy", Action = PlanAction.Delete, ObjectName = "Block IoT to Internal", ChangeText = "Added Oct 17 by Alex", Tier = PlanTiers.Acknowledge, Reasons = ["Delete"] },
                new() { Key = "fp:u1", Collection = "firewall_policy", Action = PlanAction.Update, ObjectName = "VPN to Internal", ChangeText = "Action: Allow All to Block", Fields = [new() { Label = "Action", Old = "Allow All", New = "Block" }] },
                new() { Key = "fp:u2", Collection = "firewall_policy", Action = PlanAction.Update, ObjectName = "Cameras to Internet", ChangeText = "Enabled: on to off", Fields = [new() { Label = "Enabled", Old = "on", New = "off" }] },
                new() { Key = "wl:home", Collection = "wlanconf", Action = PlanAction.Update, ObjectName = "Home", ChangeText = "Security, PMF, passphrase", Tier = PlanTiers.Acknowledge, Reasons = ["Service tag: WLAN security", "A passphrase change disconnects every client on Home"],
                    Fields = [new() { Label = "Security protocol", Old = "WPA2/WPA3", New = "WPA2" }, new() { Label = "PMF", Old = "Optional", New = "Disabled" }, new() { Label = "Passphrase", Secret = true }] },
                new() { Key = "portconf:update:Trunk-Core:p1", Collection = "portconf", Action = PlanAction.Update, ObjectName = "Trunk-Core", ChangeText = "Tagged networks: removes Management", Tier = PlanTiers.Lockout, ManagementPath = true },
                new() { Key = "dns:c", Collection = "static_dns", Action = PlanAction.Create, ObjectName = "printer.lan", ChangeText = "A 192.0.2.40" },
                new() { Key = "net:iot", Collection = "networkconf", Action = PlanAction.Update, ObjectName = "IoT network", ChangeText = "mDNS: off to on", Tier = PlanTiers.Acknowledge, Reasons = ["Service tag: mDNS/multicast"] },
                new() { Key = "dev:ap", Collection = "device", Action = PlanAction.Update, ObjectName = "Back Yard AP", ChangeText = "5 GHz channel width: 80 to 160" },
            ],
            WriteOrder =
            [
                new() { Title = "Local DNS, IoT network", Detail = "No dependencies" },
                new() { Title = "Firewall policies and policy order", Detail = "One gateway provision" },
                new() { Title = "WLAN Home, Back Yard AP", Detail = "Devices re-provision" },
                new() { Title = "Port profile Trunk-Core", Detail = "Last: it can cut a device's management path" },
                new() { Title = "Verify", Detail = "New snapshot, compared with the plan" },
            ],
        };
        doc.Context["source_detail"] = "Pinned before Firmware Rollout";
        doc.FirmwareAlign = [new() { Mac = SwNew, Name = "USW Pro Max 24 PoE", Current = "7.1.66", Target = "7.2.123" }];
        var acks = new PlanAcknowledgements { Items = ["fp:delete", "net:iot"] };
        return new PlanView
        {
            Plan = new ConfigPlan { Id = 7, Name = "Roll Back HQ to Oct 15, 9:00 AM", Status = ConfigPlanStatus.Draft, SourceDescription = "Snapshot, Oct 15, 9:00 AM", ObjectCount = 9, DeviceCount = 1 },
            Doc = doc,
            Acks = acks,
            SiteName = "HQ",
            SiteDetail = "direct",
            ApprovalsOn = true,
            ApprovalRequired = true,
            CallerIsSiteAdmin = false,
            Remaining = PlanReadiness.Remaining(doc, acks, false),
            ConfirmationText = "Garage Switch",
            WatchMinutes = 5,
            QuietestHourSuggestion = Now.AddDays(2),
        };
    }
}

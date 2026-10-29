using Microsoft.Playwright;
using Xunit;

namespace NetworkOptimizer.Web.BrowserTests;

/// <summary>
/// The "On this page" card index (wwwroot/js/card-index.js), driven in a real browser against
/// app.css, on fixture pages shaped like the app's (sidebar, .page-content scroller, article.content).
/// </summary>
[Collection(BrowserCollection.Name)]
public class CardIndexTests
{
    private const string Origin = "http://card-index.test";
    private const string Scroller = "document.querySelector('.page-content')";
    private const string Active = "[...document.querySelectorAll('.card-index-link.active')].map(a => a.textContent).join(' + ')";
    private const string Links = "[...document.querySelectorAll('.card-index-link')].map(a => a.textContent)";

    // Heights mix long cards with short ones a careless reading line skips.
    private static readonly int[] Sizes = [600, 120, 700, 80, 90, 800, 150, 900];

    private readonly BrowserFixture _fx;

    public CardIndexTests(BrowserFixture fx) => _fx = fx;

    private static string Card(string name, int height, string? id = null, string? cls = null) =>
        $"<div class=\"card {cls}\"{(id != null ? $" id=\"{id}\"" : "")}><div class=\"card-header\"><h2 class=\"card-title\">{name}</h2></div><div style=\"height:{height}px\"></div></div>";

    private static string Cards() => string.Concat(Sizes.Select((h, i) => Card($"Card {i}", h, $"c{i}")));

    private async Task<IPage> OpenAsync(string body, int width = 2560, int height = 1440, string head = "")
    {
        var page = await _fx.Browser!.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = width, Height = height } });
        var html = $"""
            <!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="css/app.css">{head}</head>
            <body><div class="app-container" style="display:flex;height:100vh"><nav class="sidebar"></nav>
            <div class="main-content"><main class="page-content"><article class="content"><div id="page">{body}</div></article></main></div></div>
            <script src="js/card-index.js"></script></body></html>
            """;
        await page.RouteAsync($"{Origin}/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath.TrimStart('/');
            if (path == "page.html")
            {
                await route.FulfillAsync(new RouteFulfillOptions { Body = html, ContentType = "text/html" });
                return;
            }
            var file = Path.Combine(_fx.WebRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) { await route.FulfillAsync(new RouteFulfillOptions { Status = 404 }); return; }
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Body = await File.ReadAllTextAsync(file),
                ContentType = path.EndsWith(".css") ? "text/css" : "application/javascript",
            });
        });
        await page.GotoAsync($"{Origin}/page.html");
        await page.WaitForTimeoutAsync(400);
        await page.Mouse.MoveAsync(width * 0.6f, height / 2f);
        return page;
    }

    private static Task<string> ActiveAsync(IPage page) => page.EvaluateAsync<string>(Active);

    private static Task ClickAsync(IPage page, string name) => page.ClickAsync($".card-index-link:text-is(\"{name}\")");

    /// <summary>Scrolls the whole page in small steps, as a reader's wheel does, recording each highlight change.</summary>
    private static async Task<List<string>> ScrollThroughAsync(IPage page)
    {
        var seen = new List<string>();
        var total = await page.EvaluateAsync<int>($"{Scroller}.scrollHeight");
        for (var y = 0; y < total; y += 30)
        {
            await page.EvaluateAsync($"window.dispatchEvent(new WheelEvent('wheel')); {Scroller}.scrollTop = {y}");
            await page.WaitForTimeoutAsync(25);
            var a = await ActiveAsync(page);
            if (seen.Count == 0 || seen[^1] != a) seen.Add(a);
        }
        return seen;
    }

    private static Task<int> TopOfAsync(IPage page, string id) =>
        page.EvaluateAsync<int>($"Math.round(document.getElementById('{id}').getBoundingClientRect().top - {Scroller}.getBoundingClientRect().top)");

    [BrowserFact]
    public async Task Index_ShowsWhenACardStartsBelowTheFirstScreen()
    {
        var page = await OpenAsync(Card("Device Status", 50) + "<div style=\"height:2000px\"></div>" + Card("Temperature Alert Thresholds", 50));
        Assert.Equal(["Device Status", "Temperature Alert Thresholds"], await page.EvaluateAsync<string[]>(Links));
    }

    [BrowserFact]
    public async Task Index_IsAbsentWhenEveryCardFits()
    {
        var page = await OpenAsync(Card("C0", 50) + Card("C1", 50) + Card("C2", 50));
        Assert.Empty(await page.EvaluateAsync<string[]>(Links));
    }

    [BrowserFact]
    public async Task Names_SkipTheSuffixAndTitleControls_AndHonorDataCardIndex()
    {
        var page = await OpenAsync(
            Card("Snapshots<span class=\"card-title-suffix\">170 kept, 5 MB</span>", 1500)
            + Card("Access Layer <select><option>GPON</option><option>DSL (ADSL/VDSL)</option></select>", 50)
            + "<section class=\"card\" data-card-index=\"Sites\"><header><h3 class=\"shp-title\">Sites</h3></header></section>");
        Assert.Equal(["Snapshots", "Access Layer", "Sites"], await page.EvaluateAsync<string[]>(Links));
    }

    [BrowserFact]
    public async Task Cards_AreListedInReadingOrder_NotDomOrder()
    {
        // The Dashboard's shape: each column is its own container, so the DOM runs down one column first.
        var columns = "<div style=\"display:grid;grid-template-columns:1fr 1fr;gap:1rem;align-items:start\">"
            + "<div>" + Card("ONT Stats", 900) + Card("Starlink Stats", 700) + "</div>"
            + "<div>" + Card("Cellular Stats", 1300) + Card("Active Alerts", 900) + "</div></div>";
        var page = await OpenAsync(Card("Sites", 600) + columns);
        Assert.Equal(["Sites", "ONT Stats", "Cellular Stats", "Starlink Stats", "Active Alerts"], await page.EvaluateAsync<string[]>(Links));
    }

    [BrowserFact]
    public async Task SlowScroll_LightsEveryCardInOrder()
    {
        var page = await OpenAsync(Cards());
        Assert.Equal(Sizes.Select((_, i) => $"Card {i}"), await ScrollThroughAsync(page));
    }

    [BrowserFact]
    public async Task SideBySideCards_LightTogether_AndAClickLightsOnlyTheOneClicked()
    {
        var grid = "<div style=\"display:grid;grid-template-columns:1fr 1fr 1fr;gap:1rem\">"
            + Card("Access Layer", 300) + Card("ISP Network", 300) + Card("Transit Health", 300) + "</div>";
        var page = await OpenAsync(Card("Findings", 500) + grid + Card("Networks on Your Path", 900) + Card("Internet Outages", 900));

        Assert.Equal(["Findings", "Access Layer + ISP Network + Transit Health", "Networks on Your Path", "Internet Outages"],
            await ScrollThroughAsync(page));

        await page.EvaluateAsync($"{Scroller}.scrollTop = 0");
        await ClickAsync(page, "Access Layer");
        await page.WaitForTimeoutAsync(900);
        Assert.Equal("Access Layer", await ActiveAsync(page));
    }

    [BrowserFact]
    public async Task DeepLink_LightsItsCard_IncludingOnesNearThePageEnd()
    {
        var page = await OpenAsync(Cards());
        for (var i = 0; i < Sizes.Length; i++)
        {
            await page.EvaluateAsync($"document.getElementById('c{i}').scrollIntoView({{ behavior: 'instant', block: 'start' }})");
            await page.WaitForTimeoutAsync(120);
            Assert.Equal($"Card {i}", await ActiveAsync(page));
        }
    }

    [BrowserFact]
    public async Task ReloadRestore_WinsOverTheDeepLinkBeforeIt()
    {
        var page = await OpenAsync(Cards());
        await page.EvaluateAsync("document.getElementById('c1').scrollIntoView({ behavior: 'instant', block: 'start' })");
        await page.WaitForTimeoutAsync(100);
        // scrollRestoration.js re-applies the saved scrollTop every 100 ms after a reload.
        for (var n = 0; n < 3; n++)
        {
            await page.EvaluateAsync($"{Scroller}.scrollTop += document.getElementById('c5').getBoundingClientRect().top - {Scroller}.getBoundingClientRect().top + 1");
            await page.WaitForTimeoutAsync(100);
        }
        Assert.Equal("Card 5", await ActiveAsync(page));
    }

    [BrowserFact]
    public async Task ScrollingUpAfterAClick_NeverDropsToALaterCard()
    {
        var page = await OpenAsync(Cards());
        await page.EvaluateAsync($"{Scroller}.scrollTop = 1e6");
        await page.WaitForTimeoutAsync(100);
        await ClickAsync(page, "Card 5");
        await page.WaitForTimeoutAsync(900);

        var seen = new List<string> { await ActiveAsync(page) };
        for (var n = 0; n < 60; n++)
        {
            await page.Mouse.WheelAsync(0, -60);
            await page.WaitForTimeoutAsync(40);
            var a = await ActiveAsync(page);
            if (seen[^1] != a) seen.Add(a);
        }
        Assert.Equal(["Card 5", "Card 4", "Card 3", "Card 2", "Card 1", "Card 0"], seen);
    }

    [BrowserFact]
    public async Task ScrollingDownAfterAClickNearTheEnd_StepsThroughEveryLaterCard()
    {
        // Wi-Fi Optimizer - Overview's shape: short cards at the end the line passes all at once.
        var stack = "<div style=\"display:grid;grid-template-columns:1fr 1fr;gap:1rem\">" + Card("Wi-Fi Health Score", 500)
            + "<div>" + Card("Client Band Distribution", 200) + Card("Channel Optimization", 260, "co") + "</div></div>";
        var page = await OpenAsync(Card("Overview", 900) + stack + Card("Health Issues", 160) + Card("Access Points", 220) + Card("Speed / Coverage Map", 300));
        await page.EvaluateAsync($"{Scroller}.scrollTop = 1e6");
        await page.WaitForTimeoutAsync(100);
        await ClickAsync(page, "Channel Optimization");
        await page.WaitForTimeoutAsync(1500);
        Assert.Equal("Channel Optimization", await ActiveAsync(page));

        await page.Mouse.MoveAsync(1500, 700);
        var seen = new List<string> { await ActiveAsync(page) };
        for (var n = 0; n < 40; n++)
        {
            await page.Mouse.WheelAsync(0, 40);
            await page.WaitForTimeoutAsync(40);
            var a = await ActiveAsync(page);
            if (seen[^1] != a) seen.Add(a);
        }
        Assert.Equal(["Channel Optimization", "Health Issues", "Access Points", "Speed / Coverage Map"], seen);
    }

    [BrowserFact]
    public async Task WheelOverTheList_ScrollsThePage()
    {
        var page = await OpenAsync(Cards());
        await page.HoverAsync(".card-index-link:text-is(\"Card 3\")");
        await page.Mouse.WheelAsync(0, 400);
        await page.WaitForTimeoutAsync(300);
        Assert.True(await page.EvaluateAsync<int>($"{Scroller}.scrollTop") > 300);
    }

    [BrowserFact]
    public async Task Click_LandsItsCard_WhenContentAboveGrowsMidScroll()
    {
        var page = await OpenAsync(Cards());
        await page.EvaluateAsync($"{Scroller}.scrollTop = 1e6");
        await page.Mouse.WheelAsync(0, 1);
        await page.WaitForTimeoutAsync(700);

        await ClickAsync(page, "Card 5");
        await page.WaitForTimeoutAsync(150);
        await page.EvaluateAsync("document.querySelector('#c1 > div:last-child').style.height = '520px'");
        await page.WaitForTimeoutAsync(2500);

        Assert.Equal(16, await TopOfAsync(page, "c5"));
        Assert.Equal("Card 5", await ActiveAsync(page));
    }

    [BrowserFact]
    public async Task DoubleClick_LandsAndKeepsItsCard()
    {
        var page = await OpenAsync(Cards());
        await page.EvaluateAsync($"{Scroller}.scrollTop = 1e6");
        await page.Mouse.WheelAsync(0, 1);
        await page.WaitForTimeoutAsync(700);

        await ClickAsync(page, "Card 5");
        await page.WaitForTimeoutAsync(150);
        await ClickAsync(page, "Card 5");
        await page.WaitForTimeoutAsync(2500);

        Assert.Equal(16, await TopOfAsync(page, "c5"));
        Assert.Equal("Card 5", await ActiveAsync(page));
    }

    [BrowserFact]
    public async Task WheelAtThePageEnd_MovesTheHighlightOn()
    {
        var page = await OpenAsync(Cards());
        await ClickAsync(page, "Card 6");
        await page.WaitForTimeoutAsync(2000);
        Assert.Equal(0, await page.EvaluateAsync<int>($"Math.round({Scroller}.scrollHeight - {Scroller}.clientHeight - {Scroller}.scrollTop)"));
        Assert.Equal("Card 6", await ActiveAsync(page));

        await page.Mouse.WheelAsync(0, 100);
        await page.WaitForTimeoutAsync(300);
        Assert.Equal("Card 7", await ActiveAsync(page));
    }

    [BrowserFact]
    public async Task TabSwitchedByClass_RebuildsTheIndex()
    {
        var body = Card("Security Audit", 700, cls: "tab-sec") + Card("Alert Channels", 700, cls: "tab-sec") + Card("Threat Intelligence", 700, cls: "tab-sec")
            + Card("Application Settings", 1500, cls: "tab-app") + Card("Guided Tours", 900, cls: "tab-app");
        var page = await OpenAsync(body, head: "<style>#page.on-sec .tab-app, #page.on-app .tab-sec { display: none; }</style>");
        await page.EvaluateAsync("document.getElementById('page').className = 'on-sec'");
        await page.WaitForTimeoutAsync(400);
        Assert.Equal(["Security Audit", "Alert Channels", "Threat Intelligence"], await page.EvaluateAsync<string[]>(Links));

        await page.EvaluateAsync("document.getElementById('page').className = 'on-app'");
        await page.WaitForTimeoutAsync(400);
        Assert.Equal(["Application Settings", "Guided Tours"], await page.EvaluateAsync<string[]>(Links));
    }

    [BrowserFact]
    public async Task Rerender_KeepsOneIndex_AndClicksLandOnTheNewNodes()
    {
        var page = await OpenAsync(Cards());
        await page.EvaluateAsync("document.getElementById('page').innerHTML = document.getElementById('page').innerHTML");
        await page.WaitForTimeoutAsync(400);
        Assert.Equal(1, await page.EvaluateAsync<int>("document.querySelectorAll('.card-index, .card-index-row').length"));

        await ClickAsync(page, "Card 3");
        await page.WaitForTimeoutAsync(1500);
        Assert.Equal(16, await TopOfAsync(page, "c3"));
        Assert.Equal("Card 3", await ActiveAsync(page));
    }

    [BrowserFact]
    public async Task NarrowGutter_GetsARow_WideGutter_GetsAListBelowTheBackButton()
    {
        var narrow = await OpenAsync(Cards(), width: 1920, height: 1080);
        Assert.True(await narrow.EvaluateAsync<bool>("!!document.querySelector('nav.card-index-row') && !document.querySelector('nav.card-index')"));

        var back = "<div class=\"page-back-float\"><button type=\"button\" class=\"page-back-btn\" aria-label=\"Back\">&lt;</button></div>";
        var wide = await OpenAsync(back + Cards());
        await wide.EvaluateAsync($"{Scroller}.scrollTop = 600");
        await wide.WaitForTimeoutAsync(150);
        var gap = await wide.EvaluateAsync<double>("document.querySelector('nav.card-index').getBoundingClientRect().top - document.querySelector('.page-back-float .page-back-btn').getBoundingClientRect().bottom");
        Assert.True(gap >= 15, $"list sits {gap} px below the back button");
    }
}

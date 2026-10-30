using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace NetworkOptimizer.Web.BrowserTests;

/// <summary>
/// Change Control's YAML editor (wwwroot/js/cc-editor.js), driven in a real browser against the
/// vendored CodeMirror bundle and app.css. The page's .NET side is a stub that records each call.
/// </summary>
[Collection(BrowserCollection.Name)]
public class CcEditorTests
{
    private const string Origin = "http://cc-editor.test";
    private const string View = "document.getElementById('host')._cc.view";
    private const string Doc = View + ".state.doc.toString()";
    private const string FoldCount = "(() => { let n = 0; cm.foldedRanges(" + View + ".state).between(0, " + View + ".state.doc.length, () => { n++; }); return n; })()";

    // CodeMirror ignores Enter for 75 ms after a completion list opens (interactionDelay), so a pick
    // made sooner falls through to a line break. A person never types that fast; a test does.
    private const int CompletionDelayMs = 150;

    private readonly BrowserFixture _fx;

    public CcEditorTests(BrowserFixture fx) => _fx = fx;

    // ---------- Fixtures ----------

    private static string Id(int i) => i.ToString("x24");

    /// <summary>A firewall policy as the editor's export writes it: "- id:" first, the name next.</summary>
    private static string Policy(int i, string src = "Internal", string dst = "External", string? name = null) =>
        $"- id: {Id(i)}\n" +
        $"  name: \"{name ?? $"Policy {i}"}\"\n" +
        "  action: BLOCK\n" +
        "  enabled: true\n" +
        "  source:\n" +
        $"    zone: {src}\n" +
        "    matching_target: IP\n" +
        "    matching_target_type: SPECIFIC\n" +
        "    ips:\n" +
        $"      - 192.0.2.{i % 250 + 1}\n" +
        "    port_matching_type: ANY\n" +
        "  destination:\n" +
        $"    zone: {dst}\n" +
        "    matching_target: ANY\n" +
        "    port_matching_type: ANY\n";

    private static string Heading(string src, string dst) => $"# {src} → {dst}\n";

    /// <summary>The Firewall Policies group: its collection, then the empty ones exported after it.</summary>
    private static string Firewall(params string[] parts) =>
        "firewall_policies:\n" + string.Concat(parts) + "content_filtering: []\nacl_rules: []\n";

    private static string Schema(IReadOnlyDictionary<string, string>? managed = null, bool secretName = false)
    {
        object Side() => new
        {
            fields = new object[]
            {
                new { name = "zone", @ref = "zones" },
                new { name = "matching_target", values = new[] { "ANY", "IP", "NETWORK" } },
                new { name = "matching_target_type", values = new[] { "SPECIFIC", "OBJECT" } },
                new { name = "ips" },
                new { name = "network_ids", @ref = "networks" },
                new { name = "port_matching_type", values = new[] { "ANY", "SPECIFIC" } },
            },
        };
        var schema = new
        {
            collections = new Dictionary<string, object>
            {
                ["firewall_policies"] = new
                {
                    add = "Firewall Policy",
                    managed = managed ?? new Dictionary<string, string>(),
                    undeletable = (managed ?? new Dictionary<string, string>()).Keys.ToArray(),
                    fields = new object[]
                    {
                        new { name = "name", secret = secretName },
                        new { name = "action", values = new[] { "ALLOW", "BLOCK", "REJECT" } },
                        new { name = "enabled", values = new[] { "true", "false" } },
                        new { name = "source", shape = Side() },
                        new { name = "destination", shape = Side() },
                    },
                },
                ["wlans"] = new
                {
                    add = "WLAN",
                    fields = new object[] { new { name = "name" }, new { name = "x_passphrase", secret = true } },
                },
                ["content_filtering"] = new { },
                ["acl_rules"] = new { },
            },
            refs = new
            {
                zones = new[] { "Internal", "External", "Gateway", "Hotspot" },
                networks = new[] { new { label = "Default", value = "Default" }, new { label = "IoT", value = "IoT" } },
            },
        };
        return JsonSerializer.Serialize(schema);
    }

    /// <summary>
    /// Opens the editor on text, with a stub page that records OnEditorChanged (decoding the streamed
    /// text), AddObject, RevealSecret, and GenerateSecret. tall: the editor shows every line, so each
    /// line's widgets are in the DOM; off, it keeps its 560 px scroller.
    /// </summary>
    private async Task<IPage> OpenAsync(string text, string? schema = null, string key = "1|Firewall Policies",
        string? scope = "viewer|site:test", bool tall = true, int width = 1400, int height = 2400)
    {
        var page = await _fx.Browser!.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = width, Height = height } });
        var html = $$"""
            <!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="css/app.css">
            {{(tall ? "<style>.cc-editor .cm-editor { max-height: none !important; }</style>" : "")}}</head>
            <body><div class="change-control"><div class="card"><div class="cc-editor" id="host"></div></div></div>
            <script type="module">
            import * as cc from './js/cc-editor.js';
            import * as cm from './lib/codemirror/codemirror.bundle.js?v=7';
            window.cc = cc;
            window.cm = cm;
            window.calls = [];
            window.DotNet = { createJSStreamReference: bytes => ({ bytes }) };
            window.dotnet = {
                invokeMethodAsync: (name, ...args) => {
                    if (name === 'OnEditorChanged') {
                        window.calls.push({ name, key: args[0], text: new TextDecoder().decode(args[1].bytes) });
                        return Promise.resolve();
                    }
                    window.calls.push({ name, args });
                    if (name === 'RevealSecret') return Promise.resolve('hunter2-revealed');
                    if (name === 'GenerateSecret') return Promise.resolve('generated-' + args[0]);
                    return Promise.resolve(null);
                },
            };
            window.ready = true;
            </script></body></html>
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
        await page.WaitForFunctionAsync("window.ready === true");
        await page.EvaluateAsync("""
            ([text, schema, key, scope]) => {
                const host = document.getElementById('host');
                cc.create(host, window.dotnet, '', 'YAML', key, true, scope);
                if (schema) cc.setSchema(host, schema);
                cc.setText(host, text, key);
            }
            """, new object?[] { text, schema, key, scope });
        await page.WaitForTimeoutAsync(100);
        return page;
    }

    private static Task<string> DocAsync(IPage page) => page.EvaluateAsync<string>(Doc);

    private static Task CursorAsync(IPage page, int pos) =>
        page.EvaluateAsync($"(() => {{ const v = {View}; v.dispatch({{ selection: {{ anchor: {pos} }} }}); v.focus(); }})()");

    /// <summary>The position of the end of the first line containing text, or of text's own start with atStart.</summary>
    private static Task<int> PosOfAsync(IPage page, string text, bool atStart = false, int nth = 0) =>
        page.EvaluateAsync<int>("""
            ([text, atStart, nth]) => {
                const doc = document.getElementById('host')._cc.view.state.doc.toString();
                let at = -1;
                for (let i = 0; i <= nth; i++) at = doc.indexOf(text, at + 1);
                if (at < 0) throw new Error('not in doc: ' + text);
                return atStart ? at : at + text.length;
            }
            """, new object[] { text, atStart, nth });

    /// <summary>Clicks a document position with the mouse (a real pointer selection), dx pixels right of it.</summary>
    private static async Task ClickPosAsync(IPage page, int pos, int dx = 0)
    {
        var xy = await page.EvaluateAsync<double[]>($"(() => {{ const c = {View}.coordsAtPos({pos}); return [c.left, (c.top + c.bottom) / 2]; }})()");
        await page.Mouse.ClickAsync((float)xy[0] + dx, (float)xy[1]);
    }

    private static Task<int> ChangeCallsAsync(IPage page) =>
        page.EvaluateAsync<int>("window.calls.filter(c => c.name === 'OnEditorChanged').length");

    private static Task<string?> LastChangeAsync(IPage page) =>
        page.EvaluateAsync<string?>("(window.calls.filter(c => c.name === 'OnEditorChanged').at(-1) ?? {}).text ?? null");

    private static Task<string[]> TextsAsync(IPage page, string selector) =>
        page.EvaluateAsync<string[]>($"[...document.querySelectorAll('{selector}')].map(e => e.textContent)");

    private static Task<bool> CompletionOpenAsync(IPage page) =>
        page.EvaluateAsync<bool>("!!document.querySelector('.cm-tooltip-autocomplete')");

    // ---------- Document and change reporting ----------

    [BrowserFact]
    public async Task SetText_ShowsTheText_AndIsNoEdit()
    {
        var text = Firewall(Policy(0), Policy(1));
        var page = await OpenAsync(text, Schema());
        Assert.Equal(text, await DocAsync(page));
        await page.EvaluateAsync("cc.setText(document.getElementById('host'), 'wlans: []\\n', '2|WLANs')");
        await page.WaitForTimeoutAsync(400);
        Assert.Equal(0, await ChangeCallsAsync(page));
    }

    [BrowserFact]
    public async Task Typing_ReportsTheWholeText_AfterAPause()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        await CursorAsync(page, await PosOfAsync(page, "Policy 0"));
        await page.Keyboard.TypeAsync(" renamed");
        Assert.Equal(0, await ChangeCallsAsync(page));
        await page.WaitForTimeoutAsync(400);
        Assert.Equal(1, await ChangeCallsAsync(page));
        Assert.Equal(await DocAsync(page), await LastChangeAsync(page));
        Assert.Contains("Policy 0 renamed", await LastChangeAsync(page));
    }

    [BrowserFact]
    public async Task ALargeCollection_IsReportedWhole_PastSignalRsMessageLimit()
    {
        // Firewall Policies on a large site is past SignalR's 32 KB message limit: sent as a plain
        // argument, the call was dropped and no edit to the collection ever counted.
        var page = await OpenAsync(Firewall(Enumerable.Range(0, 200).Select(i => Policy(i)).ToArray()), Schema());
        Assert.True(await page.EvaluateAsync<int>($"{Doc}.length") > 32 * 1024);
        await CursorAsync(page, await PosOfAsync(page, "Policy 150"));
        await page.Keyboard.TypeAsync("x");
        await page.WaitForTimeoutAsync(400);
        Assert.Equal(await DocAsync(page), await LastChangeAsync(page));
    }

    [BrowserFact]
    public async Task SetText_SendsAPendingChange_UnderItsOwnKey_First()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema(), key: "1|Firewall Policies");
        await CursorAsync(page, await PosOfAsync(page, "Policy 0"));
        await page.Keyboard.TypeAsync("!");
        await page.EvaluateAsync("cc.setText(document.getElementById('host'), 'wlans: []\\n', '2|WLANs')");
        var change = await page.EvaluateAsync<string[]>("(() => { const c = window.calls.find(c => c.name === 'OnEditorChanged'); return [c.key, c.text]; })()");
        Assert.Equal("1|Firewall Policies", change[0]);
        Assert.Contains("Policy 0!", change[1]);
    }

    [BrowserFact]
    public async Task Enter_IndentsUnderAKey_AndKeepsAnItemsKeys()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        await CursorAsync(page, await PosOfAsync(page, "  source:"));
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.TypeAsync("x");
        Assert.Contains("  source:\n    x\n", await DocAsync(page));

        Assert.Equal(2, await page.EvaluateAsync<int>("cc.yamlIndentAfter('- id: abc')"));
        Assert.Equal(4, await page.EvaluateAsync<int>("cc.yamlIndentAfter('  destination:')"));
        Assert.Equal(4, await page.EvaluateAsync<int>("cc.yamlIndentAfter('    zone: External  # comment')"));
        Assert.Equal(4, await page.EvaluateAsync<int>("cc.yamlIndentAfter('  - name: x')"));
        Assert.Equal(6, await page.EvaluateAsync<int>("cc.yamlIndentAfter('  - ports:')"));
    }

    // ---------- Removing and adding objects ----------

    [BrowserFact]
    public async Task Remove_DeletesTheWholeObject_AndKeepsTheHeading()
    {
        var page = await OpenAsync(Firewall(Heading("Internal", "External"), Policy(0), Policy(1)), Schema());
        Assert.Equal(["Remove Policy 0", "Remove Policy 1"], await page.EvaluateAsync<string[]>(
            "[...document.querySelectorAll('.cc-remove-item')].map(b => b.getAttribute('aria-label'))"));
        await page.ClickAsync(".cc-remove-item >> nth=0");
        Assert.Equal(Firewall(Heading("Internal", "External"), Policy(1)), await DocAsync(page));
    }

    [BrowserFact]
    public async Task AddLine_AsksThePageToAddToItsCollection()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        Assert.Equal(["+ Add Firewall Policy"], await TextsAsync(page, ".cc-add-item"));
        await page.ClickAsync(".cc-add-item");
        Assert.Equal("firewall_policies", await page.EvaluateAsync<string>("window.calls.find(c => c.name === 'AddObject').args[0]"));
    }

    [BrowserFact]
    public async Task InsertObject_GoesAtTheCollectionsEnd_WithTheCursorOnItsFirstBlankField()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        await page.EvaluateAsync("cc.insertObject(document.getElementById('host'), 'firewall_policies', '- name: \\n  action: BLOCK\\n')");
        Assert.Equal(Firewall(Policy(0), "- name: \n  action: BLOCK\n"), await DocAsync(page));
        Assert.Equal(await PosOfAsync(page, "- name: "), await page.EvaluateAsync<int>($"{View}.state.selection.main.head"));
    }

    [BrowserFact]
    public async Task InsertObject_IntoAnEmptyCollection_DropsItsBrackets()
    {
        var page = await OpenAsync("wlans: []\n", Schema());
        await page.EvaluateAsync("cc.insertObject(document.getElementById('host'), 'wlans', '- name: \\n')");
        Assert.Equal("wlans:\n- name: \n", await DocAsync(page));
    }

    [BrowserFact]
    public async Task InsertObject_PastALockedPolicyThatEndsTheFile_IsAllowed()
    {
        var managed = new Dictionary<string, string> { [Id(1)] = "UniFi" };
        var page = await OpenAsync("firewall_policies:\n" + Policy(0) + Policy(1), Schema(managed));
        await page.EvaluateAsync("cc.insertObject(document.getElementById('host'), 'firewall_policies', '- name: \\n')");
        Assert.EndsWith(Policy(1) + "- name: \n", await DocAsync(page));
    }

    // ---------- Read-only objects ----------

    [BrowserFact]
    public async Task ManagedObjects_AreLabeled_HaveNoRemove_AndRefuseTyping()
    {
        var managed = new Dictionary<string, string> { [Id(1)] = "UniFi", [Id(2)] = "Threat Intelligence" };
        var text = Firewall(Policy(0), Policy(1), Policy(2));
        var page = await OpenAsync(text, Schema(managed));

        Assert.Equal(["Read-only", "Read-only"], await TextsAsync(page, ".cc-managed-label"));
        Assert.Equal(["Managed by UniFi; Change Control does not edit it.", "Managed by Threat Intelligence; change it there."],
            await page.EvaluateAsync<string[]>("[...document.querySelectorAll('.cc-managed-label')].map(e => e.dataset.tooltip)"));
        Assert.Equal(["Remove Policy 0"], await page.EvaluateAsync<string[]>(
            "[...document.querySelectorAll('.cc-remove-item')].map(b => b.getAttribute('aria-label'))"));

        await CursorAsync(page, await PosOfAsync(page, "Policy 1"));
        await page.Keyboard.TypeAsync("xyz");
        await page.Keyboard.PressAsync("Backspace");
        Assert.Equal(text, await DocAsync(page));
    }

    [BrowserFact]
    public async Task RemovingTheObjectAboveALockedOne_StillWorks()
    {
        var managed = new Dictionary<string, string> { [Id(1)] = "UniFi" };
        var page = await OpenAsync(Firewall(Policy(0), Policy(1)), Schema(managed));
        await page.ClickAsync(".cc-remove-item");
        Assert.Equal(Firewall(Policy(1)), await DocAsync(page));
    }

    // ---------- Firewall policy order ----------

    [BrowserFact]
    public async Task PolicyOrder_LabelsEachPolicyInItsZonePair_WithoutUniFisPredefinedOnes()
    {
        var managed = new Dictionary<string, string> { [Id(3)] = "UniFi" };
        var page = await OpenAsync(Firewall(
            Heading("Internal", "External"), Policy(0), Policy(1), Policy(3),
            Heading("Internal", "Gateway"), Policy(2, dst: "Gateway")), Schema(managed));
        Assert.Equal(["Internal → External, 1 of 2", "Internal → External, 2 of 2", "Internal → Gateway, 1 of 1"],
            await TextsAsync(page, ".cc-policy-rank-label"));
    }

    [BrowserFact]
    public async Task OrderArrows_MoveAPolicyWithinItsPair_AndStopAtItsEnds()
    {
        var page = await OpenAsync(Firewall(Heading("Internal", "External"), Policy(0), Policy(1)), Schema());
        var disabled = await page.EvaluateAsync<bool[]>("[...document.querySelectorAll('.cc-policy-step')].map(b => b.disabled)");
        Assert.Equal(new[] { true, false, false, true }, disabled);
        await page.ClickAsync(".cc-policy-step[aria-label='Move down in its zone pair'] >> nth=0");
        Assert.Equal(Firewall(Heading("Internal", "External"), Policy(1), Policy(0)), await DocAsync(page));
        // The arrows read the document at click time: the same button moves it straight back.
        await page.ClickAsync(".cc-policy-step[aria-label='Move up in its zone pair'] >> nth=1");
        Assert.Equal(Firewall(Heading("Internal", "External"), Policy(0), Policy(1)), await DocAsync(page));
    }

    [BrowserFact]
    public async Task OrderArrows_SitOnThePolicysFirstLine_AtItsHeight()
    {
        var page = await OpenAsync(Firewall(Policy(0), Policy(1)), Schema());
        var heights = await page.EvaluateAsync<double[]>(
            "[...document.querySelectorAll('.cm-line')].slice(0, 4).map(l => l.getBoundingClientRect().height)");
        Assert.All(heights, h => Assert.Equal(heights[0], h, 1));
    }

    [BrowserFact]
    public async Task APolicyWithoutZones_IsCountedAlone_WithNoPairName()
    {
        var page = await OpenAsync("firewall_policies:\n- name: New\n  action: BLOCK\n", Schema());
        Assert.Equal(["1 of 1"], await TextsAsync(page, ".cc-policy-rank-label"));
    }

    // ---------- Folding ----------

    [BrowserFact]
    public async Task CollapseAll_FoldsEveryObject_NamedByItsName_AndExpandAllOpensThem()
    {
        var page = await OpenAsync(Firewall(Policy(0), Policy(1), Policy(2)), Schema());
        await page.ClickAsync(".cc-collapse-children >> nth=0");
        Assert.Equal(3, await page.EvaluateAsync<int>(FoldCount));
        Assert.Equal(["Policy 0 …", "Policy 1 …", "Policy 2 …"], await TextsAsync(page, ".cm-foldPlaceholder"));
        Assert.Equal("Expand All", await page.TextContentAsync(".cc-collapse-children >> nth=0"));
        await page.ClickAsync(".cc-collapse-children >> nth=0");
        Assert.Equal(0, await page.EvaluateAsync<int>(FoldCount));
    }

    [BrowserFact]
    public async Task CollapseAll_OnAZoneHeading_FoldsOnlyItsGroup_AndTheCollectionsStillFoldsAll()
    {
        var page = await OpenAsync(Firewall(
            Heading("Internal", "External"), Policy(0), Policy(1),
            Heading("Internal", "Gateway"), Policy(2, dst: "Gateway")), Schema());
        // One on the collection's key, one on each heading.
        Assert.Equal(3, await page.EvaluateAsync<int>("document.querySelectorAll('.cc-collapse-children').length"));
        await page.ClickAsync(".cc-collapse-children >> nth=2");
        Assert.Equal(["Policy 2 …"], await TextsAsync(page, ".cm-foldPlaceholder"));
        await page.ClickAsync(".cc-collapse-children >> nth=0");
        Assert.Equal(3, await page.EvaluateAsync<int>(FoldCount));
    }

    [BrowserFact]
    public async Task EmptyCollectionsAfterTheLastObject_StayOutOfItsFold()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        await page.ClickAsync(".cc-collapse-children >> nth=0");
        var lines = await TextsAsync(page, ".cm-line");
        Assert.Contains("content_filtering: []", lines);
        Assert.Contains("acl_rules: []", lines);
        // The last policy's block ends at its own last line, so its x and arrows take nothing after it.
        var blocks = await page.EvaluateAsync<string[]>($"cc.itemBlocks({View}.state.doc).map(b => b.collection + ':' + b.label)");
        Assert.Equal(["firewall_policies:Policy 0"], blocks);
    }

    [BrowserFact]
    public async Task ClickingRightOfAFoldedObject_OpensNoList()
    {
        var page = await OpenAsync(Firewall(Policy(0), Policy(1)), Schema());
        await page.ClickAsync(".cc-collapse-children >> nth=0");
        var placeholder = await page.QuerySelectorAsync(".cm-foldPlaceholder >> nth=0");
        var box = await placeholder!.BoundingBoxAsync();
        await page.Mouse.ClickAsync(box!.X + box.Width + 80, box.Y + box.Height / 2);
        await page.WaitForTimeoutAsync(150);
        Assert.False(await CompletionOpenAsync(page));
    }

    [BrowserFact]
    public async Task Folds_SurviveSwitchingToAnotherCollectionAndBack()
    {
        var text = Firewall(Policy(0), Policy(1), Policy(2));
        var page = await OpenAsync(text, Schema());
        await page.ClickAsync(".cc-collapse-children >> nth=0");
        await page.EvaluateAsync("cc.setText(document.getElementById('host'), 'wlans: []\\n', '1|WLANs')");
        Assert.Equal(0, await page.EvaluateAsync<int>(FoldCount));
        await page.EvaluateAsync("([t]) => cc.setText(document.getElementById('host'), t, '1|Firewall Policies')", new object[] { text });
        Assert.Equal(3, await page.EvaluateAsync<int>(FoldCount));
    }

    [BrowserFact]
    public async Task Folds_AfterAnUploadAndDiscard_LandOnTheirObjects()
    {
        // An upload's text has other lengths; Discard restored its folds by position, and one ran from
        // line 1 into the middle of a policy thousands of lines down.
        var policies = Enumerable.Range(0, 40).Select(i => Policy(i)).ToArray();
        var original = Firewall(policies);
        var upload = original.Replace("name: \"Policy ", "name: \"Uploaded and much longer Policy ");
        var page = await OpenAsync(original, Schema(), key: "1|Firewall Policies");
        await page.EvaluateAsync("([t]) => cc.setText(document.getElementById('host'), t, '2|Firewall Policies')", new object[] { upload });
        await page.ClickAsync(".cc-collapse-children >> nth=0");
        await page.EvaluateAsync("([t]) => cc.setText(document.getElementById('host'), t, '3|Firewall Policies')", new object[] { original });

        var folds = await page.EvaluateAsync<string[]>($$"""
            (() => {
                const state = {{View}}.state, out = [];
                cm.foldedRanges(state).between(0, state.doc.length, (f, t) => {
                    const first = state.doc.lineAt(f), last = state.doc.lineAt(t);
                    out.push((first.to === f && last.to === t ? '' : 'mid-line ') + first.text);
                });
                return out;
            })()
            """);
        Assert.Equal(40, folds.Length);
        Assert.All(folds, f => Assert.StartsWith("- id: ", f));
    }

    [BrowserFact]
    public async Task Folds_ComeBackByObject_WhenTheEditorIsCreatedAgain()
    {
        var text = Firewall(Policy(0), Policy(1), Policy(2));
        var page = await OpenAsync(text, Schema());
        await page.EvaluateAsync("localStorage.clear()");
        await page.EvaluateAsync($$"""
            (() => {
                const v = {{View}};
                const b = cc.itemBlocks(v.state.doc)[1];
                v.dispatch({ effects: cm.foldEffect.of({ from: b.lineEnd, to: b.to }) });
            })()
            """);
        // A reload: a new editor, the same scope and collection, the policy moved down by an edit elsewhere.
        var moved = text.Replace(Policy(0), Policy(0) + Policy(9));
        await page.EvaluateAsync("""
            ([t]) => {
                const host = document.getElementById('host');
                cc.create(host, window.dotnet, t, 'YAML', '1|Firewall Policies', true, 'viewer|site:test');
            }
            """, new object[] { moved });
        Assert.Equal(["Policy 1 …"], await TextsAsync(page, ".cm-foldPlaceholder"));
    }

    // ---------- Typing helpers ----------

    [BrowserTheory]
    [InlineData("  name: Diag Ping|", "  name: \"Diag Ping\"")]
    [InlineData("  name: Diag Ping|  # note", "  name: \"Diag Ping\"  # note")]
    [InlineData("  name: [TEST] NO lifecycle|", "  name: \"[TEST] NO lifecycle\"")]
    [InlineData("  name: |[TEST] NO lifecycle", "  name: \"[TEST] NO lifecycle\"")]
    [InlineData("  name: |[TEST] NO lifecycle\"", "  name: \"[TEST] NO lifecycle\"")]
    [InlineData("  name: \"Diag Ping|", "  name: \"Diag Ping\"")]
    [InlineData("  name: |Diag Ping", "  name: \"Diag Ping\"")]
    [InlineData("    ips: [192.0.2.10|]", "    ips: [\"192.0.2.10\"]")]
    [InlineData("    ips: [a, b|]", "    ips: [a, \"b\"]")]
    [InlineData("    ips: [|a, b]", "    ips: [\"a\", b]")]
    [InlineData("  source: {zone: Internal|}", "  source: {zone: \"Internal\"}")]
    [InlineData("  enabled: true|", "  enabled: \"true\"")]
    public async Task AQuote_WrapsTheValueItTouches_AndNeverDoubles(string marked, string expected)
    {
        var at = marked.IndexOf('|');
        var line = marked.Remove(at, 1);
        var page = await OpenAsync("firewall_policies:\n- id: x\n" + line + "\n", Schema());
        await CursorAsync(page, "firewall_policies:\n- id: x\n".Length + at);
        await page.Keyboard.TypeAsync("\"");
        Assert.Equal("firewall_policies:\n- id: x\n" + expected + "\n", await DocAsync(page));
    }

    [BrowserTheory]
    [InlineData("  name: |", "  name: \"\"")]
    [InlineData("  name|", "  name\"")]
    public async Task AQuote_WithNoValueToWrap_TypesAsUsual(string marked, string expected)
    {
        var at = marked.IndexOf('|');
        var page = await OpenAsync("firewall_policies:\n- id: x\n" + marked.Remove(at, 1) + "\n", Schema());
        await CursorAsync(page, "firewall_policies:\n- id: x\n".Length + at);
        await page.Keyboard.TypeAsync("\"");
        Assert.Equal("firewall_policies:\n- id: x\n" + expected + "\n", await DocAsync(page));
    }

    [BrowserFact]
    public async Task ChangingTheMatchingTarget_AddsItsFields_AndRemovesTheOtherTypes()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        var from = await PosOfAsync(page, "matching_target: IP");
        // As picking NETWORK from the list does: one change of the whole value.
        await page.EvaluateAsync($"{View}.dispatch({{ changes: {{ from: {from - 2}, to: {from}, insert: 'NETWORK' }}, userEvent: 'input.complete' }})");
        await page.WaitForTimeoutAsync(100);
        var doc = await DocAsync(page);
        Assert.Contains("    matching_target: NETWORK\n    network_ids: []\n    match_opposite_networks: false\n", doc);
        Assert.DoesNotContain("ips:", doc);
        Assert.DoesNotContain("192.0.2.1", doc);
    }

    [BrowserFact]
    public async Task AnAnyTarget_DropsASpecificMatchType()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        var from = await PosOfAsync(page, "matching_target: IP");
        await page.EvaluateAsync($"{View}.dispatch({{ changes: {{ from: {from - 2}, to: {from}, insert: 'ANY' }}, userEvent: 'input.complete' }})");
        await page.WaitForTimeoutAsync(100);
        var source = (await DocAsync(page)).Split("  destination:")[0];
        Assert.Contains("    matching_target: ANY\n", source);
        Assert.DoesNotContain("matching_target_type", source);
        Assert.DoesNotContain("ips:", source);
    }

    // ---------- Completion ----------

    [BrowserFact]
    public async Task AKeyAndASpace_OpenItsValues()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        await CursorAsync(page, await PosOfAsync(page, "  enabled: true"));
        await page.Keyboard.PressAsync("Enter");
        await page.Keyboard.TypeAsync("action: ");
        await page.WaitForSelectorAsync(".cm-tooltip-autocomplete");
        Assert.Equal(["ALLOW", "BLOCK", "REJECT"], await TextsAsync(page, ".cm-tooltip-autocomplete .cm-completionLabel"));
    }

    [BrowserFact]
    public async Task ClickingAValueWithChoices_OpensThem_WithTheCurrentOneHighlighted()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        await ClickPosAsync(page, await PosOfAsync(page, "    zone: External", atStart: false) - 3);
        await page.WaitForSelectorAsync(".cm-tooltip-autocomplete");
        Assert.Equal(["Internal", "External", "Gateway", "Hotspot"], await TextsAsync(page, ".cm-tooltip-autocomplete .cm-completionLabel"));
        Assert.Equal("External", await page.TextContentAsync(".cm-tooltip-autocomplete li[aria-selected] .cm-completionLabel"));
    }

    [BrowserFact]
    public async Task Enter_OnAClickOpenedList_IsALineBreak()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        var end = await PosOfAsync(page, "    zone: External");
        await ClickPosAsync(page, end, dx: 40);
        await page.WaitForSelectorAsync(".cm-tooltip-autocomplete li[aria-selected]:has-text('External')");
        await page.WaitForTimeoutAsync(CompletionDelayMs);
        await page.Keyboard.PressAsync("Enter");
        Assert.False(await CompletionOpenAsync(page));
        var doc = await DocAsync(page);
        Assert.Equal("  destination:\n    zone: External\n    \n    matching_target: ANY\n", doc[doc.IndexOf("  destination:")..doc.IndexOf("    port_matching_type: ANY", doc.IndexOf("  destination:"))]);
    }

    [BrowserFact]
    public async Task AnArrowThenEnter_OnAClickOpenedList_ReplacesTheWholeValue()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        // Clicked mid-word: the pick still replaces all of "External", not the part after the click.
        await ClickPosAsync(page, await PosOfAsync(page, "    zone: Ext"));
        // The current value is highlighted a moment after the list opens; an arrow moves on from it.
        await page.WaitForSelectorAsync(".cm-tooltip-autocomplete li[aria-selected]:has-text('External')");
        await page.WaitForTimeoutAsync(CompletionDelayMs);
        await page.Keyboard.PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("Enter");
        Assert.Contains("    zone: Gateway\n", await DocAsync(page));
    }

    [BrowserFact]
    public async Task ASecretField_OffersTheGenerators()
    {
        var page = await OpenAsync("wlans:\n- name: Home\n  x_passphrase: \n", Schema());
        await CursorAsync(page, await PosOfAsync(page, "x_passphrase: "));
        await page.EvaluateAsync($"cm.startCompletion({View})");
        await page.WaitForSelectorAsync(".cm-tooltip-autocomplete");
        Assert.Equal(["Generate passphrase", "Generate random"], await TextsAsync(page, ".cm-tooltip-autocomplete .cm-completionLabel"));
        await page.WaitForTimeoutAsync(CompletionDelayMs);
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForTimeoutAsync(100);
        Assert.Contains("x_passphrase: generated-Passphrase\n", await DocAsync(page));
    }

    // ---------- Secrets ----------

    [BrowserFact]
    public async Task AMaskedSecret_ShowsAsAChip_AndShowRevealsItOnItsLine()
    {
        var page = await OpenAsync("wlans:\n- name: Home\n  x_passphrase: <secret:wlans/1/x_passphrase>\n", Schema());
        Assert.Equal(1, await page.EvaluateAsync<int>("document.querySelectorAll('.cc-secret').length"));
        Assert.DoesNotContain("<secret:", await page.TextContentAsync(".cm-content"));
        await page.ClickAsync(".cc-secret button");
        await page.WaitForTimeoutAsync(100);
        Assert.Equal("<secret:wlans/1/x_passphrase>", await page.EvaluateAsync<string>("window.calls.find(c => c.name === 'RevealSecret').args[0]"));
        Assert.Contains("x_passphrase: hunter2-revealed", await page.TextContentAsync(".cm-content"));
    }

    [BrowserFact]
    public async Task ARevealedSecret_LeftUnedited_IsNoEdit()
    {
        var page = await OpenAsync("wlans:\n- name: Home\n  x_passphrase: <secret:wlans/1/x_passphrase>\n", Schema());
        await page.ClickAsync(".cc-secret button");
        await page.WaitForTimeoutAsync(400);
        // The page is told the token, never the value shown, while the reveal is unedited.
        var reported = await LastChangeAsync(page);
        Assert.NotNull(reported);
        Assert.Contains("<secret:wlans/1/x_passphrase>", reported);
        Assert.DoesNotContain("hunter2", reported);
        // Leaving the line puts the token back.
        await CursorAsync(page, 0);
        await page.WaitForTimeoutAsync(100);
        Assert.Equal("wlans:\n- name: Home\n  x_passphrase: <secret:wlans/1/x_passphrase>\n", await DocAsync(page));
    }

    [BrowserFact]
    public async Task ASecretInClear_IsMaskedOffTheCursorsLine()
    {
        var page = await OpenAsync("wlans:\n- name: Home\n  x_passphrase: correct-horse\n", Schema());
        await CursorAsync(page, 0);
        Assert.DoesNotContain("correct-horse", await page.TextContentAsync(".cm-content"));
        await CursorAsync(page, await PosOfAsync(page, "correct-horse"));
        Assert.Contains("correct-horse", await page.TextContentAsync(".cm-content"));
    }

    // ---------- Problems ----------

    [BrowserFact]
    public async Task SetProblems_MarksTheirLines()
    {
        var page = await OpenAsync(Firewall(Policy(0)), Schema());
        await page.EvaluateAsync("""
            cc.setProblems(document.getElementById('host'), [
                { line: 3, level: 'Error', message: 'Bad name' },
                { line: 4, level: 'Warning', message: 'Odd action' },
                { line: 999, level: 'Error', message: 'Past the end' },
            ])
            """);
        await page.WaitForTimeoutAsync(100);
        Assert.Equal(1, await page.EvaluateAsync<int>("document.querySelectorAll('.cm-lintRange-error').length"));
        Assert.Equal(1, await page.EvaluateAsync<int>("document.querySelectorAll('.cm-lintRange-warning').length"));
    }

    [BrowserFact]
    public async Task GoToLine_OpensTheFoldOverIt_AndPutsTheCursorThere()
    {
        var page = await OpenAsync(Firewall(Policy(0), Policy(1)), Schema(), tall: false, height: 900);
        await page.ClickAsync(".cc-collapse-children >> nth=0");
        var line = await page.EvaluateAsync<int>($"{View}.state.doc.lineAt({await PosOfAsync(page, "zone: Internal", nth: 1)}).number");
        await page.EvaluateAsync($"cc.goToLine(document.getElementById('host'), {line})");
        await page.WaitForTimeoutAsync(150);
        Assert.Equal(1, await page.EvaluateAsync<int>(FoldCount));
        Assert.Equal(await PosOfAsync(page, "zone: Internal", atStart: true, nth: 1),
            await page.EvaluateAsync<int>($"{View}.state.selection.main.head"));
        Assert.True(await page.EvaluateAsync<bool>($"{View}.hasFocus"));
    }

    // ---------- Performance ----------

    [BrowserFact]
    public async Task ALargeCollection_ScrolledToItsEnd_SwapsInWellUnderASecond()
    {
        // Each visible line's schema lookup walked back to the collection's key: 10 s per swap on a
        // site's Firewall Policies scrolled to its end, where an upload restores the scroll.
        // A real policy's shape: mostly "key: value" lines, each one a lookup.
        var sb = new StringBuilder("firewall_policies:\n");
        for (var i = 0; i < 290; i++)
        {
            sb.Append($"- id: {Id(i)}\n  name: \"Policy {i}\"\n  action: BLOCK\n  source:\n    zone: Internal\n")
              .Append("    matching_target: IP\n    ips:\n      - 192.0.2.1\n  destination:\n    zone: External\n")
              .Append("    matching_target: ANY\n    port_matching_type: ANY\n");
            for (var k = 0; k < 22; k++) sb.Append($"  extra_{k}: v\n");
        }
        var text = sb.ToString();
        var schema = """{"collections":{"firewall_policies":{"fields":[{"name":"name","secret":true},{"name":"action"},{"name":"source","shape":{"fields":[{"name":"zone"}]}}]}}}""";
        var page = await OpenAsync(text, schema, tall: false, height: 1000);
        Assert.True(await page.EvaluateAsync<int>($"{View}.state.doc.lines") > 9000);
        await page.EvaluateAsync($"{View}.scrollDOM.scrollTop = {View}.scrollDOM.scrollHeight");
        await page.WaitForTimeoutAsync(200);

        // As an upload does it: the spinner hides the editor for at least a frame, then it is shown
        // with the new text. Shown again after hiding, it checks every line; the old lookup took 5 s.
        var upload = text.Replace("action: BLOCK", "action: REJECT");
        await page.EvaluateAsync("([t]) => cc.setText(document.getElementById('host'), t, '2|Firewall Policies')", new object[] { upload });
        await page.WaitForTimeoutAsync(100);
        var ms = await page.EvaluateAsync<int>("""
            async ([t]) => {
                const host = document.getElementById('host');
                const frame = () => new Promise(r => requestAnimationFrame(() => setTimeout(r, 0)));
                host.classList.add('is-loading');
                await frame();
                const start = performance.now();
                host.classList.remove('is-loading');
                cc.setText(host, t, '3|Firewall Policies');
                await frame(); await frame(); await frame();
                return Math.round(performance.now() - start);
            }
            """, new object[] { text });
        Assert.True(ms < 1500, $"Showing the editor again with the swapped text took {ms} ms");
    }

    // ---------- Pure helpers ----------

    [BrowserFact]
    public async Task QuotedEndAndStart_FindTheValueToWrap()
    {
        var page = await OpenAsync("x: y\n");
        var results = await page.EvaluateAsync<string[]>("""
            (() => {
                const at = (s, f) => { const p = s.indexOf('|'); const text = s.replace('|', ''); const r = f({ text, from: 0 }, p); return r === null ? 'null' : String(r); };
                return [
                    at('  name: |Diag Ping', cc.quotedEnd),
                    at('  ips: [|a, b]', cc.quotedEnd),
                    at('  name: |"x"', cc.quotedEnd),
                    at('  name: Diag Ping|', cc.quotedStart),
                    at('  name|', cc.quotedStart),
                    at('  ips: [a, [b]|]', cc.quotedStart),
                ];
            })()
            """);
        Assert.Equal(["17", "9", "null", "8", "null", "null"], results);
    }
}


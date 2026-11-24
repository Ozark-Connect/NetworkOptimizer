using Microsoft.Playwright;
using Xunit;

namespace NetworkOptimizer.Web.BrowserTests;

/// <summary>
/// Settings' unsaved-changes prompt and floating Save result (wwwroot/js/settings-section-save.js), driven in a real
/// browser. A fake card stands in for a Blazor one: its Save either goes busy and re-renders its message (a save that
/// waits on the server), or changes nothing when the outcome repeats (a save that finishes in one render). Every card
/// carries a standing warning above its Save row, as Satellite Imagery does, which is never a save's result.
/// </summary>
[Collection(BrowserCollection.Name)]
public class SettingsSectionSaveTests
{
    private const string Origin = "http://settings-save.test";
    private readonly BrowserFixture _fx;

    public SettingsSectionSaveTests(BrowserFixture fx) => _fx = fx;

    // The card's Save calls window.serverSave, which the test sets: { busy, cls, text } decides the next render.
    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="css/app.css"></head><body>
        <div class="settings-container">
          <div class="card" id="c"><div class="card-header"><h2 class="card-title">Card</h2></div><div class="card-body">
            <div class="alert alert-warning">Non-commercial use only.</div>
            <input id="field" class="form-control">
            <div class="form-group" data-autosave><input id="side" class="form-control"><button id="side-commit" class="btn btn-primary">Activate</button></div>
            <div style="height:2500px"></div>
            <div class="action-buttons"><button id="save" class="btn btn-primary">Save</button></div>
            <div id="msg"></div>
          </div></div>
        </div>
        <script type="module">
          import * as m from './js/settings-section-save.js';
          window.ss = m;
          window.asked = [];
          window.confirm = t => { window.asked.push(t); return false; };
          const save = document.getElementById('save'), msg = document.getElementById('msg');
          let el = null;
          window.show = (cls, text) => {
            if (!cls) { el?.remove(); el = null; return; }
            if (!el) { el = document.createElement('div'); msg.appendChild(el); }
            if (el.className !== `alert alert-${cls}`) el.className = `alert alert-${cls}`;
            if (el.textContent !== text) el.textContent = text;
          };
          save.addEventListener('click', () => {
            const s = window.serverSave;
            if (s.busy) {
              setTimeout(() => { save.disabled = true; window.show(null); }, 20);
              setTimeout(() => { save.disabled = false; window.show(s.cls, s.text); }, 120);
            } else {
              setTimeout(() => window.show(s.cls, s.text), 30);   // one render: identical state changes nothing
            }
          });
          m.mount(document.querySelector('.settings-container'));
          window.ready = true;
        </script></body></html>
        """;

    private async Task<IPage> OpenAsync()
    {
        var page = await _fx.Browser!.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 800 } });
        await page.RouteAsync($"{Origin}/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath.TrimStart('/');
            if (path == "page.html") { await route.FulfillAsync(new RouteFulfillOptions { Body = Page, ContentType = "text/html" }); return; }
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
        return page;
    }

    private static async Task EditAsync(IPage page, string id = "#field")
    {
        await page.FillAsync(id, Guid.NewGuid().ToString("N")[..6]);
        await page.WaitForTimeoutAsync(50);
    }

    private static Task ServerAsync(IPage page, bool busy, string? cls, string? text) =>
        page.EvaluateAsync("([b, c, t]) => window.serverSave = { busy: b, cls: c, text: t }", new object?[] { busy, cls, text });

    /// <summary>True when leaving Settings would ask first.</summary>
    private static Task<bool> PromptsAsync(IPage page) =>
        page.EvaluateAsync<bool>("() => { window.asked = []; window.ss.confirmLeave('/dashboard'); return window.asked.length > 0; }");

    private static async Task ScrollCardTopAsync(IPage page)
    {
        await page.EvaluateAsync("() => { document.getElementById('c').scrollIntoView({ block: 'start' }); window.dispatchEvent(new Event('scroll')); }");
        await page.WaitForTimeoutAsync(200);
    }

    /// <summary>Clicks the floating Save and returns the icon it shows within 3 s ("success", "error", "warning"), or "none".</summary>
    private static async Task<string> FloatSaveAsync(IPage page)
    {
        await ScrollCardTopAsync(page);
        await page.EvaluateAsync("() => document.querySelectorAll('.settings-floating-result').forEach(r => r.hidden = true)");
        await page.ClickAsync(".settings-floating-save");
        for (var waited = 0; waited < 3000; waited += 25)
        {
            var kind = await page.EvaluateAsync<string?>("() => { const r = document.querySelector('.settings-floating-result'); return r && !r.hidden ? r.className.replace('settings-floating-result is-', '') : null; }");
            if (kind != null) return kind;
            await page.WaitForTimeoutAsync(25);
        }
        return "none";
    }

    [BrowserTheory]
    [InlineData(true, "success", "Saved.", false)]
    [InlineData(true, "danger", "Could not reach InfluxDB.", true)]
    [InlineData(true, "warning", "Channel name is required.", true)]
    [InlineData(true, "warning", "Saved, but InfluxDB is unreachable.", false)]
    [InlineData(true, null, null, false)]
    public async Task SaveOutcome_DecidesTheLeavePrompt(bool busy, string? cls, string? text, bool prompts)
    {
        var page = await OpenAsync();
        await EditAsync(page);
        await ServerAsync(page, busy, cls, text);
        await page.ClickAsync("#save");
        await page.WaitForTimeoutAsync(1800);
        Assert.Equal(prompts, await PromptsAsync(page));
    }

    [BrowserFact]
    public async Task EditDuringSave_StaysUnsaved()
    {
        var page = await OpenAsync();
        await EditAsync(page);
        await ServerAsync(page, true, "success", "Saved.");
        await page.ClickAsync("#save");
        await page.WaitForTimeoutAsync(60);
        await EditAsync(page);
        await page.WaitForTimeoutAsync(400);
        Assert.True(await PromptsAsync(page));
    }

    [BrowserFact]
    public async Task FieldCommittedByItsOwnButton_IsNotAnUnsavedEdit()
    {
        var page = await OpenAsync();
        await EditAsync(page, "#side");
        await page.ClickAsync("#side-commit");
        Assert.False(await PromptsAsync(page));
    }

    [BrowserFact]
    public async Task FloatingSave_ShowsEachRepeatSavesResult_PastAStandingWarning()
    {
        var page = await OpenAsync();
        var seen = new List<string>();
        // One-render saves: the second and third success, and the repeated failure, change nothing on the page.
        foreach (var (cls, text) in new[] { ("danger", "Fix the errors before saving."), ("danger", "Fix the errors before saving."),
                     ("success", "Saved."), ("success", "Saved."), ("success", "Saved.") })
        {
            await EditAsync(page);
            await ServerAsync(page, false, cls, text);
            seen.Add(await FloatSaveAsync(page) + (await PromptsAsync(page) ? "+prompt" : ""));
        }
        Assert.Equal(["error+prompt", "error+prompt", "success", "success", "success"], seen);
    }

    [BrowserFact]
    public async Task FloatingSave_BusySave_ShowsItsResultAtOnce()
    {
        var page = await OpenAsync();
        await EditAsync(page);
        await ServerAsync(page, true, "success", "Saved.");
        Assert.Equal("success", await FloatSaveAsync(page));
        Assert.False(await PromptsAsync(page));
    }
}

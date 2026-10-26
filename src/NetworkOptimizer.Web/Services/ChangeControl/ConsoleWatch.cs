using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// The console-level side of Change Control (spec "Console backup and console settings"): keeps
/// copies of UniFi's own Network app backups (.unf), encrypted, and diffs the security-relevant
/// console settings (admins, auto-update, auto-backup). Console settings are never restored.
/// </summary>
public sealed class ConsoleWatch
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    /// <summary>Name prefix for a copied backup in the file store, which takes plain file names only (no folders).</summary>
    internal const string BackupPrefix = "console-backup_";
    private const string SettingsFile = "console-settings.json";

    private readonly ChangeControlSiteEngine _engine;
    private readonly IConfigBlobStore _blobs;
    private readonly IAlertEventBus? _bus;
    private readonly ILogger _logger;
    private DateTime _last = DateTime.MinValue;

    public ConsoleWatch(ChangeControlSiteEngine engine, IConfigBlobStore blobs, IAlertEventBus? bus, ILogger logger)
    {
        _engine = engine;
        _blobs = blobs;
        _bus = bus;
        _logger = logger;
    }

    private string Slug => _engine.SiteSlug;

    /// <summary>One pass, at most hourly: copy new backups, thin old ones, diff console settings.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _last < Interval || !_engine.Api.IsConnected) return;
        _last = DateTime.UtcNow;
        try
        {
            await CopyBackupsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Console backup copy failed for site {Site}", Slug);
        }
        try
        {
            await DiffConsoleSettingsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Console settings check failed for site {Site}", Slug);
        }
    }

    /// <summary>A backup entry from cmd/backup list-backups (filename, datetime, version, type: UniFi's own backup settings page).</summary>
    internal sealed record BackupEntry(string FileName, DateTime TakenAt, string? Version);

    [NetworkOptimizer.Core.VendorSpecific("UniFi", "cmd/backup list-backups entries")]
    internal static List<BackupEntry> ParseBackups(string body)
    {
        var result = new List<BackupEntry>();
        if (ConfigSnapshotReader.ExtractList(body) is not { } list) return result;
        foreach (var b in list)
        {
            // Cloud backups live on UniFi's servers and download from a different path.
            if (b["type"]?.ToString() == "cloud") continue;
            if (b["filename"]?.ToString() is not { Length: > 0 } name || name.Contains('/') || name.Contains("..")) continue;
            var taken = DateTime.TryParse(b["datetime"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t : DateTime.UtcNow;
            result.Add(new BackupEntry(name, taken, b["version"]?.ToString()));
        }
        return result;
    }

    private async Task CopyBackupsAsync(CancellationToken ct)
    {
        var listed = await _engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Post, "cmd/backup", "{\"cmd\":\"list-backups\"}", ct);
        if (!listed.IsSuccess) return;
        var entries = ParseBackups(listed.Body);
        var settings = await _engine.Repositories.UseAsync((r, c) => r.GetSettingsAsync(c), ct);
        var (records, rolloutPins) = await _engine.Repositories.UseAsync(async (r, c) =>
            (await r.GetConsoleBackupsAsync(c),
             (await r.GetSnapshotsAsync(limit: 500, cancellationToken: c)).Where(s => s.Trigger == ConfigSnapshotTrigger.BeforeFirmwareRollout).Select(s => s.TakenAt).ToList()), ct);
        var known = records.Select(r => r.FileName).ToHashSet(StringComparer.Ordinal);

        // Only what the keep count would retain is worth downloading.
        foreach (var e in entries.Where(e => !known.Contains(e.FileName)).OrderByDescending(e => e.TakenAt).Take(Math.Max(settings.ConsoleBackupsToKeep, 1) + 2))
        {
            var (status, bytes) = await _engine.Api.GetBytesAsync(UniFiApiScope.NetworkApp, $"dl/autobackup/{Uri.EscapeDataString(e.FileName)}", ct);
            if (status is < 200 or >= 300 || bytes.Length == 0) continue;
            var sha = await _blobs.PutFileAsync(Slug, BackupPrefix + e.FileName, bytes, ct);
            var record = new ConsoleBackupRecord
            {
                FileName = e.FileName, TakenAt = e.TakenAt, FetchedAt = DateTime.UtcNow, SizeBytes = bytes.Length, Sha256 = sha,
                NetworkAppVersion = e.Version,
                FirmwareRollout = rolloutPins.Any(p => e.TakenAt >= p.AddMinutes(-5) && e.TakenAt - p < TimeSpan.FromMinutes(30)),
            };
            await _engine.Repositories.UseAsync((r, c) => r.AddConsoleBackupAsync(record, c), ct);
            records.Add(record);
        }

        var thin = Thin(records, settings.ConsoleBackupsToKeep);
        if (thin.Count == 0) return;
        foreach (var r in thin) _blobs.DeleteFile(Slug, BackupPrefix + r.FileName);
        await _engine.Repositories.UseAsync((r, c) => r.DeleteConsoleBackupsAsync(thin.Select(x => x.Id).ToList(), c), ct);
    }

    /// <summary>Backups past the keep count; ones Firmware Rollout triggered are never counted or thinned.</summary>
    internal static List<ConsoleBackupRecord> Thin(IEnumerable<ConsoleBackupRecord> records, int keep) =>
        records.Where(r => !r.FirmwareRollout && r.Id != 0).OrderByDescending(r => r.TakenAt).Skip(Math.Max(keep, 0)).ToList();

    /// <summary>The security-relevant console settings, from GET /api/system and the console users list.</summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "console /api/system and /proxy/users/api/v2/users")]
    internal static JsonObject ConsoleSettings(string? systemBody, string? usersBody)
    {
        var s = new JsonObject();
        if (systemBody != null && JsonNode.Parse(systemBody) is JsonObject sys)
        {
            s["auto_backup"] = sys["autoBackupEnabled"]?.DeepClone();
            s["firmware_channel"] = sys["firmware"]?["releaseChannel"]?.DeepClone();
            s["auto_update"] = sys["firmware"]?["autoUpdate"]?.DeepClone();
            if (sys["apps"]?["controllers"] is JsonArray apps)
                s["app_updates"] = new JsonObject(apps.OfType<JsonObject>().Where(a => a["name"] != null)
                    .Select(a => KeyValuePair.Create(a["name"]!.ToString(), (JsonNode?)new JsonObject
                    {
                        ["channel"] = a["releaseChannel"]?.DeepClone(),
                        ["schedule"] = a["updateSchedule"]?.DeepClone(),
                    })));
        }
        if (usersBody != null && JsonNode.Parse(usersBody) is { } users)
        {
            var list = users is JsonObject o && o["data"] is JsonArray d ? d : users as JsonArray ?? [];
            var admins = new JsonObject();
            foreach (var u in list.OfType<JsonObject>())
            {
                var roles = (u["roles"] as JsonArray ?? []).OfType<JsonObject>().Select(r => r["system_key"]?.ToString()).OfType<string>().OrderBy(r => r).ToList();
                if (roles.Count == 0) continue;
                var name = u["full_name"]?.ToString() ?? u["username"]?.ToString() ?? "";
                var id = u["id"]?.ToString() ?? u["unique_id"]?.ToString() ?? name;
                admins[id] = new JsonObject
                {
                    ["name"] = name,
                    ["roles"] = new JsonArray(roles.Select(r => (JsonNode?)r).ToArray()),
                    ["status"] = u["status"]?.DeepClone(),
                };
            }
            s["admins"] = admins;
        }
        return s;
    }

    /// <summary>Plain lines for what changed between two console settings reads.</summary>
    internal static List<string> Describe(JsonObject before, JsonObject after)
    {
        var lines = new List<string>();
        var a0 = before["admins"] as JsonObject ?? [];
        var a1 = after["admins"] as JsonObject ?? [];
        foreach (var (id, u) in a1.Where(p => !a0.ContainsKey(p.Key)))
            lines.Add($"Console admin added: {u?["name"]} ({string.Join(", ", (u?["roles"] as JsonArray ?? []).Select(r => r?.ToString()))})");
        foreach (var (id, u) in a0.Where(p => !a1.ContainsKey(p.Key)))
            lines.Add($"Console admin removed: {u?["name"]}");
        foreach (var (id, u) in a1.Where(p => a0.ContainsKey(p.Key)))
            if (!JsonNode.DeepEquals(u?["roles"], a0[id]?["roles"]))
                lines.Add($"Console admin {u?["name"]}: roles changed to {string.Join(", ", (u?["roles"] as JsonArray ?? []).Select(r => r?.ToString()))}");
            else if (!JsonNode.DeepEquals(u?["status"], a0[id]?["status"]))
                lines.Add($"Console admin {u?["name"]}: status {a0[id]?["status"]} to {u?["status"]}");

        void Field(string key, string label)
        {
            if (!JsonNode.DeepEquals(before[key], after[key]))
                lines.Add($"{label}: {Show(before[key])} to {Show(after[key])}");
        }
        Field("auto_backup", "Console auto-backup");
        Field("auto_update", "UniFi OS auto-update");
        Field("firmware_channel", "UniFi OS update channel");
        Field("app_updates", "Application update channels or schedules");
        return lines;
    }

    private static string Show(JsonNode? n) => n switch
    {
        null => "not set",
        JsonValue v => v.ToString(),
        _ => n.ToJsonString(),
    };

    private async Task DiffConsoleSettingsAsync(CancellationToken ct)
    {
        var system = await _engine.Api.SendAsync(UniFiApiScope.Console, HttpMethod.Get, "api/system", null, ct);
        var users = await _engine.Api.SendAsync(UniFiApiScope.Console, HttpMethod.Get, "proxy/users/api/v2/users", null, ct);
        if (!system.IsSuccess && !users.IsSuccess) return;
        var current = ConsoleSettings(system.IsSuccess ? system.Body : null, users.IsSuccess ? users.Body : null);
        // A read that failed this time keeps last time's value for that part, so a 401 is not "every admin removed".
        var previousBytes = await _blobs.GetFileAsync(Slug, SettingsFile, ct);
        var previous = previousBytes == null ? null : JsonNode.Parse(Encoding.UTF8.GetString(previousBytes)) as JsonObject;
        if (previous != null)
            foreach (var (k, v) in previous.Where(p => !current.ContainsKey(p.Key)).ToList())
                current[k] = v?.DeepClone();

        var currentText = CanonicalJson.Serialize(current);
        if (previous != null && CanonicalJson.Serialize(previous) == currentText) return;
        await _blobs.PutFileAsync(Slug, SettingsFile, Encoding.UTF8.GetBytes(currentText), ct);
        if (previous == null) return;

        var lines = Describe(previous, current);
        if (lines.Count == 0) return;
        var change = new ConfigChange
        {
            Kind = ConfigChangeKind.ConsoleSettings,
            OccurredAt = DateTime.UtcNow,
            DetectedAt = DateTime.UtcNow,
            ActorKind = ConfigChangeActorKind.NoAdminRecorded,
            Summary = string.Join("; ", lines).Length > 500 ? string.Join("; ", lines)[..497] + "..." : string.Join("; ", lines),
            Evaluated = true,
        };
        change = await _engine.Repositories.UseAsync((r, c) => r.AddChangeAsync(change, [], c), ct);
        if (_bus != null)
            await _bus.PublishAsync(new AlertEvent
            {
                EventType = ChangeAlerts.ConsoleSettings,
                Source = ChangeAlerts.Source,
                Severity = lines.Any(l => l.StartsWith("Console admin", StringComparison.Ordinal)) ? AlertSeverity.Warning : AlertSeverity.Info,
                Title = $"UniFi Console settings changed: {lines[0]}",
                Message = string.Join(". ", lines) + ".",
                Context = new Dictionary<string, string> { ["change_id"] = change.Id.ToString() },
                SourceUrl = ChangeAlerts.ChangeUrl(change.Id),
                SiteSlug = Slug,
            }, ct);
    }
}

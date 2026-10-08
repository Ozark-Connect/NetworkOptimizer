using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>A point-in-time copy of a site's channel apply run, for the panel.</summary>
public sealed record ChannelApplyRunSnapshot(
    RadioBand Band,
    IReadOnlyList<ChannelApplyItem> Items,
    IReadOnlyList<int> ItemWaves,
    IReadOnlyList<ChannelApplyOutcome> NotApplied,
    IReadOnlyList<ChannelApplyOutcome> Outcomes,
    int WaveCount,
    int CurrentWave,
    bool Running,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc);

/// <summary>
/// Runs channel applies on the server, one run per site, so a run finishes whether or not anyone
/// keeps the page open. Each wave is read, written, and then watched until every AP in it reports
/// its new channel, before the next wave starts. Not gated: only <see cref="IChannelPlanApplyService"/>
/// calls it.
/// </summary>
public sealed class ChannelPlanApplyRunner
{
    /// <summary>An AP reports the written config about 7 s after the PUT (measured on a U7).</summary>
    private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    /// <summary>Covers a 60 s DFS channel availability check plus provisioning.</summary>
    private static readonly TimeSpan ArrivalDeadline = TimeSpan.FromSeconds(90);

    /// <summary>A finished run stays readable this long, so the panel can show it after a reload.</summary>
    private static readonly TimeSpan KeepFinishedFor = TimeSpan.FromHours(1);

    private sealed class Run
    {
        public required RadioBand Band { get; init; }
        public required List<List<ChannelApplyItem>> Waves { get; init; }
        public required List<ChannelApplyOutcome> NotApplied { get; init; }
        public List<ChannelApplyOutcome> Outcomes { get; } = new();
        public int CurrentWave;
        public bool Running = true;
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public DateTime? FinishedAtUtc;
    }

    private readonly Dictionary<string, Run> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly SiteConnectionRegistry _connections;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ChannelPlanApplyRunner> _logger;

    /// <param name="connections">Every site's UniFi Network connection.</param>
    /// <param name="lifetime">A run stops when the app shuts down.</param>
    /// <param name="logger">Logger.</param>
    public ChannelPlanApplyRunner(
        SiteConnectionRegistry connections, IHostApplicationLifetime lifetime, ILogger<ChannelPlanApplyRunner> logger)
    {
        _connections = connections;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Starts a run unless one is already running on the site.</summary>
    public bool TryStart(string siteSlug, RadioBand band, List<List<ChannelApplyItem>> waves, List<ChannelApplyOutcome> notApplied)
    {
        Run run;
        lock (_lock)
        {
            if (_runs.TryGetValue(siteSlug, out var existing) && existing.Running) return false;
            run = new Run { Band = band, Waves = waves, NotApplied = notApplied };
            _runs[siteSlug] = run;
        }
        _ = Task.Run(() => ExecuteAsync(siteSlug, run, _lifetime.ApplicationStopping));
        return true;
    }

    /// <summary>The site's current or most recent run, or null.</summary>
    public ChannelApplyRunSnapshot? Get(string siteSlug)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(siteSlug, out var run)) return null;
            if (!run.Running && run.FinishedAtUtc < DateTime.UtcNow - KeepFinishedFor)
            {
                _runs.Remove(siteSlug);
                return null;
            }
            var items = run.Waves.SelectMany(w => w).ToList();
            var itemWaves = run.Waves.SelectMany((w, i) => w.Select(_ => i)).ToList();
            return new ChannelApplyRunSnapshot(run.Band, items, itemWaves, run.NotApplied.ToList(),
                run.Outcomes.ToList(), run.Waves.Count, run.CurrentWave, run.Running, run.StartedAtUtc, run.FinishedAtUtc);
        }
    }

    /// <summary>Forgets a finished run. A running one is kept.</summary>
    public void Dismiss(string siteSlug)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(siteSlug, out var run) && !run.Running) _runs.Remove(siteSlug);
        }
    }

    private void Record(Run run, ChannelApplyOutcome outcome)
    {
        lock (_lock) run.Outcomes.Add(outcome);
    }

    private async Task ExecuteAsync(string siteSlug, Run run, CancellationToken ct)
    {
        string? stopReason = null;
        try
        {
            var connection = _connections.GetFor(siteSlug);
            var client = connection.IsConnected ? connection.Client : null;
            if (client == null) stopReason = "Not connected to UniFi Network";

            for (var w = 0; w < run.Waves.Count; w++)
            {
                lock (_lock) run.CurrentWave = w;
                var wave = run.Waves[w];
                if (stopReason != null || ct.IsCancellationRequested)
                {
                    foreach (var item in wave)
                        Record(run, new(item, ChannelApplyStatus.Skipped, stopReason ?? "Network Optimizer restarted"));
                    continue;
                }
                stopReason = await ApplyWaveAsync(siteSlug, client!, run, wave, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Channel apply run failed (site {Site})", siteSlug);
            lock (_lock)
            {
                var done = run.Outcomes.Select(o => o.Item).ToHashSet();
                foreach (var item in run.Waves.SelectMany(w => w).Where(i => !done.Contains(i)))
                    run.Outcomes.Add(new(item, ChannelApplyStatus.Failed, "UniFi Network could not be reached"));
            }
        }
        finally
        {
            lock (_lock)
            {
                run.Running = false;
                run.FinishedAtUtc = DateTime.UtcNow;
            }
            _logger.LogInformation("Channel apply finished on {Band}: {Summary} (site {Site})", run.Band,
                string.Join(", ", run.Outcomes.GroupBy(o => o.Status).Select(g => $"{g.Count()} {g.Key}")), siteSlug);
        }
    }

    /// <summary>Applies one wave. Returns why the run must stop, or null to go on.</summary>
    private async Task<string?> ApplyWaveAsync(
        string siteSlug, UniFiApiClient client, Run run, List<ChannelApplyItem> wave, CancellationToken ct)
    {
        // One fresh read of every device per check, whatever the wave size.
        var devices = await ReadDevicesAsync(client, ct);
        if (devices.Count == 0)
        {
            foreach (var item in wave)
                Record(run, new(item, ChannelApplyStatus.Failed, "UniFi Network could not be reached"));
            return "UniFi Network could not be reached";
        }
        // Each written radio, with the config version the AP ran before the write, until the AP reports it.
        var pending = new Dictionary<ChannelApplyItem, string?>();
        foreach (var item in wave)
        {
            var device = devices.GetValueOrDefault(item.ApMac.ToLowerInvariant());
            var (update, stop) = ChannelPlanApply.Preflight(item, device);
            if (stop != null) { Record(run, stop); continue; }

            UniFiDeviceResponse? written;
            try
            {
                written = await client.UpdateDeviceRadioChannelsAsync(device!.Id, [update!], ct);
                if (written == null)
                {
                    Record(run, new(item, ChannelApplyStatus.Failed, "UniFi Network refused the change"));
                    continue;
                }
            }
            catch (UniFiPermissionException ex)
            {
                // The account cannot write at all, so every remaining radio would fail the same way.
                Record(run, new(item, ChannelApplyStatus.Failed, ex.Message));
                foreach (var rest in wave.SkipWhile(i => i != item).Skip(1))
                    Record(run, new(rest, ChannelApplyStatus.Skipped, "Not attempted"));
                return "Not attempted";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per radio, so the radios already written in this wave are still watched to arrival.
                _logger.LogWarning(ex, "Channel apply: write failed for {Ap} (site {Site})", item.ApMac, siteSlug);
                Record(run, new(item, ChannelApplyStatus.Failed, "UniFi Network could not be reached"));
                continue;
            }

            _logger.LogInformation("Channel apply: {Ap} {Band} Ch {From}/{FromW} -> Ch {To}/{ToW} (site {Site})",
                item.ApName, item.Band, item.CurrentChannel, item.CurrentWidth, item.Channel, item.Width, siteSlug);
            pending[item] = device!.CfgVersion;
        }

        // Every change in the wave is saved from here, so a stop or a failed read is Unconfirmed,
        // never Skipped or Failed.
        try
        {
            await Task.Delay(FirstCheckAfter, ct);
            var deadline = DateTime.UtcNow + ArrivalDeadline;
            while (pending.Count > 0)
            {
                devices = await ReadDevicesAsync(client, ct);
                foreach (var (item, cfgVersion) in pending.ToList())
                {
                    if (!ChannelPlanApply.HasArrived(item, devices.GetValueOrDefault(item.ApMac.ToLowerInvariant()), cfgVersion)) continue;
                    Record(run, new(item, ChannelApplyStatus.Applied));
                    pending.Remove(item);
                }
                if (pending.Count == 0) break;
                if (DateTime.UtcNow >= deadline)
                {
                    foreach (var item in pending.Keys)
                        Record(run, new(item, ChannelApplyStatus.Unconfirmed,
                            $"Saved in UniFi Network; the AP had not reported the new channel after {ArrivalDeadline.TotalSeconds:0} seconds"));
                    break;
                }
                await Task.Delay(CheckEvery, ct);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var item in pending.Keys)
                Record(run, new(item, ChannelApplyStatus.Unconfirmed, "Saved in UniFi Network; stopped waiting for the AP"));
            return "Network Optimizer restarted";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Channel apply: could not read the wave back (site {Site})", siteSlug);
            foreach (var item in pending.Keys)
                Record(run, new(item, ChannelApplyStatus.Unconfirmed, "Saved in UniFi Network; could not read the AP back"));
        }
        return null;
    }

    private static async Task<Dictionary<string, UniFiDeviceResponse>> ReadDevicesAsync(UniFiApiClient client, CancellationToken ct) =>
        (await client.GetDevicesAsync(ct, useCache: false))
            .Where(d => !string.IsNullOrEmpty(d.Mac))
            .GroupBy(d => d.Mac.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());
}

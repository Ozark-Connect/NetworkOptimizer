using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Firmware;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>Clock the executor's grace windows, budgets and cool-downs are driven by.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTime start) => _utcNow = new DateTimeOffset(start);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
}

/// <summary>Alert bus that keeps everything published.</summary>
internal sealed class CapturingBus : IAlertEventBus
{
    public List<AlertEvent> Published { get; } = [];

    public ValueTask PublishAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default)
    {
        Published.Add(alertEvent);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<AlertEvent> ConsumeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// Scripted command surface. Every call is recorded so a test can assert which path a step took -
/// console command, arbitrary-image command, or SSH - and each can be told to refuse.
/// </summary>
internal sealed class FakeFirmwareCommandClient : IFirmwareCommandClient
{
    public bool UsesApiKey { get; set; }
    public FirmwareCommandResult UpgradeResult { get; set; } = FirmwareCommandResult.Ok();
    public FirmwareCommandResult ExternalResult { get; set; } = FirmwareCommandResult.Ok();
    public FirmwareCommandResult SshResult { get; set; } = FirmwareCommandResult.Ok();
    public FirmwareCommandResult BackupResult { get; set; } = FirmwareCommandResult.Ok();

    public string DeviceChannel { get; set; } = "release";
    public List<string> AvailableDeviceChannels { get; set; } = ["release", "release-candidate"];

    /// <summary>UniFi's own nightly auto-upgrade; null means the console would not say.</summary>
    public bool? AutoUpgradeEnabled { get; set; }

    // A real console answers with a firmware block even when it has nothing pending. An empty
    // object means "could not be reached", which is what an API-key connection returns.
    // Hardware carries the installed UniFi OS build, which the downgrade guards compare against;
    // a console with none refuses every console update, which is the point of that guard.
    public UniFiConsoleSystemInfo? ConsoleInfo { get; set; } = new()
    {
        Firmware = new UniFiConsoleFirmware(),
        Hardware = new UniFiConsoleHardware { FirmwareVersion = "4.3.5" },
        Apps = new UniFiConsoleApps
        {
            Controllers =
            [
                new UniFiConsoleController
                {
                    Name = UniFiConsoleController.NetworkName,
                    Version = "9.0.0",
                    UpdateAvailable = "9.1.0",
                    ReleaseChannel = "release",
                },
            ],
        },
    };
    public List<UniFiFirmwareCatalogEntry> Catalog { get; } = [];

    /// <summary>What the console offers as a UniFi OS build; null means it is current.</summary>
    public UniFiConsoleFirmwareRelease? PendingUniFiOs { get; set; }

    public bool NetworkAppUpdateAccepted { get; set; } = true;
    public bool UniFiOsUpdateAccepted { get; set; } = true;

    public List<string> UpgradeCommands { get; } = [];
    public List<(string Mac, string Url)> ExternalCommands { get; } = [];
    public List<(string Host, string Url)> SshCommands { get; } = [];
    public List<string> ChannelWrites { get; } = [];

    /// <summary>Console channel PATCHes, in order. Null in a slot means that surface was not written.</summary>
    public List<(string? NetworkApp, string? UniFiOs)> ConsoleChannelWrites { get; } = [];

    /// <summary>Console-level calls in the order they were made, so ordering can be asserted.</summary>
    public List<string> Calls { get; } = [];

    public int CheckForUpdatesCalls { get; private set; }
    public int ApplicationUpdateChecks { get; private set; }
    public int BackupCalls { get; private set; }
    public int NetworkAppUpdateCalls { get; private set; }
    public int UniFiOsUpdateCalls { get; private set; }

    public Task<FirmwareCommandResult> TriggerUpgradeAsync(string deviceMac, CancellationToken cancellationToken = default)
    {
        UpgradeCommands.Add(deviceMac);
        return Task.FromResult(UpgradeResult);
    }

    public Task<FirmwareCommandResult> TriggerExternalUpgradeAsync(string deviceMac, string firmwareUrl, CancellationToken cancellationToken = default)
    {
        ExternalCommands.Add((deviceMac, firmwareUrl));
        return Task.FromResult(ExternalResult);
    }

    public Task<FirmwareCommandResult> TriggerSshUpgradeAsync(string host, string firmwareUrl, CancellationToken cancellationToken = default)
    {
        SshCommands.Add((host, firmwareUrl));
        return Task.FromResult(SshResult);
    }

    public Task<IReadOnlyList<UniFiFirmwareCatalogEntry>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        CheckForUpdatesCalls++;
        return Task.FromResult<IReadOnlyList<UniFiFirmwareCatalogEntry>>(Catalog);
    }

    public Task<string?> GetDeviceChannelAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(DeviceChannel);

    public Task<RolloutChannelAvailability> GetChannelAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new RolloutChannelAvailability
        {
            CurrentDeviceChannel = DeviceChannel,
            AvailableDeviceChannels = AvailableDeviceChannels,
        });

    public Task<bool?> GetAutoUpgradeEnabledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AutoUpgradeEnabled);

    public Task<bool> SetDeviceChannelAsync(string channel, CancellationToken cancellationToken = default)
    {
        ChannelWrites.Add(channel);
        DeviceChannel = channel;
        return Task.FromResult(true);
    }

    public Task<bool> CheckForApplicationUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ApplicationUpdateChecks++;
        Calls.Add("app-update-check");
        return Task.FromResult(true);
    }

    /// <summary>Writes the channels through to the console info, so a later read sees them.</summary>
    public Task<bool> SetConsoleChannelsAsync(string? networkAppChannel, string? unifiOsChannel, CancellationToken cancellationToken = default)
    {
        if (networkAppChannel == null && unifiOsChannel == null)
            return Task.FromResult(true);

        ConsoleChannelWrites.Add((networkAppChannel, unifiOsChannel));
        Calls.Add("console-channels");

        if (networkAppChannel != null && ConsoleInfo?.NetworkApplication != null)
            ConsoleInfo.NetworkApplication.ReleaseChannel = networkAppChannel;
        if (unifiOsChannel != null && ConsoleInfo?.Firmware != null)
            ConsoleInfo.Firmware.ReleaseChannel = unifiOsChannel;

        return Task.FromResult(true);
    }

    public Task<UniFiConsoleSystemInfo?> GetConsoleSystemInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ConsoleInfo);

    public Task<FirmwareCommandResult> TriggerBackupAsync(CancellationToken cancellationToken = default)
    {
        BackupCalls++;
        return Task.FromResult(BackupResult);
    }

    public Task<bool> TriggerNetworkApplicationUpdateAsync(CancellationToken cancellationToken = default)
    {
        NetworkAppUpdateCalls++;
        Calls.Add("network-app-update");
        return Task.FromResult(NetworkAppUpdateAccepted);
    }

    public Task<UniFiConsoleFirmwareRelease?> GetPendingUniFiOsUpdateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PendingUniFiOs);

    public Task<bool> TriggerUniFiOsUpdateAsync(CancellationToken cancellationToken = default)
    {
        UniFiOsUpdateCalls++;
        Calls.Add("unifi-os-update");
        return Task.FromResult(UniFiOsUpdateAccepted);
    }

    public FirmwareCommandResult SshNetworkAppResult { get; set; } = FirmwareCommandResult.Ok();
    public FirmwareCommandResult SshUniFiOsResult { get; set; } = FirmwareCommandResult.Ok();

    public Task<FirmwareCommandResult> TriggerSshNetworkAppUpdateAsync(string debUrl, CancellationToken cancellationToken = default)
    {
        Calls.Add("ssh-network-app-update");
        return Task.FromResult(SshNetworkAppResult);
    }

    public Task<FirmwareCommandResult> TriggerSshUniFiOsUpdateAsync(string firmwareUrl, CancellationToken cancellationToken = default)
    {
        Calls.Add("ssh-unifi-os-update");
        return Task.FromResult(SshUniFiOsResult);
    }
}

/// <summary>A device table the test moves through offline, upgrading and back-online states.</summary>
internal sealed class ScriptedDeviceObserver : IRolloutDeviceObserver
{
    public Dictionary<string, RolloutDeviceObservation> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Simulates a console that answers nothing at all, as during a gateway's own upgrade.</summary>
    public bool ConsoleDark { get; set; }

    public Task<IReadOnlyList<RolloutDeviceObservation>> ObserveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RolloutDeviceObservation>>(
            ConsoleDark ? [] : Devices.Values.ToList());

    public void Set(string mac, int state, string? firmware, string? upgradeTo = null, string? ip = "192.0.2.10", string model = "U6PRO", string name = "AP 1")
    {
        Devices[mac] = new RolloutDeviceObservation
        {
            Mac = mac,
            Name = name,
            Model = model,
            IpAddress = ip,
            Firmware = firmware,
            State = state,
            Upgradable = upgradeTo != null,
            UpgradeToFirmware = upgradeTo,
        };
    }
}

/// <summary>Litmus that answers whatever the test set, per device where it matters.</summary>
internal sealed class FakeLitmusService : IRolloutLitmusService
{
    public RolloutResourceStats Stats { get; set; } = new() { CpuPercent = 10, MemoryUsedPercent = 40, SampleCount = 12 };
    public Dictionary<string, RolloutResourceStats> StatsByMac { get; } = new(StringComparer.OrdinalIgnoreCase);
    public LitmusVerdict Verdict { get; set; } = LitmusVerdict.Pass();
    public Dictionary<string, LitmusVerdict> VerdictByMac { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<RolloutResourceStats> CaptureStatsAsync(string deviceMac, DateTime from, DateTime to, CancellationToken cancellationToken = default) =>
        Task.FromResult(StatsByMac.TryGetValue(deviceMac, out var stats) ? stats : Stats);

    public Task<LitmusVerdict> RunShortLitmusAsync(
        string deviceMac, RolloutResourceStats? preStats, DateTime from, DateTime to, CancellationToken cancellationToken = default) =>
        Task.FromResult(VerdictByMac.TryGetValue(deviceMac, out var verdict) ? verdict : Verdict);
}

/// <summary>Health gate the test flips.</summary>
internal sealed class FakeHealthGate : IRolloutHealthGate
{
    public RolloutHealthVerdict Verdict { get; set; } = RolloutHealthVerdict.Ok();

    public Task<RolloutHealthVerdict> EvaluateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Verdict);
}

/// <summary>Mesh queue that records what it was asked to re-pair.</summary>
internal sealed class RecordingMeshRepairQueue : IMeshRepairQueue
{
    public List<(string? Ip, string? Iface, string? Name)> Enqueued { get; } = [];

    public bool Enqueue(string? childIp, string? iface, string? apName)
    {
        Enqueued.Add((childIp, iface, apName));
        return childIp != null && iface != null && iface.StartsWith("vwiresta", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Hands the orchestrator one in-memory repository. Production opens a site-pinned system scope
/// per call; nothing about the state machine depends on that, so the test collapses it.
/// </summary>
internal sealed class InMemoryRepositoryAccessor : IFirmwareRolloutRepositoryAccessor
{
    private readonly IFirmwareRolloutRepository _repository;

    public InMemoryRepositoryAccessor(IFirmwareRolloutRepository repository) => _repository = repository;

    public Task<T> UseAsync<T>(Func<IFirmwareRolloutRepository, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default) =>
        work(_repository, cancellationToken);

    public Task UseAsync(Func<IFirmwareRolloutRepository, CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
        work(_repository, cancellationToken);
}

/// <summary>
/// The site data a plan is built from, scripted. Keeps the service's tests about plan composition
/// rather than about consoles, floor plans and release feeds.
/// </summary>
internal sealed class FakeRolloutPlanningSource : IRolloutPlanningSource
{
    public List<PlannerDevice> Devices { get; } = [];
    public IApNeighborOracle? Neighbors { get; set; }
    public int ClientCount { get; set; } = 20;
    public bool ConsoleConnected { get; set; } = true;

    public QuietWindowProposal Window { get; set; } = new()
    {
        Day = DayOfWeek.Sunday,
        Hour = 3,
        StartLocal = new DateTime(2026, 8, 16, 3, 0, 0, DateTimeKind.Unspecified),
        // A site far from any plausible test server, so a schedule derived from the server's zone
        // instead of the site's cannot coincidentally match.
        TimeZoneId = "Australia/Sydney",
        StartUtc = new DateTime(2026, 8, 15, 17, 0, 0, DateTimeKind.Utc),
        Basis = "7-day usage history",
    };

    /// <summary>Image URLs the feed would resolve, by device MAC. Anything absent has none.</summary>
    public Dictionary<string, string> PriorVersionUrls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int ContextCalls { get; private set; }
    public int WindowCalls { get; private set; }
    public int PriorVersionCalls { get; private set; }
    public int LastEstimatedSeconds { get; private set; }
    public TimeSpan LastMinLead { get; private set; }

    /// <summary>Learned timings the estimator is composed from, as if merged across sites.</summary>
    public List<FirmwareModelTiming> MergedTimings { get; } = [];

    public int EstimatorCalls { get; private set; }

    public Task<FirmwareTimingEstimator> GetEstimatorAsync(
        IReadOnlyList<FirmwareModelTiming> siteTimings, CancellationToken cancellationToken = default)
    {
        EstimatorCalls++;
        return Task.FromResult(new FirmwareTimingEstimator(
            MergedTimings.Count > 0 ? MergedTimings : siteTimings));
    }

    public Task<RolloutPlanningContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        ContextCalls++;
        return Task.FromResult(new RolloutPlanningContext
        {
            Devices = Devices,
            Neighbors = Neighbors,
            ClientCount = ClientCount,
            ConsoleConnected = ConsoleConnected,
        });
    }

    public Task<QuietWindowProposal> ProposeWindowAsync(
        RolloutPlanningContext context,
        int estimatedSeconds,
        FirmwareRolloutSettings settings,
        TimeSpan minLead,
        CancellationToken cancellationToken = default)
    {
        WindowCalls++;
        LastEstimatedSeconds = estimatedSeconds;
        LastMinLead = minLead;
        return Task.FromResult(Window);
    }

    public Task PopulatePriorVersionsAsync(
        RolloutPlanDocument document,
        IEnumerable<FirmwareRolloutStep> steps,
        CancellationToken cancellationToken = default)
    {
        PriorVersionCalls++;
        document.PriorVersions.Clear();
        foreach (var step in steps)
        {
            PriorVersionUrls.TryGetValue(step.DeviceMac, out var url);
            document.PriorVersions.Add(new PlanPriorVersion
            {
                Mac = step.DeviceMac,
                Version = step.FromVersion,
                Url = url,
                UnavailableReason = url == null ? "the public release feed carries no such build" : null,
            });
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Hands autopilot the one planning source the test scripted. Production opens a site-pinned
/// system scope per use; nothing about plan composition depends on that.
/// </summary>
internal sealed class DirectPlanningScope : IRolloutPlanningScope
{
    private readonly IRolloutPlanningSource _source;

    public DirectPlanningScope(IRolloutPlanningSource source) => _source = source;

    public Task<T> UseAsync<T>(
        Func<IRolloutPlanningSource, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default) =>
        work(_source, cancellationToken);

    public Task UseAsync(
        Func<IRolloutPlanningSource, CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
        work(_source, cancellationToken);
}

/// <summary>
/// Publish dates and changelog links the test scripts per model and version. Anything not set is
/// unknown to the feed, which is exactly what an RC build or a feed outage looks like.
/// </summary>
internal sealed class FakeReleaseMetadataSource : IReleaseMetadataSource
{
    private readonly Dictionary<string, ReleaseMetadata> _byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes every lookup throw, as an unreachable feed does.</summary>
    public bool Throws { get; set; }

    public int Calls { get; private set; }

    public void Set(string model, string version, DateTime? publishedAt, string? changelogUrl = null) =>
        _byKey[$"{model}|{version}"] = new ReleaseMetadata(publishedAt, changelogUrl);

    public Task<ReleaseMetadata?> GetAsync(string? model, string? version, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Throws) throw new InvalidOperationException("The release feed is unreachable");
        return Task.FromResult(_byKey.TryGetValue($"{model}|{version}", out var metadata) ? metadata : null);
    }
}

/// <summary>
/// Everything one orchestrator test needs, wired to doubles: an in-memory database with a real
/// repository behind it, so "persisted after every transition" is asserted against real rows.
/// </summary>
internal sealed class RolloutHarness : IDisposable
{
    public static readonly DateTime Start = new(2026, 8, 14, 3, 0, 0, DateTimeKind.Utc);

    private readonly string _databaseName = Guid.NewGuid().ToString();

    public NetworkOptimizerDbContext Db { get; }
    public FirmwareRolloutRepository Repository { get; }
    public FakeTimeProvider Time { get; } = new(Start);
    public FakeFirmwareCommandClient Commands { get; } = new();
    public ScriptedDeviceObserver Observer { get; } = new();
    public FakeLitmusService Litmus { get; } = new();
    public FakeHealthGate Health { get; } = new();
    public RecordingMeshRepairQueue Mesh { get; } = new();
    public RolloutSuppressionRegistry Suppression { get; } = new();
    public CapturingBus Bus { get; } = new();
    public FakeRolloutPlanningSource Planning { get; } = new();
    public FakeReleaseMetadataSource Releases { get; } = new();
    public AuditContext Audit { get; } = new();
    public CallerContext Caller { get; } = new();
    public RolloutAutopilot Autopilot { get; }
    public FirmwareRolloutOrchestrator Orchestrator { get; }
    public FirmwareRolloutService Service { get; }

    /// <summary>Actor name every plan this harness creates is attributed to.</summary>
    public const string Actor = "TestAdmin";

    public RolloutHarness()
    {
        Db = NewContext();
        Repository = new FirmwareRolloutRepository(Db, NullLogger<FirmwareRolloutRepository>.Instance);
        Autopilot = new RolloutAutopilot(
            new InMemoryRepositoryAccessor(Repository),
            new DirectPlanningScope(Planning),
            Commands,
            Releases,
            Bus,
            Time,
            NullLogger<RolloutAutopilot>.Instance);
        Orchestrator = NewOrchestrator();

        Caller.SetUser(new CallerInfo { ActorName = Actor });
        Service = new FirmwareRolloutService(
            Repository,
            Orchestrator,
            Commands,
            Planning,
            Releases,
            Audit,
            Caller,
            NullLogger<FirmwareRolloutService>.Instance);
    }

    /// <summary>
    /// Another executor over the same site and the same doubles, which is what a restart leaves:
    /// the plan is where it was and none of the in-memory state survived.
    /// </summary>
    public FirmwareRolloutOrchestrator NewOrchestrator() => new(
        new InMemoryRepositoryAccessor(Repository),
        Commands,
        Observer,
        Litmus,
        Health,
        Mesh,
        new RolloutChannelManager(Commands, NullLogger<RolloutChannelManager>.Instance),
        Suppression,
        Autopilot,
        Releases,
        Bus,
        Time,
        NullLogger<FirmwareRolloutOrchestrator>.Instance);

    public NetworkOptimizerDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: _databaseName)
            .Options;
        return new NetworkOptimizerDbContext(options);
    }

    public void Dispose() => Db.Dispose();

    public Task<FirmwareRolloutSettings> SettingsAsync() => Repository.GetSettingsAsync();

    /// <summary>Saves settings, which the executor reads fresh on every pass.</summary>
    public async Task WithSettingsAsync(Action<FirmwareRolloutSettings> configure)
    {
        var settings = await Repository.GetSettingsAsync();
        configure(settings);
        await Repository.SaveSettingsAsync(settings);
    }

    /// <summary>Creates a running plan with the given document and steps.</summary>
    public async Task<FirmwareRolloutPlan> SeedRunningPlanAsync(RolloutPlanDocument document, params FirmwareRolloutStep[] steps)
    {
        var plan = await Repository.CreatePlanAsync(new FirmwareRolloutPlan
        {
            Status = FirmwareRolloutStatus.Running,
            StartedAt = Time.GetUtcNow().UtcDateTime,
            PlanJson = JsonSerializer.Serialize(document),
            CreatedBy = "TestUser",
        });

        foreach (var step in steps) step.PlanId = plan.Id;
        await Repository.AddStepsAsync(steps);
        return plan;
    }

    /// <summary>Creates a plan that is waiting for its scheduled start.</summary>
    public async Task<FirmwareRolloutPlan> SeedScheduledPlanAsync(RolloutPlanDocument document, DateTime startAt, params FirmwareRolloutStep[] steps)
    {
        var plan = await Repository.CreatePlanAsync(new FirmwareRolloutPlan
        {
            Status = FirmwareRolloutStatus.Scheduled,
            ScheduledStartAt = startAt,
            PlanJson = JsonSerializer.Serialize(document),
            CreatedBy = "autopilot",
        });

        foreach (var step in steps) step.PlanId = plan.Id;
        await Repository.AddStepsAsync(steps);
        return plan;
    }

    /// <summary>Creates a finished plan that is waiting out its soak.</summary>
    public async Task<FirmwareRolloutPlan> SeedSoakingPlanAsync(
        RolloutPlanDocument document, DateTime startedAt, DateTime completedAt, params FirmwareRolloutStep[] steps)
    {
        var plan = await Repository.CreatePlanAsync(new FirmwareRolloutPlan
        {
            Status = FirmwareRolloutStatus.SoakWait,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            PlanJson = JsonSerializer.Serialize(document),
            CreatedBy = "TestUser",
        });

        foreach (var step in steps) step.PlanId = plan.Id;
        await Repository.AddStepsAsync(steps);
        return plan;
    }

    public async Task<FirmwareRolloutStep> StepAsync(int planId, string mac)
    {
        var steps = await Repository.GetStepsAsync(planId);
        return steps.Single(s => s.DeviceMac == mac);
    }

    public Task<FirmwareRolloutPlan?> PlanAsync(int planId) => Repository.GetPlanAsync(planId);

    public Task TickAsync() => Orchestrator.TickAsync();

    public async Task TickAsync(TimeSpan advance)
    {
        Time.Advance(advance);
        await Orchestrator.TickAsync();
    }
}

/// <summary>Builders keeping the test bodies about the state machine rather than object graphs.</summary>
internal static class RolloutFixtures
{
    public const string ApMac = "aa:bb:cc:dd:ee:01";
    public const string PeerMac = "aa:bb:cc:dd:ee:02";
    public const string SwitchMac = "aa:bb:cc:dd:ee:03";
    public const string GatewayMac = "aa:bb:cc:dd:ee:04";
    public const string FromVersion = "6.6.55.1234";
    public const string ToVersion = "7.0.11.5678";

    public static FirmwareRolloutStep Step(
        string mac,
        string name = "AP 1",
        string model = "U6PRO",
        string deviceType = "uap",
        int wave = 1,
        FirmwareRolloutStepState state = FirmwareRolloutStepState.Pending,
        string channel = "release",
        string? to = ToVersion) => new()
        {
            DeviceMac = mac,
            DeviceName = name,
            Model = model,
            DeviceType = deviceType,
            FromVersion = FromVersion,
            ToVersion = to,
            Channel = channel,
            Wave = wave,
            State = state,
        };

    /// <summary>
    /// A Cloud Gateway console reporting both channels and its UniFi Network application, which is
    /// what the channel work reads: firmware.releaseChannel and apps.controllers[network].
    /// </summary>
    public static UniFiConsoleSystemInfo Console(
        string osChannel = "release",
        string appChannel = "release",
        string? appUpdateAvailable = null,
        string appVersion = "10.6.94",
        string installedOs = "4.3.5",
        bool standalone = false) => new()
        {
            // The downgrade guards compare an offer against this, so a console without it refuses
            // every console update - which is the guard doing its job, not a fixture detail.
            Hardware = new UniFiConsoleHardware { FirmwareVersion = installedOs },
            Firmware = new UniFiConsoleFirmware
            {
                ReleaseChannel = osChannel,
                Channels = ["release", "release-candidate", "beta"],
                Latest = standalone
                    ? new UniFiConsoleFirmwareRelease { Product = UniFiConsoleSystemInfo.StandaloneConsoleProduct }
                    : null,
            },
            Apps = new UniFiConsoleApps
            {
                Controllers =
                [
                    new UniFiConsoleController
                    {
                        Name = UniFiConsoleController.NetworkName,
                        Type = "controller",
                        Version = appVersion,
                        ReleaseChannel = appChannel,
                        UpdateAvailable = appUpdateAvailable,
                    },
                ],
            },
        };

    public static RolloutPlanDocument Document(params PlanWave[] waves)
    {
        var document = new RolloutPlanDocument();
        document.Waves.AddRange(waves);
        return document;
    }

    public static PlanWave Wave(int number, params PlanWaveStep[] steps)
    {
        var wave = new PlanWave { Number = number, Channel = "release" };
        wave.Steps.AddRange(steps);
        return wave;
    }

    public static PlanWaveStep PlanStep(
        string mac,
        string model = "U6PRO",
        bool canary = false,
        bool held = false,
        int budgetSeconds = 900) => new()
        {
            Mac = mac,
            Name = "AP 1",
            Model = model,
            DeviceType = "uap",
            FromVersion = FromVersion,
            ToVersion = ToVersion,
            IsCanary = canary,
            HeldForCanary = held,
            OfflineBudgetSeconds = budgetSeconds,
        };
}

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
    public FirmwareCommandResult UpgradeResult { get; set; } = FirmwareCommandResult.Ok();
    public FirmwareCommandResult ExternalResult { get; set; } = FirmwareCommandResult.Ok();
    public FirmwareCommandResult SshResult { get; set; } = FirmwareCommandResult.Ok();
    public FirmwareCommandResult BackupResult { get; set; } = FirmwareCommandResult.Ok();

    public string DeviceChannel { get; set; } = "release";
    public UniFiConsoleSystemInfo? ConsoleInfo { get; set; } = new();
    public List<UniFiFirmwareCatalogEntry> Catalog { get; } = [];

    /// <summary>What the console offers as a UniFi OS build; null means it is current.</summary>
    public UniFiConsoleFirmwareRelease? PendingUniFiOs { get; set; }

    public bool NetworkAppUpdateAccepted { get; set; } = true;
    public bool UniFiOsUpdateAccepted { get; set; } = true;

    public List<string> UpgradeCommands { get; } = [];
    public List<(string Mac, string Url)> ExternalCommands { get; } = [];
    public List<(string Host, string Url)> SshCommands { get; } = [];
    public List<string> ChannelWrites { get; } = [];
    public int CheckForUpdatesCalls { get; private set; }
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

    public Task<bool> SetDeviceChannelAsync(string channel, CancellationToken cancellationToken = default)
    {
        ChannelWrites.Add(channel);
        DeviceChannel = channel;
        return Task.FromResult(true);
    }

    public Task<bool> SetConsoleChannelsAsync(string? networkAppChannel, string? unifiOsChannel, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

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
        return Task.FromResult(NetworkAppUpdateAccepted);
    }

    public Task<UniFiConsoleFirmwareRelease?> GetPendingUniFiOsUpdateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PendingUniFiOs);

    public Task<bool> TriggerUniFiOsUpdateAsync(CancellationToken cancellationToken = default)
    {
        UniFiOsUpdateCalls++;
        return Task.FromResult(UniFiOsUpdateAccepted);
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
    public FirmwareRolloutOrchestrator Orchestrator { get; }

    public RolloutHarness()
    {
        Db = NewContext();
        Repository = new FirmwareRolloutRepository(Db, NullLogger<FirmwareRolloutRepository>.Instance);
        var channels = new RolloutChannelManager(Commands, NullLogger<RolloutChannelManager>.Instance);
        Orchestrator = new FirmwareRolloutOrchestrator(
            new InMemoryRepositoryAccessor(Repository),
            Commands,
            Observer,
            Litmus,
            Health,
            Mesh,
            channels,
            Suppression,
            Bus,
            Time,
            NullLogger<FirmwareRolloutOrchestrator>.Instance);
    }

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

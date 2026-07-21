using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Auditing;

/// <summary>
/// The append-only audit sink and its background writer (design doc 05). Implements
/// <see cref="IAuditLogger"/> as a bounded channel (drop-oldest on overflow, availability over
/// completeness for a network tool) and drains it on a single background thread that batches inserts
/// into the main-DB <c>AuditEvents</c> table, flushes on shutdown, and prunes old rows on a schedule.
/// </summary>
public sealed class AuditWriterService : BackgroundService, IAuditLogger
{
    private const int Capacity = 10_000;
    private const int MaxBatch = 200;

    private readonly Channel<AuditEvent> _channel = Channel.CreateBounded<AuditEvent>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly IDbContextFactory<AuthDbContext> _dbFactory;
    private readonly AuditRetentionOptions _retention;
    private readonly IAuditForwarder _forwarder;
    private readonly ILogger<AuditWriterService> _logger;
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public AuditWriterService(
        IDbContextFactory<AuthDbContext> dbFactory,
        AuditRetentionOptions retention,
        IAuditForwarder forwarder,
        ILogger<AuditWriterService> logger)
    {
        _dbFactory = dbFactory;
        _retention = retention;
        _forwarder = forwarder;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Log(AuditEvent auditEvent) => _channel.Writer.TryWrite(auditEvent);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var first in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                var batch = new List<AuditEvent> { first };
                while (batch.Count < MaxBatch && _channel.Reader.TryRead(out var next))
                    batch.Add(next);

                await PersistAsync(batch, stoppingToken);
                await _forwarder.ForwardAsync(batch, stoppingToken);
                await MaybePruneAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown - fall through to the final drain below.
        }

        await DrainRemainingAsync();
    }

    private async Task PersistAsync(List<AuditEvent> batch, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.AuditEvents.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Never let audit I/O crash the writer; the events are lost but the app stays up.
            _logger.LogError(ex, "Audit writer failed to persist {Count} events.", batch.Count);
        }
    }

    private async Task MaybePruneAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastPruneUtc < TimeSpan.FromHours(6))
            return;
        _lastPruneUtc = DateTime.UtcNow;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var cutoff = DateTime.UtcNow - _retention.MaxAge;
            var removedByAge = await db.AuditEvents.Where(e => e.TimestampUtc < cutoff).ExecuteDeleteAsync(ct);

            var removedByCap = 0;
            var total = await db.AuditEvents.CountAsync(ct);
            if (total > _retention.MaxRows)
            {
                var overflow = total - _retention.MaxRows;
                var oldestIds = await db.AuditEvents
                    .OrderBy(e => e.Id).Take(overflow).Select(e => e.Id).ToListAsync(ct);
                removedByCap = await db.AuditEvents.Where(e => oldestIds.Contains(e.Id)).ExecuteDeleteAsync(ct);
            }

            var removed = removedByAge + removedByCap;
            if (removed > 0)
            {
                // The prune is itself an audited event (design doc 05).
                db.AuditEvents.Add(new AuditEvent
                {
                    Category = AuditCategories.Audit,
                    Action = AuditActions.Pruned,
                    ActorName = "system:audit-retention",
                    ActorAuthMethod = "system",
                    Outcome = AuditOutcomes.Success,
                    DetailsJson = $"{{\"removedByAge\":{removedByAge},\"removedByCap\":{removedByCap},\"maxAgeDays\":{_retention.MaxAge.TotalDays:0}}}",
                });
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Audit retention pruned {Removed} events.", removed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit retention prune failed.");
        }
    }

    private async Task DrainRemainingAsync()
    {
        var remaining = new List<AuditEvent>();
        while (_channel.Reader.TryRead(out var e))
            remaining.Add(e);
        if (remaining.Count == 0)
            return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.AuditEvents.AddRange(remaining);
            await db.SaveChangesAsync();
            _logger.LogInformation("Audit writer flushed {Count} events on shutdown.", remaining.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit writer failed to flush {Count} events on shutdown.", remaining.Count);
        }
    }
}

/// <summary>Audit retention limits (design doc 05): default 365 days plus a hard row cap.</summary>
public sealed class AuditRetentionOptions
{
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(365);
    public int MaxRows { get; init; } = 1_000_000;
}

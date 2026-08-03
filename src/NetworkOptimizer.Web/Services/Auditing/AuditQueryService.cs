using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Auditing;

/// <summary>Filter for querying the audit log (design doc 05: time/category/actor/site/outcome).</summary>
public sealed record AuditFilter
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public string? Category { get; init; }
    public string? Actor { get; init; }
    public string? SiteSlug { get; init; }
    public string? Outcome { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 100;
}

/// <summary>Read-only, filtered access to the audit log plus CSV/JSON export of the current filter.</summary>
public interface IAuditQueryService
{
    Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditFilter filter);
    Task<int> CountAsync(AuditFilter filter);
    Task<string> ExportJsonAsync(AuditFilter filter);
    Task<string> ExportCsvAsync(AuditFilter filter);
}

/// <inheritdoc />
public sealed class AuditQueryService : IAuditQueryService
{
    private const int ExportCap = 100_000;
    private readonly IDbContextFactory<AuthDbContext> _dbFactory;

    public AuditQueryService(IDbContextFactory<AuthDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditFilter filter)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await Apply(db.AuditEvents.AsNoTracking(), filter)
            .OrderByDescending(e => e.Id)
            .Skip(filter.Skip)
            .Take(Math.Clamp(filter.Take, 1, 1000))
            .ToListAsync();
    }

    public async Task<int> CountAsync(AuditFilter filter)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await Apply(db.AuditEvents.AsNoTracking(), filter).CountAsync();
    }

    public async Task<string> ExportJsonAsync(AuditFilter filter)
    {
        var rows = await ExportRowsAsync(filter);
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> ExportCsvAsync(AuditFilter filter)
    {
        var rows = await ExportRowsAsync(filter);
        var sb = new StringBuilder();
        sb.AppendLine("Id,TimestampUtc,Category,Action,Outcome,Actor,AuthMethod,SourceIp,TargetType,TargetId,TargetName,SiteSlug,CorrelationId");
        foreach (var e in rows)
        {
            sb.Append(e.Id).Append(',')
              .Append(Csv(e.TimestampUtc.ToString("O"))).Append(',')
              .Append(Csv(e.Category)).Append(',')
              .Append(Csv(e.Action)).Append(',')
              .Append(Csv(e.Outcome)).Append(',')
              .Append(Csv(e.ActorName)).Append(',')
              .Append(Csv(e.ActorAuthMethod)).Append(',')
              .Append(Csv(e.SourceIp)).Append(',')
              .Append(Csv(e.TargetType)).Append(',')
              .Append(Csv(e.TargetId)).Append(',')
              .Append(Csv(e.TargetName)).Append(',')
              .Append(Csv(e.SiteSlug)).Append(',')
              .Append(Csv(e.CorrelationId)).AppendLine();
        }
        return sb.ToString();
    }

    private async Task<List<AuditEvent>> ExportRowsAsync(AuditFilter filter)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await Apply(db.AuditEvents.AsNoTracking(), filter)
            .OrderByDescending(e => e.Id)
            .Take(ExportCap)
            .ToListAsync();
    }

    private static IQueryable<AuditEvent> Apply(IQueryable<AuditEvent> q, AuditFilter f)
    {
        if (f.FromUtc is not null) q = q.Where(e => e.TimestampUtc >= f.FromUtc);
        if (f.ToUtc is not null) q = q.Where(e => e.TimestampUtc <= f.ToUtc);
        if (!string.IsNullOrEmpty(f.Category)) q = q.Where(e => e.Category == f.Category);
        if (!string.IsNullOrEmpty(f.Outcome)) q = q.Where(e => e.Outcome == f.Outcome);
        // Only the typed side is normalized: slugs are stored lowercase, so lowering the column too
        // would run a function per row and give up the index for a case that cannot occur.
        if (!string.IsNullOrEmpty(f.SiteSlug))
        {
            var slug = f.SiteSlug.ToLowerInvariant();
            q = q.Where(e => e.SiteSlug == slug);
        }
        // Both sides here, unlike the slug above: actor names are whatever the identity provider
        // gave us, so neither end has a guaranteed case. Contains translates to instr() on SQLite,
        // which is case-sensitive - searching "Kira" found nothing for a user stored as "kira".
        if (!string.IsNullOrEmpty(f.Actor))
        {
            var actor = f.Actor.ToLowerInvariant();
            q = q.Where(e => e.ActorName != null && e.ActorName.ToLower().Contains(actor));
        }
        return q;
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}

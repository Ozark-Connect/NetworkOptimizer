using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Helpers;

/// <summary>
/// Speed test history queries shared by every results page: the raw-JSON-free projection, the history
/// search in SQL, paging, and a result's position. Each takes the scope (directions, time window) as the
/// query it is called on.
/// </summary>
public static class Iperf3ResultQueries
{
    /// <summary>The directions LAN Speed Test shows: everything but the WAN tests.</summary>
    public static readonly SpeedTestDirection[] LanDirections =
    [
        SpeedTestDirection.ServerToDevice, SpeedTestDirection.ClientToServer, SpeedTestDirection.BrowserToServer,
    ];

    /// <summary>The directions Client Speed Test shows: tests a client started.</summary>
    public static readonly SpeedTestDirection[] ClientDirections =
    [
        SpeedTestDirection.ClientToServer, SpeedTestDirection.BrowserToServer,
    ];

    private static Expression<Func<Iperf3Result, Iperf3Result>>? _withoutRawJson;

    /// <summary>
    /// Every mapped column except the raw iperf3 JSON, which no page reads back and which is most of a
    /// row's size. Built from the EF model, so a new column is carried without editing this. The results
    /// are not tracked; attach one before changing it, and the save writes only what changed.
    /// </summary>
    public static IQueryable<Iperf3Result> WithoutRawJson(this IQueryable<Iperf3Result> query, DbContext db) =>
        query.Select(_withoutRawJson ??= BuildWithoutRawJson(db.Model));

    private static Expression<Func<Iperf3Result, Iperf3Result>> BuildWithoutRawJson(IModel model)
    {
        var r = Expression.Parameter(typeof(Iperf3Result), "r");
        var bindings = model.FindEntityType(typeof(Iperf3Result))!.GetProperties()
            .Where(p => p.PropertyInfo is { CanWrite: true }
                        && p.Name is not (nameof(Iperf3Result.RawDownloadJson) or nameof(Iperf3Result.RawUploadJson)))
            .Select(p => (MemberBinding)Expression.Bind(p.PropertyInfo!, Expression.Property(r, p.PropertyInfo!)));
        return Expression.Lambda<Func<Iperf3Result, Iperf3Result>>(
            Expression.MemberInit(Expression.New(typeof(Iperf3Result)), bindings), r);
    }

    /// <summary>Newest first, ties broken by Id: the order every page and position uses.</summary>
    public static IOrderedQueryable<Iperf3Result> NewestFirst(this IQueryable<Iperf3Result> query) =>
        query.OrderByDescending(r => r.TestTime).ThenByDescending(r => r.Id);

    /// <summary>
    /// Applies a history filter in SQL. Matches the same fields as <see cref="SpeedTestFilterHelper.MatchesFilter"/>;
    /// keep the two in step.
    /// </summary>
    public static IQueryable<Iperf3Result> MatchingHistoryFilter(this IQueryable<Iperf3Result> query, DbContext db, SpeedTestHistoryFilter filter)
    {
        if (!string.IsNullOrEmpty(filter.DeviceHost))
        {
            var host = filter.DeviceHost.ToLower();
            query = query.Where(r => r.DeviceHost.ToLower() == host);
        }

        if (filter.NormalizedSearch is { } f)
        {
            // Hops live in PathAnalysisJson; json_valid skips a row the model would fail to deserialize.
            var hopMatchIds = db.Database.SqlQuery<int>($"""
                SELECT r.Id AS Value FROM Iperf3Results r
                WHERE r.PathAnalysisJson IS NOT NULL AND json_valid(r.PathAnalysisJson)
                  AND EXISTS (
                    SELECT 1 FROM json_each(r.PathAnalysisJson, '$.Path.Hops') h
                    WHERE instr(lower(coalesce(json_extract(h.value, '$.DeviceName'), '')), {f}) > 0
                       OR instr(lower(coalesce(json_extract(h.value, '$.DeviceMac'), '')), {f}) > 0
                       OR instr(lower(coalesce(json_extract(h.value, '$.DeviceIp'), '')), {f}) > 0)
                """);
            query = query.Where(r =>
                r.DeviceHost.ToLower().Contains(f)
                || (r.DeviceName != null && r.DeviceName.ToLower().Contains(f))
                || (r.ClientMac != null && r.ClientMac.ToLower().Contains(f))
                || hopMatchIds.Contains(r.Id));
        }

        return query;
    }

    /// <summary>
    /// One page of <paramref name="scope"/> under a history filter, newest first, without the raw JSON.
    /// The page is clamped to the last one.
    /// </summary>
    public static async Task<SpeedTestHistoryPage> PageAsync(this IQueryable<Iperf3Result> scope, DbContext db,
        SpeedTestHistoryFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        scope = scope.AsNoTracking();
        var totalUnfiltered = await scope.CountAsync(cancellationToken);
        var filtered = filter.IsEmpty ? scope : scope.MatchingHistoryFilter(db, filter);
        var total = filter.IsEmpty ? totalUnfiltered : await filtered.CountAsync(cancellationToken);

        pageSize = Math.Max(1, pageSize);
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, lastPage);

        var items = total == 0
            ? []
            : await filtered.NewestFirst()
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .WithoutRawJson(db)
                .ToListAsync(cancellationToken);

        return new SpeedTestHistoryPage(items, page, total, totalUnfiltered);
    }

    /// <summary>
    /// The 0-based position of a result in <paramref name="scope"/> under a history filter, in
    /// <see cref="NewestFirst"/> order, or null when the filter or scope excludes it.
    /// </summary>
    public static async Task<int?> IndexOfAsync(this IQueryable<Iperf3Result> scope, DbContext db,
        SpeedTestHistoryFilter filter, int resultId, CancellationToken cancellationToken = default)
    {
        var filtered = scope.AsNoTracking().MatchingHistoryFilter(db, filter);
        var target = await filtered
            .Where(r => r.Id == resultId)
            .Select(r => new { r.TestTime })
            .FirstOrDefaultAsync(cancellationToken);
        if (target == null) return null;

        return await filtered.CountAsync(r =>
            r.TestTime > target.TestTime || (r.TestTime == target.TestTime && r.Id > resultId), cancellationToken);
    }

    /// <summary>
    /// A cheap fingerprint of <paramref name="scope"/>: its row count, newest Id, and newest test time. A poll
    /// compares two to skip a reload when nothing was added or deleted.
    /// </summary>
    public static async Task<SpeedTestResultsStamp> StampAsync(this IQueryable<Iperf3Result> scope, CancellationToken cancellationToken = default)
    {
        var stamp = await scope.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new SpeedTestResultsStamp(g.Count(), g.Max(r => r.Id), g.Max(r => r.TestTime)))
            .FirstOrDefaultAsync(cancellationToken);
        return stamp ?? SpeedTestResultsStamp.None;
    }
}

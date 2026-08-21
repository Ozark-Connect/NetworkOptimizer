using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// The PON series both chart tabs draw. An ONT attached to a monitored SFP module and a standalone
/// one report the same counters, so they get one builder rather than two lists kept in step by hand.
/// </summary>
public static class PonChartSeries
{
    /// <summary>
    /// Project supplemental PON points into the chart payload, converting cumulative
    /// counters to per-interval deltas (CM Stats style - cumulative lines are unreadable).
    /// A negative step means the ONT rebooted and reset its counters; that interval's
    /// delta is null (a gap) rather than a bogus spike. Null when the module has no
    /// supplemental data, so the UI can hide the PON section entirely.
    /// </summary>
    public static List<object>? Build(List<MonitoringInfluxClient.PonSeriesPoint>? points)
    {
        if (points is not { Count: > 0 }) return null;

        static long? Delta(long? cur, long? prev) =>
            cur is long c && prev is long p && c >= p ? c - p : null;

        var ordered = points.OrderBy(p => p.Time).ToList();
        var items = new List<object>(ordered.Count);
        MonitoringInfluxClient.PonSeriesPoint? prev = null;
        foreach (var p in ordered)
        {
            items.Add(new
            {
                time = p.Time.ToString("o"),
                state = p.PonLinkStatus,
                statePrev = p.PonLinkStatusPrev,
                onuId = p.OnuId,
                dsFec = p.DsFecEnabled,
                usFec = p.UsFecEnabled,
                respTime = p.OnuResponseTime,
                uptime = p.SfpUptimeS,
                lanLink = p.LanLinkStatus,
                lanMode = p.LanMode,
                bip = Delta(p.BipErrors, prev?.BipErrors),
                fec = Delta(p.FecErrors, prev?.FecErrors),
                fecCorr = Delta(p.FecCorrectedWords, prev?.FecCorrectedWords),
                hec = Delta(p.HecUncorrected, prev?.HecUncorrected),
                hecCorr = Delta(p.HecCorrected, prev?.HecCorrected),
                bwmapCorr = Delta(p.BwmapCorrected, prev?.BwmapCorrected),
                bwmapUncorr = Delta(p.BwmapUncorrected, prev?.BwmapUncorrected),
                gemTx = Delta(p.GemTxFrames, prev?.GemTxFrames),
                gemTxIdle = Delta(p.GemTxIdleFrames, prev?.GemTxIdleFrames),
                gemRx = Delta(p.GemRxFrames, prev?.GemRxFrames),
                gemDrop = Delta(p.GemRxDropped, prev?.GemRxDropped),
                allocLost = Delta(p.AllocLost, prev?.AllocLost),
                lanFcs = Delta(p.LanRxFcsErrors, prev?.LanRxFcsErrors),
                lanDrop = Delta(p.LanTxDropEvents, prev?.LanTxDropEvents),
                lanOvfl = Delta(p.LanBufferOverflow, prev?.LanBufferOverflow),
            });
            prev = p;
        }
        return items;
    }
}

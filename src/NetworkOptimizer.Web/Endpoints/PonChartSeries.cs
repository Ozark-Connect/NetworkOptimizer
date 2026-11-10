using NetworkOptimizer.Core.Helpers;
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
                ploamMs = p.PloamElapsedMs,
                lanLink = p.LanLinkStatus,
                lanMode = p.LanMode,
                olt = DisplayFormatters.FormatOltVendor(p.OltVendorId, p.OltModel),
                oltVersion = p.OltVersion,
                registered = DisplayFormatters.FormatPonRegistration(p.RegistrationStatus, p.KeyExchangeStatus),
                firmware = p.FirmwareVersion,
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
                dsUtil = DownstreamUtilization(p, prev)?.Percent,
                dsUsedMbps = DownstreamUtilization(p, prev)?.Mbps,
                allocLost = Delta(p.AllocLost, prev?.AllocLost),
                lanFcs = Delta(p.LanRxFcsErrors, prev?.LanRxFcsErrors),
                lanDrop = Delta(p.LanTxDropEvents, prev?.LanTxDropEvents),
                lanOvfl = Delta(p.LanBufferOverflow, prev?.LanBufferOverflow),
                // Raw cumulative values for the error totals table
                bipTotal = p.BipErrors,
                fecTotal = p.FecErrors,
                fecCorrTotal = p.FecCorrectedWords,
                hecTotal = p.HecUncorrected,
                hecCorrTotal = p.HecCorrected,
                bwmapCorrTotal = p.BwmapCorrected,
                bwmapUncorrTotal = p.BwmapUncorrected,
                allocLostTotal = p.AllocLost,
                gemDropTotal = p.GemRxDropped,
                lanFcsTotal = p.LanRxFcsErrors,
                lanDropTotal = p.LanTxDropEvents,
                lanOvflTotal = p.LanBufferOverflow,
            });
            prev = p;
        }
        return items;
    }

    /// <summary>A GPON downstream frame: 125 us, 38880 bytes at 2.488 Gbit/s.</summary>
    private const double GponFrameBytes = 38880;
    /// <summary>With FEC on, RS(255,239) takes 16 parity bytes from each of the frame's 153 codewords.</summary>
    private const double GponFecParityBytes = 153 * 16;
    private const double GemIdleFrameBytes = 5;
    private const double GponFramesPerSecond = 8000;
    private const long SuperframeModulus = 1L << 30;

    /// <summary>
    /// How much of the PON's downstream was in use over one interval, all ONTs together: the OLT
    /// fills what nobody needs with idle GEM frames, and every ONT counts the same ones. Read
    /// against the superframe counter rather than the clock, so the interval length and the
    /// sampling jitter drop out. The frame header (PCBd, with the bandwidth map) is counted as
    /// in use, so a PON with no traffic at all still reads a little above zero. Null when either
    /// counter is missing, went backwards (a reboot) or the result is outside 0 to 100 percent,
    /// which is what a superframe counter that wrapped more than once produces.
    /// </summary>
    public static (double Percent, double Mbps)? DownstreamUtilization(
        MonitoringInfluxClient.PonSeriesPoint cur, MonitoringInfluxClient.PonSeriesPoint? prev)
    {
        if (prev is null
            || cur.GemRxIdleFrames is not long idle || prev.GemRxIdleFrames is not long idlePrev
            || cur.DsSuperframe is not long sf || prev.DsSuperframe is not long sfPrev
            || idle < idlePrev)
            return null;

        var frames = sf >= sfPrev ? sf - sfPrev : sf + SuperframeModulus - sfPrev;
        if (frames <= 0) return null;

        var payload = GponFrameBytes - (cur.DsFecEnabled == 1 ? GponFecParityBytes : 0);
        var usedPerFrame = payload - (idle - idlePrev) * GemIdleFrameBytes / frames;
        var percent = usedPerFrame / payload * 100;
        if (percent < 0 || percent > 100) return null;

        return (Math.Round(percent, 3), Math.Round(usedPerFrame * 8 * GponFramesPerSecond / 1e6, 2));
    }
}

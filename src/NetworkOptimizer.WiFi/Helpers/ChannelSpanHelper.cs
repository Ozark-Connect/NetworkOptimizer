using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>
/// Shared helpers for channel span/bonding group calculations and interference scoring.
/// Extracted from SpectrumAnalysis.razor and ChannelAnalysis.razor to avoid duplication.
/// </summary>
public static class ChannelSpanHelper
{
    /// <summary>
    /// Returns the (low, high) channel range for a given primary channel and width,
    /// accounting for bonding groups. Used for overlap-aware interference scoring.
    /// With a measured <paramref name="centerChannel"/> (5 and 6 GHz) the span is the block
    /// around that center; without one it is derived from the primary, which at 320 MHz is a
    /// guess between two overlapping channelizations.
    /// </summary>
    public static (int Low, int High) GetChannelSpan(RadioBand band, int primaryChannel, int width, int? centerChannel = null)
    {
        if (band == RadioBand.Band2_4GHz)
        {
            // 2.4 GHz: 5 MHz channel spacing, ~22 MHz signal width
            int halfSpan = width == 40 ? 4 : 2;
            return (Math.Max(1, primaryChannel - halfSpan), Math.Min(14, primaryChannel + halfSpan));
        }

        // 5 GHz and 6 GHz: 20 MHz spacing (4 channel numbers apart)
        if (width <= 20) return (primaryChannel, primaryChannel);

        if (TrySpanFromCenter(primaryChannel, width, centerChannel, out var measured))
            return measured;

        int channelCount = width / 20;
        int groupStart = band == RadioBand.Band5GHz
            ? GetBondingGroupStart5GHz(primaryChannel, width)
            : GetBondingGroupStart6GHz(primaryChannel, width);

        return (groupStart, groupStart + (channelCount - 1) * 4);
    }

    /// <summary>
    /// The block around a measured center: center +/- (width / 20 - 1) * 2 channel numbers. A
    /// center whose block does not contain the primary is not this radio's and is ignored.
    /// </summary>
    private static bool TrySpanFromCenter(int primaryChannel, int width, int? centerChannel, out (int Low, int High) span)
    {
        span = default;
        if (centerChannel is not { } center || width < 40) return false;
        int half = (width / 20 - 1) * 2;
        span = (center - half, center + half);
        return span.Low <= primaryChannel && primaryChannel <= span.High;
    }

    /// <summary>
    /// Converts a center frequency in MHz to the band's channel number. 5 GHz counts from 5000
    /// MHz, 6 GHz from 5950 MHz, in 5 MHz steps. Null for 2.4 GHz (whose channels are not
    /// evenly spaced) or a frequency off the band's grid.
    /// </summary>
    public static int? CenterChannelFromMhz(RadioBand band, int centerMhz)
    {
        int baseMhz = band switch
        {
            RadioBand.Band5GHz => 5000,
            RadioBand.Band6GHz => 5950,
            _ => 0
        };
        if (baseMhz == 0 || centerMhz <= baseMhz || (centerMhz - baseMhz) % 5 != 0) return null;
        return (centerMhz - baseMhz) / 5;
    }

    /// <summary>
    /// Returns the list of individual channels spanned by a given primary channel and width.
    /// Used for visual channel map rendering. Accounts for 2.4 GHz extension channel direction
    /// and, on 5 and 6 GHz, a measured block center.
    /// </summary>
    public static List<int> GetChannelWidthSpan(RadioBand band, int primaryChannel, int width, int? extChannel = null, int? centerChannel = null)
    {
        var channels = new List<int>();

        if (band == RadioBand.Band2_4GHz)
        {
            int spanLow, spanHigh;
            if (width >= 40 && extChannel.HasValue)
            {
                // ExtChannel is a direction flag: 1 = above (HT40+), -1 = below (HT40-)
                int secondary = extChannel.Value > 0 ? primaryChannel + 4 : primaryChannel - 4;
                int lo = Math.Min(primaryChannel, secondary);
                int hi = Math.Max(primaryChannel, secondary);
                spanLow = lo - 2;
                spanHigh = hi + 2;
            }
            else if (width >= 40)
            {
                // No extension channel info - assume standard HT40 direction
                int ext = primaryChannel <= 7 ? primaryChannel + 4 : primaryChannel - 4;
                int lo = Math.Min(primaryChannel, ext);
                int hi = Math.Max(primaryChannel, ext);
                spanLow = lo - 2;
                spanHigh = hi + 2;
            }
            else
            {
                // 20 MHz: ±2 spectral overlap (e.g. ch6 → 4-8)
                spanLow = primaryChannel - 2;
                spanHigh = primaryChannel + 2;
            }

            for (int ch = Math.Max(1, spanLow); ch <= Math.Min(14, spanHigh); ch++)
                channels.Add(ch);

            return channels;
        }

        if (width <= 20)
        {
            channels.Add(primaryChannel);
            return channels;
        }

        // 5 GHz and 6 GHz: 20 MHz channel spacing (4 channel numbers apart)
        int channelCount = width / 20;
        var (groupStart, _) = GetChannelSpan(band, primaryChannel, width, centerChannel);

        for (int i = 0; i < channelCount; i++)
            channels.Add(groupStart + (i * 4));

        return channels;
    }

    /// <summary>
    /// Check if two channel spans overlap.
    /// </summary>
    public static bool SpansOverlap((int Low, int High) a, (int Low, int High) b) =>
        a.Low <= b.High && b.Low <= a.High;

    /// <summary>
    /// CCA threshold (dBm). At or below this a radio does not detect a co-channel transmission and
    /// won't defer, so it suffers no contention - the interference weight curve is anchored here.
    /// </summary>
    private const double CcaThresholdDbm = -82.0;

    /// <summary>Signal (dBm) at or above which a co-channel interferer fully saturates (weight 1.0).</summary>
    private const double SaturationDbm = -50.0;

    /// <summary>
    /// Convert a received signal strength to a co-channel interference weight in [0, 1], anchored at
    /// the CCA threshold: a signal at or below CCA (-82 dBm) causes no contention (weight 0 - the
    /// radio doesn't defer), ramping linearly to 1.0 at a saturating -50 dBm. Operates on the
    /// received signal, which already accounts for band-specific propagation, so it is band-agnostic.
    /// </summary>
    public static double SignalToInterferenceWeight(int signalDbm) =>
        Math.Clamp((signalDbm - CcaThresholdDbm) / (SaturationDbm - CcaThresholdDbm), 0.0, 1.0);

    /// <summary>
    /// Compute the channel overlap factor between two channel assignments.
    /// Returns 0.0 (no overlap) to 1.0 (co-channel). A measured block center for either side
    /// replaces the guessed bonding group for that side.
    /// </summary>
    public static double ComputeOverlapFactor(
        RadioBand band,
        int channel1, int width1,
        int channel2, int width2,
        int? center1 = null, int? center2 = null)
    {
        if (band == RadioBand.Band2_4GHz)
        {
            // 2.4 GHz has overlapping channels with graduated interference
            int separation = Math.Abs(channel1 - channel2);
            return separation switch
            {
                0 => 1.0,
                1 => 0.7,
                2 => 0.3,
                3 => 0.05,
                _ => 0.0
            };
        }

        // 5/6 GHz: OFDM non-overlapping channel plan. The same primary is co-channel unless
        // measured centers say the two 320 MHz radios chose different blocks around it.
        if (channel1 == channel2 && center1 == center2)
            return 1.0;

        // Check bonding group overlap
        var span1 = GetChannelSpan(band, channel1, width1, center1);
        var span2 = GetChannelSpan(band, channel2, width2, center2);

        // Identical span = full co-channel. Two wide radios in the same bonding block occupy
        // the exact same spectrum even when their control channels differ (e.g. 100/160 and
        // 112/160 both span 100-128), so they time-share the whole channel just like a matched
        // primary. Without this they fall through to the partial-overlap branch and are scored
        // as merely "secondary" overlap, under-counting the interference.
        if (span1 == span2)
            return 1.0;

        if (SpansOverlap(span1, span2))
            return 0.7; // Bonding group overlap (secondary channels)

        return 0.0;
    }

    /// <summary>
    /// Get the start channel of the bonding group for 5 GHz.
    /// </summary>
    public static int GetBondingGroupStart5GHz(int primaryChannel, int width)
    {
        var groups = width switch
        {
            160 => new (int s, int e)[] { (36, 64), (100, 128) },
            80 => new (int s, int e)[] { (36, 48), (52, 64), (100, 112), (116, 128), (132, 144), (149, 161) },
            _ => new (int s, int e)[]
            {
                (36, 40), (44, 48), (52, 56), (60, 64),
                (100, 104), (108, 112), (116, 120), (124, 128), (132, 136), (140, 144),
                (149, 153), (157, 161), (165, 165)
            }
        };

        foreach (var (start, end) in groups)
        {
            if (primaryChannel >= start && primaryChannel <= end)
                return start;
        }
        return primaryChannel;
    }

    /// <summary>
    /// Get the start channel of the bonding group for 6 GHz. This is the guess used when no
    /// measured center is available; see <see cref="GetChannelSpan"/>.
    /// </summary>
    public static int GetBondingGroupStart6GHz(int primaryChannel, int width)
    {
        if (width == 320)
            return GetBondingGroupStart6GHz320(primaryChannel);

        if (width == 160)
        {
            var groups = new (int s, int e)[]
            {
                (1, 29), (33, 61), (65, 93), (97, 125),
                (129, 157), (161, 189), (193, 221), (225, 253)
            };
            foreach (var (start, end) in groups)
                if (primaryChannel >= start && primaryChannel <= end) return start;
        }
        else if (width == 80)
        {
            int offset = primaryChannel - 1;
            return 1 + (offset / 16 * 16);
        }
        else // 40 MHz
        {
            int offset = primaryChannel - 1;
            return 1 + (offset / 8 * 8);
        }
        return primaryChannel;
    }

    /// <summary>
    /// 802.11be defines two overlapping 320 MHz channelizations (blocks starting at 1, 65, 129,
    /// 193 and at 33, 97, 161), and every primary above 29 is valid in one block of each. The
    /// guess is the lower of the two, which is the block UniFi chose in every case measured so
    /// far (primary 69 in 33-93, 5 in 1-61, 165 in 129-189). Only a measured center is certain.
    /// </summary>
    private static int GetBondingGroupStart6GHz320(int primaryChannel)
    {
        if (primaryChannel < 1) return primaryChannel;
        int lower = 1 + (primaryChannel - 1) / 64 * 64;
        if (primaryChannel >= 33)
            lower = Math.Min(lower, 33 + (primaryChannel - 33) / 64 * 64);
        return lower;
    }
}

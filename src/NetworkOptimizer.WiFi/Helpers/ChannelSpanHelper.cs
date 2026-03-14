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
    /// </summary>
    public static (int Low, int High) GetChannelSpan(RadioBand band, int primaryChannel, int width)
    {
        if (band == RadioBand.Band2_4GHz)
        {
            // 2.4 GHz: 5 MHz channel spacing, ~22 MHz signal width
            int halfSpan = width == 40 ? 4 : 2;
            return (Math.Max(1, primaryChannel - halfSpan), Math.Min(14, primaryChannel + halfSpan));
        }

        // 5 GHz and 6 GHz: 20 MHz spacing (4 channel numbers apart)
        if (width <= 20) return (primaryChannel, primaryChannel);

        int channelCount = width / 20;
        int groupStart = band == RadioBand.Band5GHz
            ? GetBondingGroupStart5GHz(primaryChannel, width)
            : GetBondingGroupStart6GHz(primaryChannel, width);

        return (groupStart, groupStart + (channelCount - 1) * 4);
    }

    /// <summary>
    /// Returns the list of individual channels spanned by a given primary channel and width.
    /// Used for visual channel map rendering. Accounts for 2.4 GHz extension channel direction.
    /// </summary>
    public static List<int> GetChannelWidthSpan(RadioBand band, int primaryChannel, int width, int? extChannel = null)
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
        int groupStart = band == RadioBand.Band5GHz
            ? GetBondingGroupStart5GHz(primaryChannel, width)
            : GetBondingGroupStart6GHz(primaryChannel, width);

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
    /// Convert signal strength to interference weight using the standard formula.
    /// Maps -90 dBm → 0.1 (barely audible) through -50 dBm → 1.0 (very close).
    /// </summary>
    public static double SignalToInterferenceWeight(int signalDbm) =>
        Math.Clamp((signalDbm + 90) / 40.0, 0.1, 1.0);

    /// <summary>
    /// Compute the channel overlap factor between two channel assignments.
    /// Returns 0.0 (no overlap) to 1.0 (co-channel).
    /// </summary>
    public static double ComputeOverlapFactor(
        RadioBand band,
        int channel1, int width1,
        int channel2, int width2)
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

        // 5/6 GHz: OFDM non-overlapping channel plan
        // Check if same primary channel
        if (channel1 == channel2)
            return 1.0;

        // Check bonding group overlap
        var span1 = GetChannelSpan(band, channel1, width1);
        var span2 = GetChannelSpan(band, channel2, width2);

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
    /// Get the start channel of the bonding group for 6 GHz.
    /// </summary>
    public static int GetBondingGroupStart6GHz(int primaryChannel, int width)
    {
        if (width == 320)
        {
            var groups = new (int s, int e)[] { (1, 61), (97, 157), (161, 221) };
            foreach (var (start, end) in groups)
                if (primaryChannel >= start && primaryChannel <= end) return start;
        }
        else if (width == 160)
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
}

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Math kernel for ISP Health sub-scores: monotone piecewise-linear interpolation
/// over (value, score) anchors plus an exponential falloff for thresholds where the
/// score should drop drastically once exceeded.
/// </summary>
public static class ScoreCurve
{
    /// <summary>
    /// Interpolates a 0-100 score from anchors ordered by ascending value.
    /// Values at or below the first anchor return its score; values at or beyond
    /// the last anchor return its score. Anchors may describe a decreasing or
    /// increasing score as long as values ascend.
    /// </summary>
    public static double Interpolate(double value, params (double Value, double Score)[] anchors)
    {
        if (anchors.Length == 0) return 0;
        if (value <= anchors[0].Value) return Clamp(anchors[0].Score);
        for (var i = 1; i < anchors.Length; i++)
        {
            if (value > anchors[i].Value) continue;
            var (v0, s0) = anchors[i - 1];
            var (v1, s1) = anchors[i];
            if (v1 <= v0) return Clamp(s1);
            var t = (value - v0) / (v1 - v0);
            return Clamp(s0 + (s1 - s0) * t);
        }
        return Clamp(anchors[^1].Score);
    }

    /// <summary>
    /// Score for values past a threshold where the score should collapse quickly:
    /// returns <paramref name="scoreAtThreshold"/> * exp(-steepness * (value - threshold) / threshold).
    /// At value = 2x threshold with steepness 3 the result is about 5% of the threshold score.
    /// </summary>
    public static double ExponentialFalloff(double value, double threshold, double scoreAtThreshold, double steepness = 3.0)
    {
        if (threshold <= 0) return 0;
        if (value <= threshold) return Clamp(scoreAtThreshold);
        return Clamp(scoreAtThreshold * Math.Exp(-steepness * (value - threshold) / threshold));
    }

    private static double Clamp(double score) => Math.Clamp(score, 0, 100);
}

namespace ServerMonitor.Application.Analytics;

public sealed record LatencyStats(
    int Samples, int Lost, double? Min, double? Max, double? Average,
    double? P50, double? P90, double? P95, double? P99, double? Jitter, double? Current)
{
    public double PacketLossPercent => Samples + Lost == 0 ? 0 : 100.0 * Lost / (Samples + Lost);
    public static readonly LatencyStats Empty = new(0, 0, null, null, null, null, null, null, null, null, null);
}

public static class LatencyStatistics
{
    /// <summary>
    /// Computes latency statistics from successful samples in chronological order.
    /// Percentiles use linear interpolation between closest ranks (same as numpy's default).
    /// Jitter is the mean absolute difference between consecutive samples (RFC 3550 style, unsmoothed).
    /// </summary>
    public static LatencyStats Compute(IReadOnlyList<double> chronologicalSamples, int lost = 0)
    {
        if (chronologicalSamples.Count == 0)
            return LatencyStats.Empty with { Lost = lost };

        var sorted = chronologicalSamples.ToArray();
        Array.Sort(sorted);

        double? jitter = null;
        if (chronologicalSamples.Count > 1)
        {
            double sum = 0;
            for (var i = 1; i < chronologicalSamples.Count; i++)
                sum += Math.Abs(chronologicalSamples[i] - chronologicalSamples[i - 1]);
            jitter = sum / (chronologicalSamples.Count - 1);
        }

        return new LatencyStats(
            chronologicalSamples.Count, lost,
            sorted[0], sorted[^1], chronologicalSamples.Average(),
            Percentile(sorted, 50), Percentile(sorted, 90), Percentile(sorted, 95), Percentile(sorted, 99),
            jitter, chronologicalSamples[^1]);
    }

    /// <summary>Percentile over an already sorted array (linear interpolation).</summary>
    public static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) throw new ArgumentException("No samples", nameof(sorted));
        if (sorted.Length == 1) return sorted[0];
        var rank = percentile / 100.0 * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }
}

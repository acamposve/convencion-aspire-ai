using System;
using System.Collections.Generic;
using System.Linq;

namespace Convenciones.LoadTest;

/// <summary>
/// Latency distribution used by every scenario and by the self-test.
/// </summary>
/// <remarks>
/// The percentile uses linear interpolation between the two closest ranks
/// (<c>index = (n - 1) * p / 100</c>). That is the definition used by the report, and it is pinned by
/// unit tests so two runs cannot disagree because one of them used a different formula.
/// </remarks>
public sealed record LatencySummary(
    double P50,
    double P95,
    double P99,
    double Max,
    double Mean,
    int Count)
{
    public static readonly LatencySummary Empty = new(0, 0, 0, 0, 0, 0);

    public static LatencySummary From(IEnumerable<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var values = samples.Where(value => !double.IsNaN(value)).OrderBy(value => value).ToArray();
        if (values.Length == 0)
        {
            return Empty;
        }

        return new LatencySummary(
            Percentile(values, 50),
            Percentile(values, 95),
            Percentile(values, 99),
            values[^1],
            values.Average(),
            values.Length);
    }

    public static double Percentile(double[] sorted, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        ArgumentOutOfRangeException.ThrowIfNegative(percentile);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 100);

        if (sorted.Length == 0)
        {
            return 0;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var rank = (sorted.Length - 1) * percentile / 100.0;
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = rank - lower;
        return (sorted[lower] * (1 - weight)) + (sorted[upper] * weight);
    }
}

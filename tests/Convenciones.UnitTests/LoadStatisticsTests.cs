using System.Linq;
using Convenciones.LoadTest;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests;

public sealed class LoadStatisticsTests
{
    [Fact]
    public void An_empty_sample_has_no_percentiles()
    {
        var summary = LatencySummary.From([]);

        summary.Count.ShouldBe(0);
        summary.P95.ShouldBe(0);
        summary.Max.ShouldBe(0);
    }

    [Fact]
    public void A_single_sample_is_every_percentile()
    {
        var summary = LatencySummary.From([42.0]);

        summary.Count.ShouldBe(1);
        summary.P50.ShouldBe(42);
        summary.P95.ShouldBe(42);
        summary.P99.ShouldBe(42);
        summary.Max.ShouldBe(42);
        summary.Mean.ShouldBe(42);
    }

    [Fact]
    public void Percentiles_use_linear_interpolation_between_the_closest_ranks()
    {
        // (n - 1) * p / 100: p50 = 1.5 -> 2.5; p95 = 2.85 -> 3.85; p99 = 2.97 -> 3.97.
        var summary = LatencySummary.From([1, 2, 3, 4]);

        summary.P50.ShouldBe(2.5);
        summary.P95.ShouldBe(3.85, 0.0001);
        summary.P99.ShouldBe(3.97, 0.0001);
    }

    [Fact]
    public void Percentiles_do_not_depend_on_the_input_order()
    {
        var shuffled = LatencySummary.From([4, 1, 3, 2]);
        var ordered = LatencySummary.From([1, 2, 3, 4]);

        shuffled.P50.ShouldBe(ordered.P50);
        shuffled.P95.ShouldBe(ordered.P95);
        shuffled.P99.ShouldBe(ordered.P99);
    }

    [Fact]
    public void A_hundred_samples_report_the_expected_ranks()
    {
        var summary = LatencySummary.From(Enumerable.Range(1, 100).Select(value => (double)value));

        summary.Count.ShouldBe(100);
        summary.P50.ShouldBe(50.5);
        summary.P95.ShouldBe(95.05, 0.0001);
        summary.P99.ShouldBe(99.01, 0.0001);
        summary.Max.ShouldBe(100);
    }
}

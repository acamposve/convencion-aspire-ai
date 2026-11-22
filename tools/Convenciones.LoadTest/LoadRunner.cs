using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Convenciones.LoadTest;

/// <summary>
/// Open-loop load generator: requests are launched on a schedule, not one-after-another, so a slow
/// response cannot quietly reduce the offered rate and flatter the latency numbers.
/// </summary>
public sealed class LoadRunner
{
    public sealed record RequestSample(string Kind, string Outcome, double LatencyMs);

    public sealed record Result(
        int Requested,
        int Completed,
        int Succeeded,
        int Failed,
        IReadOnlyList<RequestSample> Samples,
        IReadOnlyDictionary<string, int> Outcomes,
        double ElapsedSeconds,
        double GeneratorCpuCores,
        double? MeanConcurrency,
        int PeakConcurrency)
    {
        public LatencySummary Latency(string? kind = null) =>
            LatencySummary.From(
                Samples.Where(sample => kind is null || sample.Kind == kind).Select(sample => sample.LatencyMs));
    }

    private sealed class State
    {
        public int InFlight;
        public int Peak;
        public long ConcurrencySum;
        public int ConcurrencySamples;
    }

    /// <summary>A request plus the scenario kind it belongs to, so read and write targets stay apart.</summary>
    public readonly record struct LoadRequest(HttpRequestMessage Request, string Kind);

    public static async Task<Result> RunAsync(
        HttpClient client,
        double ratePerSecond,
        TimeSpan duration,
        Func<int, LoadRequest> requestFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratePerSecond);

        var total = (int)Math.Round(ratePerSecond * duration.TotalSeconds, MidpointRounding.AwayFromZero);
        var samples = new ConcurrentBag<RequestSample>();
        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var state = new State();

        int succeeded = 0;
        int failed = 0;
        int completed = 0;

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();

        async Task SendAsync(int index)
        {
            Interlocked.Increment(ref state.InFlight);
            var loadRequest = requestFactory(index);
            var requestStart = Stopwatch.GetTimestamp();

            try
            {
                using var response = await client
                    .SendAsync(loadRequest.Request, HttpCompletionOption.ResponseContentRead, CancellationToken.None)
                    .ConfigureAwait(false);

                var elapsedMs = Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds;
                var outcome = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                samples.Add(new RequestSample(loadRequest.Kind, outcome, elapsedMs));
                Interlocked.Increment(ref completed);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref succeeded);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }

                outcomes.AddOrUpdate(outcome, 1, (_, current) => current + 1);
            }
            catch (Exception exception)
            {
                var outcome = exception.GetType().Name;
                samples.Add(new RequestSample(loadRequest.Kind, outcome, Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds));
                Interlocked.Increment(ref completed);
                Interlocked.Increment(ref failed);
                outcomes.AddOrUpdate(outcome, 1, (_, current) => current + 1);
            }
            finally
            {
                loadRequest.Request.Dispose();
                Interlocked.Decrement(ref state.InFlight);
            }
        }

        using var samplerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampler = Task.Run(
            async () =>
            {
                while (!samplerCancellation.IsCancellationRequested)
                {
                    var current = Volatile.Read(ref state.InFlight);
                    Interlocked.Add(ref state.ConcurrencySum, current);
                    Interlocked.Increment(ref state.ConcurrencySamples);

                    var observed = Volatile.Read(ref state.Peak);
                    while (current > observed)
                    {
                        var previous = Interlocked.CompareExchange(ref state.Peak, current, observed);
                        if (previous == observed)
                        {
                            break;
                        }

                        observed = previous;
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), samplerCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            },
            CancellationToken.None);

        var tasks = new List<Task>(total);
        var launched = 0;

        while (launched < total && !cancellationToken.IsCancellationRequested)
        {
            var due = (int)Math.Min(total, Math.Floor(clock.Elapsed.TotalSeconds * ratePerSecond));
            while (launched < due)
            {
                var index = launched++;
                tasks.Add(Task.Run(() => SendAsync(index), CancellationToken.None));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
        }

        while (launched < total)
        {
            var index = launched++;
            tasks.Add(Task.Run(() => SendAsync(index), CancellationToken.None));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        clock.Stop();

        await samplerCancellation.CancelAsync().ConfigureAwait(false);
        await sampler.ConfigureAwait(false);

        var cpuAfter = process.TotalProcessorTime;
        var elapsedSeconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        var generatorCores = (cpuAfter - cpuBefore).TotalSeconds / elapsedSeconds;

        return new Result(
            Requested: total,
            Completed: completed,
            Succeeded: succeeded,
            Failed: failed,
            Samples: samples.ToArray(),
            Outcomes: new SortedDictionary<string, int>(outcomes, StringComparer.Ordinal),
            ElapsedSeconds: elapsedSeconds,
            GeneratorCpuCores: generatorCores,
            MeanConcurrency: state.ConcurrencySamples == 0
                ? null
                : (double)state.ConcurrencySum / state.ConcurrencySamples,
            PeakConcurrency: state.Peak);
    }
}

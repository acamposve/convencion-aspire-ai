using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Convenciones.LoadTest;

/// <summary>
/// Queue and worker scenarios, hosted in-process with a stub stage.
/// </summary>
/// <remarks>
/// The stub is the provider boundary: it waits two seconds and returns, so the measured time is the
/// queue, the worker loop and the database — not Azure. The real AI latency and cost are out of scope
/// for P2-005 and are reported as not measured. Documents and runs are inserted directly because the
/// queue row is the contract under test; the process endpoint's quota semantics are not.
/// </remarks>
public sealed class WorkerScenarios(PostgresHarness database, LoadFixture fixture)
{
    private static readonly TimeSpan StubLatency = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Seven documents per minute for ten minutes (70 total, the 5x burst), with the worker running:
    /// the queue should barely accumulate, so the queue-wait target applies here.
    /// </summary>
    public async Task<WorkerScenarioReport> BurstAsync(
        int documents,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        const string prefix = "load/burst/";
        const int batches = 10;
        var perBatch = documents / batches;

        var options = Options();
        var queue = new ProcessingQueue(database.WorkerConnectionString, options);
        var worker = new ProcessingWorker(
            queue,
            new StubStage(StubLatency),
            new WorkerHeartbeat(database.WorkerConnectionString),
            options,
            NullLogger<ProcessingWorker>.Instance);

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();

        await worker.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var batch = 0; batch < batches; batch++)
            {
                await fixture.InsertWorkerRunsAsync(database, perBatch, prefix, cancellationToken).ConfigureAwait(false);

                var nextArrival = TimeSpan.FromSeconds(window.TotalSeconds * (batch + 1) / batches);
                var wait = nextArrival - clock.Elapsed;
                if (batch < batches - 1 && wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }

            var maxQueueDepth = await WaitForDrainAsync(prefix, TimeSpan.FromMinutes(5), cancellationToken)
                .ConfigureAwait(false);
            clock.Stop();

            var cpuAfter = process.TotalProcessorTime;
            return await BuildReportAsync(
                    name: "worker-burst",
                    documents: documents,
                    prefix: prefix,
                    clock: clock,
                    cpuSeconds: (cpuAfter - cpuBefore).TotalSeconds,
                    maxQueueDepth: maxQueueDepth,
                    note: $"70 documents arriving over {window.TotalMinutes:0} min with a 2 s stub stage")
                .ConfigureAwait(false);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Five hundred documents queued at once and drained by one worker. The 60 s queue-wait target does
    /// not apply here by construction; the drain target does, and the report says so.
    /// </summary>
    public async Task<WorkerScenarioReport> DrainAsync(
        int documents,
        CancellationToken cancellationToken = default)
    {
        const string prefix = "load/drain/";

        await fixture.InsertWorkerRunsAsync(database, documents, prefix, cancellationToken).ConfigureAwait(false);

        var options = Options();
        var queue = new ProcessingQueue(database.WorkerConnectionString, options);
        var worker = new ProcessingWorker(
            queue,
            new StubStage(StubLatency),
            new WorkerHeartbeat(database.WorkerConnectionString),
            options,
            NullLogger<ProcessingWorker>.Instance);

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();

        await worker.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var maxQueueDepth = await WaitForDrainAsync(prefix, TimeSpan.FromMinutes(30), cancellationToken)
                .ConfigureAwait(false);
            clock.Stop();

            var cpuAfter = process.TotalProcessorTime;
            return await BuildReportAsync(
                    name: "worker-drain",
                    documents: documents,
                    prefix: prefix,
                    clock: clock,
                    cpuSeconds: (cpuAfter - cpuBefore).TotalSeconds,
                    maxQueueDepth: maxQueueDepth,
                    note: $"500 documents queued at once, one worker, 2 s stub stage")
                .ConfigureAwait(false);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<WorkerScenarioReport> BuildReportAsync(
        string name,
        int documents,
        string prefix,
        Stopwatch clock,
        double cpuSeconds,
        int maxQueueDepth,
        string note)
    {
        var rows = await database.QueryAsync(
                """
                select r.status::text,
                       extract(epoch from (r.started_at - r.created_at))::double precision,
                       extract(epoch from (r.finished_at - r.started_at))::double precision
                from platform.processing_run r
                join platform.document d on d.id = r.document_id
                where d.storage_path like @prefix || '%';
                """,
                reader => new
                {
                    Status = reader.GetString(0),
                    QueueWait = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1),
                    RunDuration = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2),
                },
                CancellationToken.None,
                ("prefix", prefix))
            .ConfigureAwait(false);

        var queueWait = rows.Where(row => row.QueueWait.HasValue).Select(row => row.QueueWait!.Value).ToArray();
        var runDuration = rows.Where(row => row.RunDuration.HasValue).Select(row => row.RunDuration!.Value).ToArray();
        var completed = rows.Count(row => row.Status == "Completed");
        var failed = rows.Count(row => row.Status is "Failed" or "Cancelled");
        var elapsed = Math.Max(clock.Elapsed.TotalSeconds, 0.001);

        return new WorkerScenarioReport(
            Name: name,
            Documents: documents,
            Completed: completed,
            Failed: failed,
            DrainSeconds: elapsed,
            DocumentsPerMinute: documents / (elapsed / 60.0),
            QueueWaitSeconds: LatencySummary.From(queueWait),
            RunDurationSeconds: LatencySummary.From(runDuration),
            MaxQueueDepth: maxQueueDepth,
            GeneratorCpuCores: cpuSeconds / elapsed,
            Notes: [note]);
    }

    private async Task<int> WaitForDrainAsync(string prefix, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var giveUpAt = DateTimeOffset.UtcNow + timeout;
        var maxQueueDepth = 0;

        while (DateTimeOffset.UtcNow < giveUpAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pending = await database.ScalarAsync<int>(
                    """
                    select count(*)::int
                    from platform.processing_run r
                    join platform.document d on d.id = r.document_id
                    where d.storage_path like @prefix || '%'
                      and r.status in ('Queued', 'Running');
                    """,
                    cancellationToken,
                    ("prefix", prefix))
                .ConfigureAwait(false);

            var queued = await database.ScalarAsync<int>(
                    """
                    select count(*)::int
                    from platform.processing_run r
                    join platform.document d on d.id = r.document_id
                    where d.storage_path like @prefix || '%'
                      and r.status = 'Queued';
                    """,
                    cancellationToken,
                    ("prefix", prefix))
                .ConfigureAwait(false);

            maxQueueDepth = Math.Max(maxQueueDepth, queued);
            if (pending == 0)
            {
                return maxQueueDepth;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"The queue did not drain within {timeout.TotalMinutes:0} min.");
    }

    private WorkerOptions Options() => new()
    {
        Enabled = true,
        DeadlineUtc = null,
        AllowedCompanyId = fixture.CompanyId,
        PollIntervalSeconds = 1,
        LeaseSeconds = 60,
        AttemptTimeoutSeconds = 300,
        MaxAttempts = 3,
        BackoffSeconds = [0],
        RecoveryIntervalSeconds = 600,
        HealthFile = Path.Combine(Path.GetTempPath(), $"worker-load-{Guid.NewGuid():N}"),
    };

    private sealed class StubStage(TimeSpan latency) : IProcessingStage
    {
        public Task ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
        {
            var jitter = (Random.Shared.NextDouble() - 0.5) * 0.5;
            var delay = latency + TimeSpan.FromMilliseconds(jitter * 1000);
            return Task.Delay(delay, cancellationToken);
        }
    }
}

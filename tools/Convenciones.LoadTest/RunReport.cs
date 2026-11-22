using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Convenciones.LoadTest;

/// <summary>The measured environment. Absolute numbers are only meaningful with it.</summary>
public sealed record EnvironmentInfo(
    string MachineName,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    int LogicalProcessors,
    long GcMemoryLimitBytes,
    string DotNetVersion,
    string? DockerServerVersion,
    string PostgresImage,
    string ApiBaseAddress,
    int ApiConnectionPoolSize,
    int PostgresMaxConnections);

/// <summary>
/// The initial benchmark targets. They are deliberately not called an SLA: they are the thresholds
/// this task measures against so pass/fail has a meaning, and the owner ratifies or replaces them.
/// </summary>
public sealed record TargetSet(
    string Label,
    double ReadP95Ms,
    double ReadP99Ms,
    double WriteP95Ms,
    double WriteP99Ms,
    double UploadP95Ms,
    double UploadP99Ms,
    double MaxServerErrorRatePercent,
    double QueueWaitP95Seconds,
    double RunDurationP95Seconds,
    double DrainSeconds,
    string AvailabilityNote);

public sealed record ApiScenarioReport(
    string Name,
    string Kind,
    int DurationSeconds,
    int Requested,
    int Succeeded,
    int Failed,
    double ThroughputPerSecond,
    double ErrorRatePercent,
    LatencySummary LatencyMs,
    LatencySummary? ReadLatencyMs,
    LatencySummary? WriteLatencyMs,
    LatencySummary? UploadLatencyMs,
    IReadOnlyDictionary<string, int> StatusCodes,
    double GeneratorCpuCores,
    double? MeanConcurrency,
    int PeakConcurrency,
    IReadOnlyList<string> Notes);

public sealed record WorkerScenarioReport(
    string Name,
    int Documents,
    int Completed,
    int Failed,
    double DrainSeconds,
    double DocumentsPerMinute,
    LatencySummary QueueWaitSeconds,
    LatencySummary RunDurationSeconds,
    int MaxQueueDepth,
    double GeneratorCpuCores,
    IReadOnlyList<string> Notes);

public sealed record RunReport(
    string Label,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    EnvironmentInfo Environment,
    TargetSet Targets,
    IReadOnlyList<ApiScenarioReport> ApiScenarios,
    IReadOnlyList<WorkerScenarioReport> WorkerScenarios,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> Fixtures);

public static class RunReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Write(RunReport report, string path)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, Options) + Environment.NewLine);
    }
}

namespace Convenciones.Worker;

/// <summary>Worker configuration, bound from the <c>Worker</c> section.</summary>
public sealed class WorkerOptions
{
    /// <summary>Off until the extraction stage exists; compose still starts the container.</summary>
    public bool Enabled { get; set; }

    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>How long a claimed run stays leased before recovery may reclaim it.</summary>
    public int LeaseSeconds { get; set; } = 600;

    public int AttemptTimeoutSeconds { get; set; } = 600;

    public int MaxAttempts { get; set; } = 3;

    /// <summary>Backoff before the next attempt, indexed by attempt number.</summary>
    public int[] BackoffSeconds { get; set; } = [30, 120];

    public string HealthFile { get; set; } = "/tmp/worker-ready";

    /// <summary>Absolute UTC deadline for the temporary activation; required when Enabled is true.</summary>
    public DateTimeOffset? DeadlineUtc { get; set; }

    /// <summary>True when a non-empty deadline value was configured but could not be parsed.</summary>
    public bool DeadlineInvalid { get; set; }

    public bool DeadlineConfigured => !string.IsNullOrWhiteSpace(DeadlineRaw);

    /// <summary>Raw value, kept only to distinguish missing from invalid.</summary>
    public string? DeadlineRaw { get; set; }
}

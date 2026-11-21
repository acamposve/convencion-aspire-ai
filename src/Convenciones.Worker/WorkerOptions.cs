namespace Convenciones.Worker;

/// <summary>Worker configuration, bound from the <c>Worker</c> section.</summary>
public sealed class WorkerOptions
{
    /// <summary>Off until the extraction stage exists; compose still starts the container.</summary>
    public bool Enabled { get; set; }

    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// How often an enabled worker sweeps for runs whose lease expired. Recovery also runs at startup;
    /// this is the interval for the rest of the process lifetime.
    /// </summary>
    public int RecoveryIntervalSeconds { get; set; } = 60;

    /// <summary>How often an enabled worker settles submitted financial reservations whose response never arrived.</summary>
    public int SweepIntervalSeconds { get; set; } = 300;

    /// <summary>How long a claimed run stays leased before recovery may reclaim it.</summary>
    public int LeaseSeconds { get; set; } = 600;

    public int AttemptTimeoutSeconds { get; set; } = 600;

    public int MaxAttempts { get; set; } = 3;

    /// <summary>Backoff before the next attempt, indexed by attempt number.</summary>
    public int[] BackoffSeconds { get; set; } = [30, 120];

    public string HealthFile { get; set; } = "/tmp/worker-ready";

    /// <summary>Absolute UTC deadline for the temporary activation; required when Enabled is true.</summary>
    public DateTimeOffset? DeadlineUtc { get; set; }

    /// <summary>
    /// Optional fail-closed scope for staging: when set, only this document may be claimed.
    /// Null preserves the normal production behaviour.
    /// </summary>
    public Guid? AllowedDocumentId { get; set; }

    /// <summary>
    /// Optional fail-closed scope for staging: when set, only this run may be claimed.
    /// Null preserves the normal production behaviour.
    /// </summary>
    public Guid? AllowedRunId { get; set; }

    /// <summary>
    /// Optional staging tenant scope. When set, the document and run must belong to this company.
    /// </summary>
    public Guid? AllowedCompanyId { get; set; }

    /// <summary>
    /// Static staging allowlist. An operator-supplied AllowedCompanyId must match this value.
    /// </summary>
    public Guid? AuthorizedCompanyId { get; set; }

    /// <summary>True when an allowed scope value was configured but is not a valid GUID.</summary>
    public bool ScopeInvalid { get; set; }

    /// <summary>
    /// When true, an enabled worker must have AllowedDocumentId or AllowedRunId. Staging activation
    /// sets this; production keeps it false and preserves the existing global claim.
    /// </summary>
    public bool RequireScope { get; set; }

    /// <summary>True when a non-empty deadline value was configured but could not be parsed.</summary>
    public bool DeadlineInvalid { get; set; }

    public bool DeadlineConfigured => !string.IsNullOrWhiteSpace(DeadlineRaw);

    /// <summary>Raw value, kept only to distinguish missing from invalid.</summary>
    public string? DeadlineRaw { get; set; }
}

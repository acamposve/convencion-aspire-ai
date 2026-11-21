namespace Convenciones.Worker;

/// <summary>
/// Fail-closed validation for the temporary worker activation. A disabled worker does not need a
/// deadline; an enabled worker without a valid future deadline must not start.
/// </summary>
public static class WorkerDeadlinePolicy
{
    public static string? Validate(WorkerOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return null;
        }

        if (!options.DeadlineConfigured)
        {
            return "Worker:DeadlineUtc is required when Worker:Enabled is true.";
        }

        if (options.DeadlineInvalid || options.DeadlineUtc is null)
        {
            return "Worker:DeadlineUtc is not a valid ISO-8601 timestamp.";
        }

        if (options.DeadlineUtc <= now)
        {
            return "Worker:DeadlineUtc has already expired.";
        }

        return null;
    }

    /// <summary>
    /// Decides whether a cancelled attempt is recorded as a deadline failure.
    /// </summary>
    /// <remarks>
    /// A timeout shortened by the deadline is deadline-bound by construction, so the wall-clock
    /// comparison is only a fallback for other cancellation sources. The timer is monotonic while
    /// <c>DeadlineUtc</c> is wall-clock time; the two can differ by a few milliseconds while the
    /// clock is slewed, and a timer that fires just before the deadline must not be recorded as a
    /// retryable attempt timeout: the worker exits on the deadline and the run would never retry.
    /// </remarks>
    public static bool IsDeadlineFailure(
        bool deadlineBoundTimeout,
        DateTimeOffset? deadlineUtc,
        DateTimeOffset now) =>
        deadlineBoundTimeout || (deadlineUtc is { } deadline && now >= deadline);
}

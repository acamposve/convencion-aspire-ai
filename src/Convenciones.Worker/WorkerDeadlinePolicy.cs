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
}

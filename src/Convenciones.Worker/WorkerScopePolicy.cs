namespace Convenciones.Worker;

/// <summary>
/// Fail-closed validation for the optional staging claim scope. Production leaves RequireScope false
/// and keeps the existing global claim; the staging override sets it true and requires a target.
/// </summary>
public static class WorkerScopePolicy
{
    public static string? Validate(WorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ScopeInvalid)
        {
            return "Worker:AllowedDocumentId and Worker:AllowedRunId must be valid GUIDs when configured.";
        }

        if (options.Enabled
            && options.RequireScope
            && options.AuthorizedCompanyId is null)
        {
            return "Worker:AuthorizedCompanyId is required when Worker:RequireScope is true.";
        }

        if (options.Enabled
            && options.RequireScope
            && options.AllowedCompanyId is null)
        {
            return "Worker:AllowedCompanyId is required when Worker:RequireScope is true.";
        }

        if (options.Enabled
            && options.RequireScope
            && options.AllowedDocumentId is null
            && options.AllowedRunId is null)
        {
            return "Worker:AllowedDocumentId or Worker:AllowedRunId is required when Worker:RequireScope is true.";
        }

        return null;
    }
}

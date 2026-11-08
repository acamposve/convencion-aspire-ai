using Npgsql;

namespace Convenciones.Worker;

/// <summary>
/// Fail-closed validation of the staging claim scope against the stored document/run relationships.
/// The worker must not start until the allowed company, document and run agree.
/// </summary>
public static class WorkerScopeValidator
{
    public static async Task<string?> ValidateAsync(
        string connectionString,
        WorkerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.RequireScope)
        {
            return null;
        }

        if (options.AllowedCompanyId is not { } companyId)
        {
            return "Worker:AllowedCompanyId is required when Worker:RequireScope is true.";
        }

        if (options.AuthorizedCompanyId is not { } authorizedCompanyId)
        {
            return "Worker:AuthorizedCompanyId is required when Worker:RequireScope is true.";
        }

        if (companyId != authorizedCompanyId)
        {
            return "Worker:AllowedCompanyId is not the authorized staging company.";
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var company = new NpgsqlCommand(
            "select exists(select 1 from app.company where id = @company);", connection))
        {
            company.Parameters.AddWithValue("company", companyId);
            if (await company.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return "Worker:AllowedCompanyId does not identify an existing company.";
            }
        }

        if (options.AllowedDocumentId is { } documentId)
        {
            await using var document = new NpgsqlCommand(
                "select company_id from platform.document where id = @document and deleted_at is null;",
                connection);
            document.Parameters.AddWithValue("document", documentId);
            var owner = await document.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (owner is not Guid documentCompany)
            {
                return "Worker:AllowedDocumentId does not identify an existing document.";
            }

            if (documentCompany != companyId)
            {
                return "Worker:AllowedDocumentId does not belong to Worker:AllowedCompanyId.";
            }
        }

        if (options.AllowedRunId is { } runId)
        {
            await using var run = new NpgsqlCommand(
                """
                select r.document_id, r.company_id, d.company_id
                from platform.processing_run r
                join platform.document d on d.id = r.document_id
                where r.id = @run
                  and d.deleted_at is null;
                """, connection);
            run.Parameters.AddWithValue("run", runId);
            await using var reader = await run.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return "Worker:AllowedRunId does not identify an existing run.";
            }

            var runDocument = reader.GetGuid(0);
            var runCompany = reader.GetGuid(1);
            var documentCompany = reader.GetGuid(2);

            if (options.AllowedDocumentId is { } expectedDocument && runDocument != expectedDocument)
            {
                return "Worker:AllowedRunId does not belong to Worker:AllowedDocumentId.";
            }

            if (runCompany != companyId || documentCompany != companyId)
            {
                return "Worker:AllowedRunId does not belong to Worker:AllowedCompanyId.";
            }
        }

        return null;
    }
}

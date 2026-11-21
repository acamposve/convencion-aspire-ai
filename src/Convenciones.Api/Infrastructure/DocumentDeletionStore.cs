using Convenciones.Documents;
using Npgsql;

namespace Convenciones.Api.Infrastructure;

/// <summary>Configuration for the durable document object deletion sweeper.</summary>
public sealed class DocumentDeletionOptions
{
    /// <summary>Seconds between sweeps; zero or negative disables the sweeper.</summary>
    public int IntervalSeconds { get; set; } = 60;

    public int BatchSize { get; set; } = 10;

    /// <summary>How long a claimed row is leased so two instances do not process it at once.</summary>
    public int LeaseSeconds { get; set; } = 300;
}

/// <summary>
/// Reads and completes durable document object deletions.
/// </summary>
/// <remarks>
/// The API request inserts the intent in the same transaction as the soft delete. This store performs
/// the storage call and records the outcome through <c>platform.record_document_object_deletion</c>, which
/// also applies the exponential backoff. The sweeper claims rows through
/// <c>platform.claim_document_object_deletions</c> (SECURITY DEFINER, because a JWT-less background task
/// cannot satisfy an <c>is_active_user()</c> policy).
/// </remarks>
public sealed class DocumentDeletionStore(string connectionString)
{
    /// <summary>Claims due rows and attempts each with <paramref name="storage"/>.</summary>
    public async Task<int> SweepOnceAsync(
        int batchSize,
        TimeSpan lease,
        IDocumentStorage storage,
        CancellationToken cancellationToken)
    {
        var claimed = new List<(Guid DocumentId, string StoragePath)>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var claim = new NpgsqlCommand(
            "select document_id, storage_path from platform.claim_document_object_deletions(@batch, @lease);",
            connection))
        {
            claim.Parameters.AddWithValue("batch", Math.Max(1, batchSize));
            claim.Parameters.AddWithValue("lease", Math.Max(30, (int)lease.TotalSeconds));

            await using var reader = await claim.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                claimed.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        foreach (var item in claimed)
        {
            await AttemptAsync(connection, item.DocumentId, item.StoragePath, storage, cancellationToken)
                .ConfigureAwait(false);
        }

        return claimed.Count;
    }

    /// <summary>Attempts the deletion for one intent, recording success or a scheduled retry.</summary>
    public async Task TryDeleteNowAsync(
        Guid documentId,
        string storagePath,
        IDocumentStorage storage,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await AttemptAsync(connection, documentId, storagePath, storage, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AttemptAsync(
        NpgsqlConnection connection,
        Guid documentId,
        string storagePath,
        IDocumentStorage storage,
        CancellationToken cancellationToken)
    {
        var succeeded = false;
        string? error = null;

        try
        {
            // A missing object is the caller's goal, not a failure: the storage adapter treats 404 as
            // success, and any other exception becomes a scheduled retry.
            await storage.DeleteAsync(storagePath, cancellationToken).ConfigureAwait(false);
            succeeded = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = exception.Message;
        }

        await using var record = new NpgsqlCommand(
            "select platform.record_document_object_deletion(@document, @succeeded, @error);",
            connection);
        record.Parameters.AddWithValue("document", documentId);
        record.Parameters.AddWithValue("succeeded", succeeded);
        record.Parameters.Add(new NpgsqlParameter("error", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)error ?? DBNull.Value,
        });

        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

using Convenciones.Api.Infrastructure;
using Convenciones.Catalog;
using Microsoft.Extensions.Configuration;
using Convenciones.Clauses;
using Convenciones.Documents;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Convenciones.Api.Endpoints;

/// <summary>Processing runs, extracted clauses, review and the taxonomy read-only surface.</summary>
public static class ProcessingEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapProcessingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/documents/{id:guid}/process", StartAsync)
            .RequiresCapability("clause.process")
            .WithTags("Processing")
            .WithName("StartDocumentProcessing");

        app.MapDelete("/documents/{id:guid}/processing", PurgeAsync)
            .RequiresCapability("clause.manage")
            .WithTags("Processing")
            .WithName("PurgeDocumentProcessing");

        app.MapGet("/documents/{id:guid}/processing", GetLatestAsync)
            .RequiresCapability("clause.read")
            .WithTags("Processing")
            .WithName("GetDocumentProcessing");

        app.MapGet("/processing/{id:guid}", GetRunAsync)
            .RequiresCapability("clause.read")
            .WithTags("Processing")
            .WithName("GetProcessingRun");

        app.MapGet("/documents/{id:guid}/clauses", ListClausesAsync)
            .RequiresCapability("clause.read")
            .WithTags("Processing")
            .WithName("ListDocumentClauses");

        app.MapGet("/clauses/{id:guid}", GetClauseAsync)
            .RequiresCapability("clause.read")
            .WithTags("Processing")
            .WithName("GetClause");

        app.MapPatch("/clauses/{id:guid}", ReviewAsync)
            .RequiresCapability("clause.review")
            .WithTags("Processing")
            .WithName("ReviewClause");

        app.MapGet("/clause-categories", ListCategoriesAsync)
            .RequiresCapability("clause.read")
            .WithTags("Processing")
            .WithName("ListClauseCategories");

        return app;
    }

    private static async Task<IResult> StartAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        ProcessingQuotaService quotas,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (await DocumentAccess.FindAccessibleAsync(session, id, db, cancellationToken).ConfigureAwait(false) is not { } document)
        {
            return NotFoundDocument(id);
        }

        if (document.CompanyId is not { } companyId)
        {
            return State("invalid_state", "The document has no company.");
        }

        var hasText = await db.DocumentTexts.AnyAsync(text => text.DocumentId == id, cancellationToken).ConfigureAwait(false);
        if (!hasText)
        {
            return State("text_not_extracted", "Extract the document text before processing it.");
        }

        await using var startTransaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireDocumentLockAsync(db, id, shared: false, cancellationToken).ConfigureAwait(false);
        if (!await DocumentIsActiveAsync(db, id, cancellationToken).ConfigureAwait(false))
        {
            return NotFoundDocument(id);
        }

        var existing = await db.ProcessingRuns
            .AsNoTracking()
            .Where(run => run.DocumentId == id
                && (run.Status == ProcessingStatus.Queued || run.Status == ProcessingStatus.Running))
            .OrderByDescending(run => run.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Results.Accepted($"/processing/{existing.Id}", ToResponse(existing));
        }

        var maxActiveRuns = configuration.GetValue<int?>("Processing:MaxActiveRunsPerCompany") ?? 1;

        // The reservation joins this transaction: the counter increment, the active-run check and the
        // run insert either commit together or not at all.
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        var quota = await quotas.TryReserveAsync(connection, transaction, companyId, maxActiveRuns, cancellationToken).ConfigureAwait(false);
        if (!quota.Allowed)
        {
            await startTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return Problem(
                StatusCodes.Status409Conflict,
                quota.Code ?? "quota_exceeded",
                quota.Detail ?? "The monthly processing quota is exhausted.");
        }

        var run = ProcessingRun.Create(id, companyId, session.UserId, DateTimeOffset.UtcNow);
        db.ProcessingRuns.Add(run);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await startTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            await startTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            // Two concurrent clicks: the partial unique index chose one; return that one.
            var winner = await db.ProcessingRuns
                .AsNoTracking()
                .Where(candidate => candidate.DocumentId == id
                    && (candidate.Status == ProcessingStatus.Queued || candidate.Status == ProcessingStatus.Running))
                .OrderByDescending(candidate => candidate.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return winner is null
                ? State("already_processing", "A processing run already exists.")
                : Results.Accepted($"/processing/{winner.Id}", ToResponse(winner!));
        }

        return Results.Accepted($"/processing/{run.Id}", ToResponse(run));
    }

    private static async Task<IResult> PurgeAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        ProcessingRetentionService retention,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (await DocumentAccess.FindAccessibleAsync(session, id, db, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFoundDocument(id);
        }

        try
        {
            await retention.PurgeDocumentAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessingRetentionService.DocumentDeletedException)
        {
            return NotFoundDocument(id);
        }
        catch (ProcessingRetentionService.ClassificationCorrectionsPresentException)
        {
            return State(
                "classification_corrections_present",
                "Purge would orphan human classification corrections.");
        }

        return Results.NoContent();
    }
    private static async Task<IResult> GetLatestAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        ProcessingQuotaService quotas,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (await DocumentAccess.FindAccessibleAsync(session, id, db, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFoundDocument(id);
        }

        var run = await db.ProcessingRuns
            .AsNoTracking()
            .Where(candidate => candidate.DocumentId == id)
            .OrderByDescending(candidate => candidate.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return run is null ? NotFoundRun(id) : Results.Ok(ToResponse(run));
    }

    private static async Task<IResult> GetRunAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        ProcessingQuotaService quotas,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        var run = await db.ProcessingRuns.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (run is null || !TenantScope.CanAccess(session, run.CompanyId))
        {
            return NotFoundRun(id);
        }

        return await DocumentIsActiveAsync(db, run.DocumentId, cancellationToken).ConfigureAwait(false)
            ? Results.Ok(ToResponse(run))
            : NotFoundRun(id);
    }

    private static async Task<IResult> ListClausesAsync(
        Guid id,
        int? page,
        int? pageSize,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (await DocumentAccess.FindAccessibleAsync(session, id, db, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFoundDocument(id);
        }

        var currentPage = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        await using var readTransaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireDocumentLockAsync(db, id, shared: true, cancellationToken).ConfigureAwait(false);
        if (!await DocumentIsActiveAsync(db, id, cancellationToken).ConfigureAwait(false))
        {
            return NotFoundDocument(id);
        }

        var currentRun = await FindCurrentRunAsync(db, id, cancellationToken).ConfigureAwait(false);
        if (currentRun is null)
        {
            await readTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new ProcessingClausePage([], currentPage, size, 0));
        }

        var query = db.ExtractedClauses.AsNoTracking()
            .Where(clause => clause.DocumentId == id && clause.RunId == currentRun.Id);

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderBy(clause => clause.Ordinal)
            .Skip((currentPage - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var classification = await LoadClassificationProjectionAsync(
            db,
            rows.Select(clause => clause.Id).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (!classification.Consistent)
        {
            return ClassificationAuditInconsistent();
        }

        var items = rows
            .Select(clause => ToProcessingClauseResponse(
                clause,
                classification.ByClause.GetValueOrDefault(clause.Id) ?? [],
                classification.Versions.GetValueOrDefault(clause.Id),
                classification.AiFingerprints.GetValueOrDefault(clause.Id, string.Empty)))
            .ToArray();
        await readTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(new ProcessingClausePage(items, currentPage, size, total));
    }

    private static async Task<IResult> GetClauseAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        ProcessingQuotaService quotas,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        var clause = await db.ExtractedClauses.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (clause is null || !TenantScope.CanAccess(session, clause.CompanyId))
        {
            return NotFoundClause(id);
        }

        await using var readTransaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireDocumentLockAsync(db, clause.DocumentId, shared: true, cancellationToken).ConfigureAwait(false);
        if (!await DocumentIsActiveAsync(db, clause.DocumentId, cancellationToken).ConfigureAwait(false))
        {
            return NotFoundClause(id);
        }

        var currentRun = await FindCurrentRunAsync(db, clause.DocumentId, cancellationToken).ConfigureAwait(false);
        if (currentRun is null || clause.RunId != currentRun.Id)
        {
            return NotFoundClause(id);
        }

        var classification = await LoadClassificationProjectionAsync(
            db,
            [id],
            cancellationToken).ConfigureAwait(false);
        if (!classification.Consistent)
        {
            return ClassificationAuditInconsistent();
        }

        await readTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToProcessingClauseResponse(
            clause!,
            classification.ByClause.GetValueOrDefault(id) ?? [],
            classification.Versions.GetValueOrDefault(id),
            classification.AiFingerprints.GetValueOrDefault(id, string.Empty)));
    }

    private static async Task<IResult> ReviewAsync(
        Guid id,
        ReviewClauseRequest request,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (!Enum.TryParse<ClauseReviewStatus>(request.Status, ignoreCase: true, out var status))
        {
            return Problem(StatusCodes.Status400BadRequest, "validation_failed", "Unknown review status.");
        }

        var clause = await db.ExtractedClauses
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (clause is null || !TenantScope.CanAccess(session, clause.CompanyId))
        {
            return NotFoundClause(id);
        }

        if (request.Classifications is null)
        {
            await using var normalReviewTransaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await AcquireDocumentLockAsync(db, clause.DocumentId, shared: true, cancellationToken).ConfigureAwait(false);
                await AcquireRunLockAsync(db, clause.RunId, shared: true, cancellationToken).ConfigureAwait(false);
                await AcquireClauseLockAsync(db, clause.Id, cancellationToken).ConfigureAwait(false);

                var reviewedClause = await ReloadLockedClauseAsync(db, clause, cancellationToken).ConfigureAwait(false);
                if (reviewedClause is null)
                {
                    await normalReviewTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return NotFoundClause(id);
                }

                if (!await DocumentIsActiveAsync(db, reviewedClause.DocumentId, cancellationToken).ConfigureAwait(false))
                {
                    await normalReviewTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return NotFoundClause(id);
                }

                var currentRun = await FindCurrentRunAsync(
                    db,
                    reviewedClause.DocumentId,
                    cancellationToken).ConfigureAwait(false);
                if (currentRun is null || reviewedClause.RunId != currentRun.Id)
                {
                    await normalReviewTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return ClauseNotCurrent(id);
                }

                var visible = await LoadClassificationProjectionAsync(
                    db,
                    [id],
                    cancellationToken).ConfigureAwait(false);
                if (!visible.Consistent)
                {
                    await normalReviewTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return ClassificationAuditInconsistent();
                }

                reviewedClause.Review(status, request.Text, request.Heading);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await normalReviewTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return Results.Ok(ToProcessingClauseResponse(
                    reviewedClause,
                    visible.ByClause.GetValueOrDefault(id) ?? [],
                    visible.Versions.GetValueOrDefault(id),
                    visible.AiFingerprints.GetValueOrDefault(id, string.Empty)));
            }
            catch
            {
                await normalReviewTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        if (status is not ClauseReviewStatus.Corrected)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "validation_failed",
                "A classification correction requires review status Corrected.");
        }

        var categories = await db.ClauseCategories.AsNoTracking()
            .Where(category => category.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byKey = categories.ToDictionary(category => category.Key, StringComparer.OrdinalIgnoreCase);

        var requested = new List<ClassificationEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.Classifications)
        {
            var key = item.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key) || !byKey.TryGetValue(key, out var definition))
            {
                return Problem(
                    StatusCodes.Status400BadRequest,
                    "validation_failed",
                    $"Unknown clause category '{item.Key}'.");
            }

            if (!seen.Add(definition.Key))
            {
                return Problem(
                    StatusCodes.Status400BadRequest,
                    "validation_failed",
                    $"Duplicate clause category '{definition.Key}'.");
            }

            var confidence = item.Confidence ?? 1.0;
            if (confidence is < 0 or > 1)
            {
                return Problem(
                    StatusCodes.Status400BadRequest,
                    "validation_failed",
                    "Classification confidence must be between 0 and 1.");
            }

            var rationale = item.Rationale;
            if (rationale is { Length: > 240 })
            {
                rationale = rationale[..240];
            }

            requested.Add(new ClassificationEntry(definition.Key, confidence, false, rationale));
        }

        if (requested.Count == 0)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "validation_failed",
                "At least one clause category is required.");
        }

        var primary = request.Primary?.Trim();
        if (string.IsNullOrWhiteSpace(primary))
        {
            primary = requested[0].Key;
        }
        else if (!seen.Contains(primary))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "validation_failed",
                "The primary category must be one of the selected categories.");
        }

        requested = requested
            .Select(item => item with { IsPrimary = string.Equals(item.Key, primary, StringComparison.OrdinalIgnoreCase) })
            .ToList();

        if (request.ExpectedVersion is not { } expectedVersion)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "validation_failed",
                "ExpectedVersion is required for a classification correction.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ExtractedClause? currentClause = null;
        try
        {
            // Document, run, then clause. Re-extraction and purge take the exclusive document/run
            // locks, so they cannot replace this clause while the correction is validating and
            // appending its audit.
            await AcquireDocumentLockAsync(db, clause.DocumentId, shared: true, cancellationToken).ConfigureAwait(false);
            await AcquireRunLockAsync(db, clause.RunId, shared: true, cancellationToken).ConfigureAwait(false);
            await AcquireClauseLockAsync(db, clause.Id, cancellationToken).ConfigureAwait(false);

            currentClause = await ReloadLockedClauseAsync(db, clause, cancellationToken).ConfigureAwait(false);
            if (currentClause is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return NotFoundClause(id);
            }

            if (!await DocumentIsActiveAsync(db, currentClause.DocumentId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return NotFoundClause(id);
            }

            var currentRun = await FindCurrentRunAsync(
                db,
                currentClause.DocumentId,
                cancellationToken).ConfigureAwait(false);
            if (currentRun is null || currentClause.RunId != currentRun.Id)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return ClauseNotCurrent(id);
            }

            var currentProjection = await LoadClassificationProjectionAsync(
                db,
                [id],
                cancellationToken).ConfigureAwait(false);
            if (!currentProjection.Consistent)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return ClassificationAuditInconsistent();
            }

            var current = currentProjection.ByClause.GetValueOrDefault(id) ?? [];
            var currentVersion = BuildClassificationVersion(
                currentClause.RunId,
                currentProjection.Versions.GetValueOrDefault(id),
                currentProjection.AiFingerprints.GetValueOrDefault(id, string.Empty),
                current);
            if (expectedVersion != currentVersion)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return ClassificationVersionConflict(currentVersion);
            }
            var previous = current
                .Select(item => new ClassificationEntry(item.Key, item.Confidence, item.IsPrimary, item.Rationale))
                .ToList();
            var previousPrimary = previous.FirstOrDefault(item => item.IsPrimary)?.Key;
            var fromState = current.Any(item => item.Source == "human") ? "human" : "ai";

            var changes = JsonSerializer.Serialize(new
            {
                previous = previous.Select(item => new
                {
                    key = item.Key,
                    confidence = item.Confidence,
                    isPrimary = item.IsPrimary,
                    rationale = item.Rationale,
                }),
                next = requested.Select(item => new
                {
                    key = item.Key,
                    confidence = item.Confidence,
                    isPrimary = item.IsPrimary,
                    rationale = item.Rationale,
                }),
                previousPrimary,
                primary,
            });

            db.AuditEntries.Add(AuditEntry.Record(
                session.UserId,
                "documents",
                "extracted_clause",
                currentClause.Id.ToString(),
                "clause.classification.corrected",
                changes,
                System.Diagnostics.Activity.Current?.TraceId.ToString(),
                fromState: fromState,
                toState: "human"));

            currentClause.Review(status, request.Text, request.Heading);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        var effective = await LoadClassificationProjectionAsync(
            db,
            [id],
            cancellationToken).ConfigureAwait(false);
        if (!effective.Consistent)
        {
            return ClassificationAuditInconsistent();
        }

        return Results.Ok(ToProcessingClauseResponse(
            currentClause!,
            effective.ByClause.GetValueOrDefault(id) ?? [],
            effective.Versions.GetValueOrDefault(id),
            effective.AiFingerprints.GetValueOrDefault(id, string.Empty)));
    }
    private static async Task<IResult> ListCategoriesAsync(
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        if (accessor.Session is null)
        {
            return Forbidden();
        }

        var categories = await db.ClauseCategories.AsNoTracking()
            .Where(category => category.IsActive)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Name)
            .Select(category => new ClauseCategoryResponse(
                category.Key,
                category.Name,
                category.Description,
                category.ParentId,
                category.SortOrder))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(categories);
    }

    private sealed record ClassificationProjection(
        Dictionary<Guid, IReadOnlyList<ProcessingClassificationResponse>> ByClause,
        Dictionary<Guid, long> Versions,
        Dictionary<Guid, string> AiFingerprints,
        bool Consistent);

    private static async Task<ClassificationProjection> LoadClassificationProjectionAsync(
        ConvencionesDbContext db,
        IReadOnlyCollection<Guid> clauseIds,
        CancellationToken cancellationToken)
    {
        var projection = new ClassificationProjection([], [], [], true);
        if (clauseIds.Count == 0)
        {
            return projection;
        }

        var categoryNames = await db.ClauseCategories.AsNoTracking()
            .Select(category => new { category.Key, category.Name })
            .ToDictionaryAsync(
                category => category.Key,
                category => category.Name,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken)
            .ConfigureAwait(false);

        var aiRows = await db.ClauseClassifications.AsNoTracking()
            .Where(classification => clauseIds.Contains(classification.ClauseId))
            .Join(
                db.ClauseCategories.AsNoTracking(),
                classification => classification.CategoryId,
                category => category.Id,
                (classification, category) => new
                {
                    classification.ClauseId,
                    category.Key,
                    category.Name,
                    classification.Confidence,
                    classification.IsPrimary,
                    classification.Rationale,
                    classification.Model,
                    classification.PromptVersion,
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var aiByClause = aiRows
            .GroupBy(row => row.ClauseId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ProcessingClassificationResponse>)group
                    .Select(row => new ProcessingClassificationResponse(
                        row.Key,
                        row.Name,
                        (double?)row.Confidence,
                        row.IsPrimary,
                        row.Rationale,
                        "ai"))
                    .ToList());
        var aiFingerprints = aiRows
            .GroupBy(row => row.ClauseId)
            .ToDictionary(
                group => group.Key,
                group => BuildAiFingerprint(group.Select(row => new AiFingerprintRow(
                    row.Key,
                    row.Name,
                    row.Confidence,
                    row.IsPrimary,
                    row.Rationale,
                    row.Model,
                    row.PromptVersion))));

        var idStrings = clauseIds.Select(id => id.ToString()).ToArray();
        // DISTINCT ON is deliberate: it makes PostgreSQL return exactly one latest correction per
        // clause. EF Core cannot turn the GroupBy/First pattern into a provider-independent promise,
        // and the raw query keeps the deterministic (occurred_at, id) ordering explicit.
        var latestCorrections = await db.AuditEntries
            .FromSql($"""
                select distinct on (entity_id) *
                from platform.audit_entry
                where entity_type = 'extracted_clause'
                  and action = 'clause.classification.corrected'
                  and entity_id = any({idStrings})
                order by entity_id, occurred_at desc, id desc
                """)
            .AsNoTracking()
            .Select(entry => new
            {
                entry.EntityId,
                entry.Id,
                entry.Changes,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var corrections = new Dictionary<Guid, IReadOnlyList<ProcessingClassificationResponse>>();
        var versions = new Dictionary<Guid, long>();
        foreach (var row in latestCorrections)
        {
            if (!Guid.TryParse(row.EntityId, out var clauseId)
                || !TryParseCorrection(row.Changes, categoryNames, out var parsed))
            {
                // The latest correction exists but cannot be interpreted. Do not fall back to
                // the older AI row: the effective classification is unknown and the read must fail
                // closed instead of presenting stale data.
                projection = projection with { Consistent = false };
                continue;
            }

            corrections[clauseId] = parsed;
            versions[clauseId] = row.Id;
        }

        var byClause = new Dictionary<Guid, IReadOnlyList<ProcessingClassificationResponse>>();
        foreach (var clauseId in clauseIds)
        {
            byClause[clauseId] = corrections.GetValueOrDefault(clauseId)
                ?? aiByClause.GetValueOrDefault(clauseId)
                ?? [];
        }

        return projection with
        {
            ByClause = byClause,
            Versions = versions,
            AiFingerprints = aiFingerprints,
            Consistent = projection.Consistent,
        };
    }

    private static bool TryParseCorrection(
        string? changes,
        IReadOnlyDictionary<string, string> categoryNames,
        out IReadOnlyList<ProcessingClassificationResponse> parsed)
    {
        parsed = [];
        if (string.IsNullOrWhiteSpace(changes))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(changes);
            if (!document.RootElement.TryGetProperty("next", out var next)
                || next.ValueKind is not JsonValueKind.Array)
            {
                return false;
            }

            var items = new List<ProcessingClassificationResponse>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryCount = 0;
            foreach (var item in next.EnumerateArray())
            {
                if (!item.TryGetProperty("key", out var keyElement)
                    || keyElement.ValueKind is not JsonValueKind.String
                    || keyElement.GetString() is not { } key
                    || !categoryNames.TryGetValue(key, out var name)
                    || !seen.Add(key))
                {
                    return false;
                }

                if (!item.TryGetProperty("confidence", out var confidenceElement)
                    || confidenceElement.ValueKind is not JsonValueKind.Number
                    || !confidenceElement.TryGetDouble(out var confidence)
                    || !double.IsFinite(confidence)
                    || confidence is < 0 or > 1)
                {
                    return false;
                }

                if (!item.TryGetProperty("isPrimary", out var primaryElement)
                    || primaryElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                var isPrimary = primaryElement.GetBoolean();
                if (isPrimary)
                {
                    primaryCount++;
                }

                string? rationale = null;
                if (item.TryGetProperty("rationale", out var rationaleElement))
                {
                    if (rationaleElement.ValueKind is JsonValueKind.String)
                    {
                        rationale = rationaleElement.GetString();
                        if (rationale is { Length: > 240 })
                        {
                            return false;
                        }
                    }
                    else if (rationaleElement.ValueKind is not JsonValueKind.Null)
                    {
                        return false;
                    }
                }

                items.Add(new ProcessingClassificationResponse(
                    key,
                    name,
                    confidence,
                    isPrimary,
                    rationale,
                    "human"));
            }

            if (items.Count == 0 || primaryCount != 1)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("primary", out var primaryField)
                || primaryField.ValueKind is not JsonValueKind.String
                || primaryField.GetString() is not { } primary
                || !items.Any(item => string.Equals(item.Key, primary, StringComparison.OrdinalIgnoreCase)
                    && item.IsPrimary))
            {
                return false;
            }

            parsed = items;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record ClassificationEntry(
        string Key,
        double? Confidence,
        bool IsPrimary,
        string? Rationale);

    private sealed record AiFingerprintRow(
        string Key,
        string Name,
        decimal? Confidence,
        bool IsPrimary,
        string? Rationale,
        string? Model,
        string? PromptVersion);
    private static ProcessingRunResponse ToResponse(ProcessingRun run) =>
        new(
            run.Id,
            run.DocumentId,
            run.Status.ToString(),
            run.Stage,
            run.Attempts,
            run.ClauseCount,
            run.ClassifiedCount,
            run.UnclassifiedCount,
            run.ErrorCode,
            run.ErrorMessage,
            run.CreatedAt,
            run.StartedAt,
            run.FinishedAt);

    private static ProcessingClauseResponse ToProcessingClauseResponse(
        ExtractedClause clause,
        IReadOnlyList<ProcessingClassificationResponse> classifications,
        long correctionVersion,
        string aiFingerprint) =>
        new(
            clause.Id,
            clause.Ordinal,
            clause.ClauseNumber,
            clause.Heading,
            clause.Text,
            clause.PageFrom,
            clause.PageTo,
            clause.SourceCharStart,
            clause.SourceCharEnd,
            (double?)clause.Confidence,
            clause.NeedsReview,
            clause.ReviewStatus.ToString(),
            classifications,
            BuildClassificationVersion(clause.RunId, correctionVersion, aiFingerprint, classifications));

    private static string BuildAiFingerprint(IEnumerable<AiFingerprintRow> rows)
    {
        var canonical = JsonSerializer.Serialize(rows
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => new
            {
                row.Key,
                row.Name,
                Confidence = row.Confidence?.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                row.IsPrimary,
                row.Rationale,
                row.Model,
                row.PromptVersion,
            })
            .ToList());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string BuildClassificationVersion(
        Guid runId,
        long correctionVersion,
        string aiFingerprint,
        IReadOnlyList<ProcessingClassificationResponse> classifications)
    {
        var canonical = JsonSerializer.Serialize(classifications
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new
            {
                item.Key,
                item.Name,
                Confidence = item.Confidence?.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                item.IsPrimary,
                item.Source,
                item.Rationale,
            }));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{runId:N}|{correctionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{aiFingerprint}|{canonical}"));
        return $"{runId:N}:{correctionVersion}:{Convert.ToHexString(digest)}";
    }

    private static Task AcquireDocumentLockAsync(
        ConvencionesDbContext db,
        Guid documentId,
        bool shared,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            shared
                ? "select pg_advisory_xact_lock_shared(hashtextextended({0}, 0))"
                : "select pg_advisory_xact_lock(hashtextextended({0}, 0))",
            [DocumentLockKey(documentId)],
            cancellationToken);

    private static Task AcquireRunLockAsync(
        ConvencionesDbContext db,
        Guid runId,
        bool shared,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            shared
                ? "select pg_advisory_xact_lock_shared(hashtextextended({0}, 0))"
                : "select pg_advisory_xact_lock(hashtextextended({0}, 0))",
            [RunLockKey(runId)],
            cancellationToken);

    private static Task AcquireClauseLockAsync(
        ConvencionesDbContext db,
        Guid clauseId,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "select pg_advisory_xact_lock(hashtextextended({0}, 0))",
            [ClauseLockKey(clauseId)],
            cancellationToken);

    private static string DocumentLockKey(Guid documentId) => ClassificationLocks.DocumentKey(documentId);

    private static string RunLockKey(Guid runId) => ClassificationLocks.RunKey(runId);

    private static string ClauseLockKey(Guid clauseId) => ClassificationLocks.ClauseKey(clauseId);

    private static async Task<ExtractedClause?> ReloadLockedClauseAsync(
        ConvencionesDbContext db,
        ExtractedClause stale,
        CancellationToken cancellationToken)
    {
        db.Entry(stale).State = EntityState.Detached;
        var current = await db.ExtractedClauses
            .SingleOrDefaultAsync(candidate => candidate.Id == stale.Id, cancellationToken)
            .ConfigureAwait(false);
        return current is not null && current.CompanyId == stale.CompanyId ? current : null;
    }


    private static Task<bool> DocumentIsActiveAsync(
        ConvencionesDbContext db,
        Guid documentId,
        CancellationToken cancellationToken) =>
        db.Documents.AsNoTracking().AnyAsync(document => document.Id == documentId, cancellationToken);

    private static Task<ProcessingRun?> FindCurrentRunAsync(
        ConvencionesDbContext db,
        Guid documentId,
        CancellationToken cancellationToken) =>
        db.ProcessingRuns.AsNoTracking()
            .Where(run => run.DocumentId == documentId
                && run.Status == ProcessingStatus.Completed
                && run.FinishedAt != null)
            .OrderByDescending(run => run.FinishedAt)
            .ThenByDescending(run => run.CreatedAt)
            .ThenByDescending(run => run.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static IResult ClauseNotCurrent(Guid id) =>
        State("clause_not_current", $"Clause '{id}' does not belong to the current completed run.");


    private static IResult ClassificationAuditInconsistent() =>
        Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Classification audit inconsistent",
            detail: "The latest classification correction cannot be interpreted. Reload or contact an operator.",
            extensions: new Dictionary<string, object?> { ["code"] = "classification_audit_inconsistent" });

    private static IResult ClassificationVersionConflict(string currentVersion) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Classification conflict",
            detail: "The classification changed while this correction was being prepared. Reload and retry.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "classification_version_conflict",
                ["currentVersion"] = currentVersion,
            });

    private static IResult Forbidden() =>
        Problem(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "No session was resolved for this request.");

    private static IResult NotFoundDocument(Guid id) =>
        Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"No document with id '{id}' is visible to the caller.");

    private static IResult NotFoundRun(Guid id) =>
        Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"No processing run with id '{id}' is visible to the caller.");

    private static IResult NotFoundClause(Guid id) =>
        Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"No clause with id '{id}' is visible to the caller.");

    private static IResult State(string code, string detail) =>
        Problem(StatusCodes.Status409Conflict, code, detail);

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(
            statusCode: status,
            title: status == StatusCodes.Status404NotFound ? "Not found" : "Invalid state",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record ProcessingRunResponse(
    Guid Id,
    Guid DocumentId,
    string Status,
    string Stage,
    int Attempts,
    int ClauseCount,
    int ClassifiedCount,
    int UnclassifiedCount,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record ProcessingClauseResponse(
    Guid Id,
    int Ordinal,
    string? Number,
    string? Heading,
    string Text,
    int PageFrom,
    int PageTo,
    int? SourceCharStart,
    int? SourceCharEnd,
    double? Confidence,
    bool NeedsReview,
    string ReviewStatus,
    IReadOnlyList<ProcessingClassificationResponse> Classifications,
    string ClassificationVersion);

public sealed record ProcessingClassificationResponse(
    string Key,
    string Name,
    double? Confidence,
    bool IsPrimary,
    string? Rationale,
    string Source);

public sealed record ProcessingClausePage(
    IReadOnlyList<ProcessingClauseResponse> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record ReviewClauseRequest(
    string Status,
    string? Text,
    string? Heading,
    IReadOnlyList<ClassificationCorrectionRequest>? Classifications,
    string? Primary,
    string? ExpectedVersion);

public sealed record ClassificationCorrectionRequest(
    string Key,
    double? Confidence,
    string? Rationale);

public sealed record ClauseCategoryResponse(
    string Key,
    string Name,
    string? Description,
    Guid? ParentId,
    int SortOrder);

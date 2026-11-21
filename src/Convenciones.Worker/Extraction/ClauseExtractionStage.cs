using Convenciones.Ai;
using Convenciones.Ai.DocumentIntelligence;
using Convenciones.Documents;
using Convenciones.Worker;
using Microsoft.Extensions.Logging;

namespace Convenciones.Worker.Extraction;

/// <summary>
/// Preparation + clause extraction + persistence. The model is instructed to treat the contract as
/// data; only the extracted text and the schema leave the worker, never the PDF path or tenant ids.
/// </summary>
public sealed class ClauseExtractionStage(
    IDocumentStorage storage,
    IDocumentLayoutClient layoutClient,
    IAiModelClient modelClient,
    IClauseStore store,
    ExtractionOptions options,
    ILogger<ClauseExtractionStage> logger) : IProcessingStage
{
    public async Task ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        var document = await store.LoadDocumentAsync(job.RunId, cancellationToken).ConfigureAwait(false)
            ?? throw new PermanentProcessingException("document_not_found", "The run references a missing document.");

        await using var pdf = await storage.OpenReadAsync(document.StoragePath, cancellationToken).ConfigureAwait(false)
            ?? throw new PermanentProcessingException("document_missing", "The stored PDF could not be opened.");

        var layout = await layoutClient.AnalyzeAsync(pdf, cancellationToken).ConfigureAwait(false);
        if (layout.RequiresOcr)
        {
            throw new PermanentProcessingException(
                "ocr_required",
                "The PDF has no usable text layer; OCR is not available in this increment.");
        }

        var chunks = ClauseChunker.Chunk(layout, options);
        if (chunks.Count > options.MaxChunks)
        {
            throw new PermanentProcessingException(
                "document_too_large",
                $"The document produced {chunks.Count} chunks; the limit is {options.MaxChunks}.");
        }

        var parsed = new List<ParsedClause>();
        long inputTokens = 0;
        long outputTokens = 0;
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.UpdateProgressAsync(job, "ExtractingClauses", parsed.Count, cancellationToken)
                .ConfigureAwait(false);

            AiCompletionResult result;
            try
            {
                result = await modelClient.CompleteAsync(
                    new AiCompletionRequest(
                        ExtractionPrompt.System,
                        ExtractionPrompt.User(chunk),
                        ExtractionSchema.Json,
                        options.MaxOutputTokens,
                        options.Deployment),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AiInvalidResponseException exception)
            {
                throw new PermanentProcessingException("invalid_model_output", exception.Message);
            }

            try
            {
                parsed.AddRange(ClauseResponseParser.Parse(result.Content, chunk));
            }
            catch (ClauseExtractionException exception)
            {
                throw new PermanentProcessingException(exception.Code, exception.Message);
            }

            inputTokens += result.InputTokens ?? 0;
            outputTokens += result.OutputTokens ?? 0;
        }

        var merged = ClauseMerger.Merge(parsed)
            .Select(clause => InstructionDetector.IsInstructionLike(clause.Text)
                ? clause with { NeedsReview = true }
                : clause)
            .ToList();
        if (merged.Count == 0)
        {
            throw new PermanentProcessingException("no_clauses_found", "The model returned no clauses.");
        }

        await store.SaveClausesAsync(job, merged, cancellationToken).ConfigureAwait(false);
        var cost = (decimal)((inputTokens / 1_000_000.0 * options.InputPricePerMillion)
            + (outputTokens / 1_000_000.0 * options.OutputPricePerMillion));
        await store.RecordUsageAsync(job.CompanyId, layout.PageCount, inputTokens, outputTokens, cost, cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation(
            "Extracted {Clauses} clause(s) from document {DocumentId} in run {RunId}.",
            merged.Count,
            job.DocumentId,
            job.RunId);
    }
}

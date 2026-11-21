using Convenciones.Ai;
using Convenciones.Ai.DocumentIntelligence;
using Convenciones.Documents;
using Convenciones.Worker;
using Convenciones.Worker.Financial;
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
    IFinancialBudget budget,
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
        var pricing = AiPricing.FromConfiguration(
            options.PricingVersion, options.InputPricePerMillion, options.OutputPricePerMillion);
        var invocation = 0;
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.UpdateProgressAsync(job, "ExtractingClauses", parsed.Count, cancellationToken)
                .ConfigureAwait(false);

            // The identity exists before submission so the reservation, the submission marker and the
            // usage row can all refer to the same logical call.
            var invocationId = Guid.NewGuid();
            var prompt = ExtractionPrompt.User(chunk);
            var estimate = AiCostEstimator.ReserveEstimate(
                pricing, ExtractionPrompt.System, prompt, options.MaxOutputTokens);

            var decision = await budget
                .ReserveAsync(job, "extraction", invocationId, estimate, cancellationToken)
                .ConfigureAwait(false);
            if (!decision.Admitted)
            {
                throw new PermanentProcessingException(
                    "budget_exceeded",
                    "The company financial budget cannot cover this invocation.");
            }

            await budget.MarkSubmittedAsync(job, invocationId, cancellationToken).ConfigureAwait(false);

            AiCompletionResult result;
            try
            {
                result = await modelClient.CompleteAsync(
                    new AiCompletionRequest(
                        ExtractionPrompt.System,
                        prompt,
                        ExtractionSchema.Json,
                        options.MaxOutputTokens,
                        options.Deployment),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AiInvalidResponseException exception)
            {
                await budget.SettleAsync(job.CompanyId, invocationId, null, cancellationToken).ConfigureAwait(false);
                throw new PermanentProcessingException("invalid_model_output", exception.Message);
            }
            catch
            {
                // The provider may bill a timed-out or aborted request; unknown usage settles at the
                // estimate rather than disappearing.
                await budget.SettleAsync(job.CompanyId, invocationId, null, cancellationToken).ConfigureAwait(false);
                throw;
            }

            // Recorded before parsing and before persistence: the provider has already billed this
            // call, so a later failure in this stage must not erase the charge.
            invocation++;
            await store.RecordInvocationAsync(
                    job,
                    "extraction",
                    invocationId,
                    invocation,
                    result,
                    invocation == 1 ? layout.PageCount : 0,
                    pricing,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                parsed.AddRange(ClauseResponseParser.Parse(result.Content, chunk));
            }
            catch (ClauseExtractionException exception)
            {
                throw new PermanentProcessingException(exception.Code, exception.Message);
            }
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
        logger.LogInformation(
            "Extracted {Clauses} clause(s) from document {DocumentId} in run {RunId}.",
            merged.Count,
            job.DocumentId,
            job.RunId);
    }
}

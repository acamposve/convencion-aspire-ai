namespace Convenciones.Worker.Extraction;

/// <summary>Runs extraction and then classification in one worker attempt.</summary>
public sealed class ClauseProcessingStage(
    ClauseExtractionStage extraction,
    ClauseClassificationStage classification) : IProcessingStage
{
    public async Task ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        await extraction.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
        await classification.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
    }
}

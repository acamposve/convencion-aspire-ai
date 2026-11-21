using Convenciones.Worker;

namespace Convenciones.Worker.Financial;

/// <summary>
/// Admission and settlement of one provider invocation. Separate from the implementation so the stages
/// depend on the accounting contract, and so their unit tests can run without PostgreSQL.
/// </summary>
public interface IFinancialBudget
{
    Task<ReservationDecision> ReserveAsync(
        ProcessingJob job,
        string stage,
        Guid invocationId,
        decimal estimatedCost,
        CancellationToken cancellationToken);

    Task MarkSubmittedAsync(ProcessingJob job, Guid invocationId, CancellationToken cancellationToken);

    /// <summary>Settles at <paramref name="actualCost"/>, or at the estimate when it is null.</summary>
    Task SettleAsync(Guid companyId, Guid invocationId, decimal? actualCost, CancellationToken cancellationToken);
}

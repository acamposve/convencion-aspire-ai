using Convenciones.Worker;
using Convenciones.Worker.Financial;

namespace Convenciones.UnitTests.Extraction;

/// <summary>
/// Allows every invocation and settles nothing: unit tests exercise prompt and parsing behaviour, not
/// budget accounting, which is covered against PostgreSQL in the integration suite.
/// </summary>
internal sealed class FakeFinancialBudget : IFinancialBudget
{
    public Task<ReservationDecision> ReserveAsync(
        ProcessingJob job,
        string stage,
        Guid invocationId,
        decimal estimatedCost,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ReservationDecision(ReservationMode.Shadow, true, null, estimatedCost));

    public Task MarkSubmittedAsync(ProcessingJob job, Guid invocationId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task SettleAsync(Guid companyId, Guid invocationId, decimal? actualCost, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

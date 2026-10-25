using System;
using System.Linq;
using System.Threading.Tasks;
using Convenciones.Api.Infrastructure;
using Convenciones.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Proves the PostgreSQL enum <c>platform.extraction_status</c> is mapped, in both directions.
/// </summary>
/// <remarks>
/// This exists because the mapping was broken twice for different reasons: EF sent the enum as an
/// integer, and once that was fixed Npgsql translated the labels to snake_case
/// (<c>not_requested</c>) while the database defines PascalCase. A test that only exercised the
/// default value would have passed through both defects.
/// </remarks>
[Collection(DocumentsCollection.Name)]
public sealed class ExtractionStatusMappingTests(DocumentsFixture fixture)
{
    [Fact]
    public async Task Every_status_can_be_written_and_read_back()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConvencionesDbContext>();

        var owner = new Guid("b1000000-0000-0000-0000-000000000001");
        var company = new Guid("b2000000-0000-0000-0000-000000000001");

        var statuses = Enum.GetValues<ExtractionStatus>();
        statuses.Length.ShouldBe(5, "the database enum has five labels");

        foreach (var status in statuses)
        {
            var id = Guid.NewGuid();
            var document = Document.Register(
                id,
                DocumentKeys.For(company, id, DateTimeOffset.UtcNow),
                $"{status}.pdf",
                1024,
                new string('a', 64),
                owner,
                company);

            document.SetExtractionStatus(status);
            db.Documents.Add(document);
            await db.SaveChangesAsync();

            db.ChangeTracker.Clear();

            var reloaded = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id);
            reloaded.ExtractionStatus.ShouldBe(status, $"status {status} must survive a round trip");
        }
    }

    [Fact]
    public async Task A_new_document_starts_as_NotRequested()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConvencionesDbContext>();

        var owner = new Guid("b1000000-0000-0000-0000-000000000001");
        var company = new Guid("b2000000-0000-0000-0000-000000000001");
        var id = Guid.NewGuid();

        db.Documents.Add(Document.Register(
            id, DocumentKeys.For(company, id, DateTimeOffset.UtcNow), "fresh.pdf", 512,
            new string('b', 64), owner, company));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reloaded = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id);
        reloaded.ExtractionStatus.ShouldBe(ExtractionStatus.NotRequested);
    }
}
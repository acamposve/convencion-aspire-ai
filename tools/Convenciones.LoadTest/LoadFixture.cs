using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Convenciones.LoadTest;

/// <summary>
/// The tenant, the read fixtures and the queue fixtures.
/// </summary>
/// <remarks>
/// Documents, agreements and clauses are created through the API when their endpoint contract is what
/// the scenario reads, so the rows satisfy the same validation a user would. Queue inputs for the
/// worker scenarios are inserted directly: their contract is the <c>processing_run</c> row, and calling
/// the process endpoint would add quota semantics that this task does not measure.
/// </remarks>
public sealed class LoadFixture
{
    public Guid CompanyId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid HeadingId { get; private set; }

    public string Token { get; set; } = string.Empty;

    public List<Guid> DocumentIds { get; } = [];

    public List<Guid> AgreementIds { get; } = [];

    public Guid[] PublishedAgreementIds { get; private set; } = [];

    public async Task SeedTenantAsync(PostgresHarness database)
    {
        ArgumentNullException.ThrowIfNull(database);

        CompanyId = Guid.NewGuid();
        UserId = Guid.NewGuid();

        await database.ExecuteAsync(
            """
            insert into app.company (id, name, country_id)
            values (@company, 'Load Test Company', (select id from app.country where code = 'VE'));

            insert into platform.user_account (id, email, display_name)
            values (@user, 'load-test@example.test', 'Load Test');

            insert into platform.user_role (user_id, role_id)
            select @user, id from platform.role where key = 'administrator';

            insert into platform.user_profile (user_id, company_id)
            values (@user, @company);
            """,
            CancellationToken.None,
            ("company", CompanyId),
            ("user", UserId));
    }

    public async Task LoadHeadingAsync(PostgresHarness database)
    {
        HeadingId = await database.ScalarAsync<Guid>(
            "select id from app.comparison_heading order by sort_order, name limit 1;");
    }

    /// <summary>Creates <paramref name="count"/> documents through the upload endpoint.</summary>
    public async Task CreateDocumentsAsync(HttpClient api, int count, byte[] pdf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pdf);

        for (var index = 0; index < count; index++)
        {
            using var content = new MultipartFormDataContent($"load-{Guid.NewGuid():N}");
            var file = new ByteArrayContent(pdf);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            content.Add(file, "file", $"fixture-{index:D4}.pdf");

            using var request = new HttpRequestMessage(HttpMethod.Post, "/documents") { Content = content };
            var response = await SendAsync(api, request, cancellationToken).ConfigureAwait(false);

            var id = ReadId(response.Body, response.Location)
                ?? throw new InvalidOperationException($"The upload response carried no id: {response.Body}");
            DocumentIds.Add(id);
        }
    }

    /// <summary>Creates agreements with clauses through the authoring endpoints.</summary>
    public async Task CreateAgreementsAsync(
        HttpClient api,
        int count,
        int clausesPerAgreement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);

        for (var index = 0; index < count; index++)
        {
            // The company + start date pair is unique, so the fixture gives every agreement its own day.
            var validFrom = new DateOnly(2026, 1, 1)
                .AddDays(index)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var create = new
            {
                companyId = CompanyId,
                validFrom,
                validTo = (string?)null,
                scope = $"load fixture {index:D4}",
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "/agreements")
            {
                Content = JsonContent.Create(create),
            };

            var response = await SendAsync(api, request, cancellationToken).ConfigureAwait(false);
            var agreementId = ReadId(response.Body, response.Location)
                ?? throw new InvalidOperationException($"The agreement response carried no id: {response.Body}");

            AgreementIds.Add(agreementId);

            for (var clause = 1; clause <= clausesPerAgreement; clause++)
            {
                var clauseRequest = new
                {
                    number = clause,
                    title = $"Clause {clause}",
                    summary = $"Fixture clause {clause}.",
                    body = $"Fixture clause {clause} body for the load fixture agreement {index:D4}.",
                    headingId = HeadingId,
                    economicComparisonNote = (string?)null,
                    isExcludedFromCompilation = false,
                    exclusionNote = (string?)null,
                };

                using var addClause = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/agreements/{agreementId}/clauses")
                {
                    Content = JsonContent.Create(clauseRequest),
                };

                await SendAsync(api, addClause, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Publishes the first agreements so the anonymous portal has real rows to read.</summary>
    public async Task PublishAgreementsAsync(HttpClient api, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);

        var published = new List<Guid>();
        foreach (var agreementId in AgreementIds.Take(count))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/agreements/{agreementId}/publish");
            await SendAsync(api, request, cancellationToken).ConfigureAwait(false);
            published.Add(agreementId);
        }

        PublishedAgreementIds = [.. published];
    }

    /// <summary>
    /// Inserts <paramref name="count"/> documents and one queued run each. Used by the worker scenarios;
    /// <paramref name="prefix"/> tags the rows so each scenario measures only its own queue.
    /// </summary>
    public Task<int> InsertWorkerRunsAsync(
        PostgresHarness database,
        int count,
        string prefix,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(
            """
            with docs as (
                insert into platform.document
                    (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id)
                select gen_random_uuid(),
                       @prefix || gen_random_uuid()::text || '.pdf',
                       'load.pdf',
                       'application/pdf',
                       1024,
                       @user,
                       @company
                from generate_series(1, @count::int)
                returning id, company_id
            )
            insert into platform.processing_run (document_id, company_id, requested_by)
            select id, company_id, @user from docs;
            """,
            cancellationToken,
            ("prefix", prefix),
            ("user", UserId),
            ("company", CompanyId),
            ("count", count));

    /// <summary>Reads the fixture state once at the end; it is not a timed operation.</summary>
    public async Task<string> DescribeAsync(PostgresHarness database)
    {
        var documents = await database.ScalarAsync<int>(
            "select count(*)::int from platform.document where company_id = @company;",
            CancellationToken.None,
            ("company", CompanyId));
        var agreements = await database.ScalarAsync<int>(
            "select count(*)::int from app.agreement where company_id = @company;",
            CancellationToken.None,
            ("company", CompanyId));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"documents={documents}; agreements={agreements}; documentFixtures={DocumentIds.Count}; " +
            $"agreementFixtures={AgreementIds.Count}; published={PublishedAgreementIds.Length}");
    }

    private sealed record ApiResponse(int Status, string Body, string? Location);

    private static async Task<ApiResponse> SendAsync(
        HttpClient api,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await api.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (status is < 200 or > 299)
        {
            throw new InvalidOperationException(
                $"{request.Method} {request.RequestUri} returned {status}: {Truncate(body)}");
        }

        return new ApiResponse(status, body, response.Headers.Location?.ToString());
    }

    private static Guid? ReadId(string body, string? location)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var id)
                && id.TryGetGuid(out var parsed))
            {
                return parsed;
            }
        }

        if (!string.IsNullOrWhiteSpace(location))
        {
            var segment = location.TrimEnd('/').Split('/')[^1];
            if (Guid.TryParse(segment, out var fromLocation))
            {
                return fromLocation;
            }
        }

        return null;
    }

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : value[..400] + "…";
}

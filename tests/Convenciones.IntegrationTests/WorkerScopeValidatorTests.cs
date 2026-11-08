using Convenciones.Worker;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

[Collection(DocumentsCollection.Name)]
public sealed class WorkerScopeValidatorTests(DocumentsFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Editor = new("b1000000-0000-0000-0000-000000000001");
    private readonly List<(Guid Run, Guid Document)> _created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (run, document) in _created)
        {
            await ExecuteAsync($"delete from platform.processing_run where id = '{run}';");
            await ExecuteAsync($"delete from platform.document where id = '{document}';");
        }
    }

    [Fact]
    public async Task Company_must_match_the_static_allowlist()
    {
        var (company, document, run) = await CreateScopeAsync();
        var options = new WorkerOptions
        {
            Enabled = true,
            RequireScope = true,
            AllowedCompanyId = company,
            AuthorizedCompanyId = Guid.NewGuid(),
            AllowedDocumentId = document,
            AllowedRunId = run,
        };

        var error = await WorkerScopeValidator.ValidateAsync(
            fixture.ConnectionString,
            options,
            CancellationToken.None);

        error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Valid_company_document_and_run_are_accepted()
    {
        var (company, document, run) = await CreateScopeAsync();

        var error = await ValidateAsync(company, document, run);

        error.ShouldBeNull();
    }

    [Fact]
    public async Task Run_only_is_validated_against_its_document_and_company()
    {
        var (company, _, run) = await CreateScopeAsync();

        var error = await ValidateAsync(company, null, run);

        error.ShouldBeNull();
    }

    [Fact]
    public async Task Nonexistent_document_is_rejected()
    {
        var (company, _, _) = await CreateScopeAsync();

        var error = await ValidateAsync(company, Guid.NewGuid(), null);

        error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Nonexistent_run_is_rejected()
    {
        var (company, _, _) = await CreateScopeAsync();

        var error = await ValidateAsync(company, null, Guid.NewGuid());

        error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Nonexistent_company_is_rejected()
    {
        var error = await ValidateAsync(Guid.NewGuid(), null, null);

        error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Soft_deleted_document_is_rejected()
    {
        var (company, document, run) = await CreateScopeAsync();
        await ExecuteAsync(
            $"update platform.document set deleted_at = now() where id = '{document}';");

        var error = await ValidateAsync(company, document, run);

        error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Document_from_another_company_is_rejected()
    {
        var (_, document, _) = await CreateScopeAsync();

        var error = await ValidateAsync(Guid.NewGuid(), document, null);

        error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Run_from_another_company_is_rejected()
    {
        var (_, _, run) = await CreateScopeAsync();

        var error = await ValidateAsync(Guid.NewGuid(), null, run);

        error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Run_from_another_document_is_rejected()
    {
        var (company, _, run) = await CreateScopeAsync();
        var (_, otherDocument, _) = await CreateScopeAsync(company);

        var error = await ValidateAsync(company, otherDocument, run);

        error.ShouldNotBeNull();
    }

    [Fact]
    public async Task Missing_document_and_run_are_rejected_before_startup()
    {
        var options = new WorkerOptions
        {
            Enabled = true,
            RequireScope = true,
            AllowedCompanyId = Guid.NewGuid(),
        };

        WorkerScopePolicy.Validate(options).ShouldNotBeNull();
    }

    private async Task<string?> ValidateAsync(Guid company, Guid? document, Guid? run)
    {
        var options = new WorkerOptions
        {
            Enabled = true,
            RequireScope = true,
            AllowedCompanyId = company,
            AuthorizedCompanyId = company,
            AllowedDocumentId = document,
            AllowedRunId = run,
        };
        return await WorkerScopeValidator.ValidateAsync(
            fixture.ConnectionString,
            options,
            CancellationToken.None);
    }

    private async Task<(Guid Company, Guid Document, Guid Run)> CreateScopeAsync(Guid? companyId = null)
    {
        var company = companyId ?? Guid.NewGuid();
        var document = Guid.NewGuid();
        var run = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        var companyInsert = companyId is null
            ? $"insert into app.company (id, name) values ('{company}', 'Scope {suffix}');"
            : string.Empty;

        await ExecuteAsync(
            $"""
            {companyInsert}
            insert into platform.document
                (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id)
            values
                ('{document}', 'scope/{suffix}.pdf', 'scope.pdf', 'application/pdf', 1024, '{Editor}', '{company}');
            insert into platform.processing_run (id, document_id, company_id)
            values ('{run}', '{document}', '{company}');
            """);

        _created.Add((run, document));
        return (company, document, run);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

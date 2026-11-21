using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Worker;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P1-009: the operational report exposes queue age and worker heartbeat without inventing thresholds.
/// </summary>
/// <remarks>
/// Thresholds are configuration. When a threshold is absent the signal is <c>Unknown</c>; the test suite
/// never relies on a built-in number. The queue is global, so each test clears queued runs first: tests in
/// this collection do not run concurrently.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class OperationsHealthTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly List<Guid> _companies = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var company in _companies)
        {
            await ExecuteAsync(
                $"delete from platform.processing_run where company_id = '{company}';" +
                $"delete from platform.document where company_id = '{company}';");
        }

        await ExecuteAsync("delete from platform.user_account where email like 'p1009-%';");

        foreach (var company in _companies)
        {
            await ExecuteAsync($"delete from app.company where id = '{company}';");
        }
    }

    [Fact]
    public async Task An_empty_queue_and_a_fresh_heartbeat_are_healthy()
    {
        await ClearQueueAsync();
        await HeartbeatAsync(fresh: true);

        using var client = Client(new Dictionary<string, string>
        {
            ["Operations:QueueMaxAgeSeconds"] = "3600",
            ["Operations:WorkerHeartbeatMaxAgeSeconds"] = "3600",
        });

        var report = await ReadOpsAsync(client);

        report.Status.ShouldBe("Healthy");
        Signal(report, "queue").Status.ShouldBe("Healthy");
        Signal(report, "worker").Status.ShouldBe("Healthy");
        DateTimeOffset.TryParse(report.CheckedAt, out var checkedAt).ShouldBeTrue();
        checkedAt.Offset.ShouldBe(TimeSpan.Zero, "the report timestamp is UTC");
    }

    [Fact]
    public async Task A_queue_older_than_the_configured_threshold_is_degraded()
    {
        await ClearQueueAsync();
        var seeded = await SeedQueuedRunAsync(TimeSpan.FromHours(2));
        await HeartbeatAsync(fresh: true);

        using var client = Client(new Dictionary<string, string>
        {
            ["Operations:QueueMaxAgeSeconds"] = "60",
            ["Operations:WorkerHeartbeatMaxAgeSeconds"] = "3600",
        });

        var report = await ReadOpsAsync(client);

        report.Status.ShouldBe("Degraded");
        var queue = Signal(report, "queue");
        queue.Status.ShouldBe("Degraded");
        queue.Data.GetProperty("thresholdSeconds").GetInt64().ShouldBe(60);
        queue.Data.GetProperty("queued").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
        queue.Data.GetProperty("oldestAgeSeconds").GetInt64().ShouldBeGreaterThanOrEqualTo(7000);

        _ = seeded;
    }

    [Fact]
    public async Task Missing_thresholds_report_unknown_instead_of_guessing()
    {
        await ClearQueueAsync();

        // No Operations:* settings on this host.
        using var client = Client(settings: null);

        var report = await ReadOpsAsync(client);

        report.Status.ShouldBe("Unknown");
        Signal(report, "queue").Status.ShouldBe("Unknown");
        Signal(report, "worker").Status.ShouldBe("Unknown");
    }

    [Fact]
    public async Task A_stale_heartbeat_is_degraded_and_a_missing_one_is_unknown()
    {
        await ClearQueueAsync();
        await HeartbeatAsync(fresh: false);

        using var stale = Client(new Dictionary<string, string>
        {
            ["Operations:QueueMaxAgeSeconds"] = "3600",
            ["Operations:WorkerHeartbeatMaxAgeSeconds"] = "60",
        });
        var staleReport = await ReadOpsAsync(stale);
        Signal(staleReport, "worker").Status.ShouldBe("Degraded");
        staleReport.Status.ShouldBe("Degraded");

        await DeleteHeartbeatAsync();

        using var missing = Client(new Dictionary<string, string>
        {
            ["Operations:QueueMaxAgeSeconds"] = "3600",
            ["Operations:WorkerHeartbeatMaxAgeSeconds"] = "60",
        });
        var missingReport = await ReadOpsAsync(missing);
        Signal(missingReport, "worker").Status.ShouldBe("Unknown");
        Signal(missingReport, "worker").Description.ShouldContain("never");
    }

    [Fact]
    public async Task Readiness_is_independent_of_queue_backlog()
    {
        await ClearQueueAsync();
        await SeedQueuedRunAsync(TimeSpan.FromHours(2));
        await HeartbeatAsync(fresh: true);

        using var client = Client(new Dictionary<string, string>
        {
            ["Operations:QueueMaxAgeSeconds"] = "60",
            ["Operations:WorkerHeartbeatMaxAgeSeconds"] = "3600",
        });

        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        ready.StatusCode.ShouldBe(HttpStatusCode.OK, "a processing backlog must not remove serving capacity");

        var ops = await client.GetAsync(new Uri("/health/ops", UriKind.Relative));
        ops.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadOpsAsync(client)).Status.ShouldBe("Degraded");
    }

    [Fact]
    public async Task An_unreachable_database_is_reported_as_unhealthy()
    {
        const string broken =
            "Host=127.0.0.1;Port=1;Database=missing;Username=missing;Password=missing;Timeout=1;Command Timeout=1";

        using var client = new ApiFactory(
            broken,
            settings: new Dictionary<string, string>
            {
                ["Operations:QueueMaxAgeSeconds"] = "60",
                ["Operations:WorkerHeartbeatMaxAgeSeconds"] = "60",
            }).CreateClient();

        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var ops = await client.GetAsync(new Uri("/health/ops", UriKind.Relative));
        ops.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var report = await ReadOpsAsync(client);
        Signal(report, "database").Status.ShouldBe("Unhealthy");
        report.Status.ShouldBe("Unhealthy");
    }

    [Fact]
    public async Task The_worker_heartbeat_writer_upserts_one_row()
    {
        await HeartbeatAsync(fresh: true);
        var before = await ScalarAsync("select last_seen_at::text from platform.worker_heartbeat where id = 1;");

        await Task.Delay(10);
        await new WorkerHeartbeat(fixture.ConnectionString).TouchAsync(enabled: true, CancellationToken.None);

        (await ScalarAsync("select count(*) from platform.worker_heartbeat;")).ShouldBe("1");
        var after = await ScalarAsync("select last_seen_at::text from platform.worker_heartbeat where id = 1;");
        after.ShouldNotBe(before, "the heartbeat moves forward");
    }

    // ---------------------------------------------------------------- helpers

    private sealed record OpsReport(string Status, string CheckedAt, JsonElement Checks);

    private sealed record SignalView(string Status, string Description, JsonElement Data);

    private HttpClient Client(IReadOnlyDictionary<string, string>? settings) =>
        new ApiFactory(fixture.ConnectionString, settings: settings).CreateClient();

    private static async Task<OpsReport> ReadOpsAsync(HttpClient client)
    {
        var response = await client.GetAsync(new Uri("/health/ops", UriKind.Relative));
        var payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement.Clone();

        return new OpsReport(
            root.GetProperty("status").GetString()!,
            root.GetProperty("checkedAt").GetString()!,
            root.GetProperty("checks"));
    }

    private static SignalView Signal(OpsReport report, string name)
    {
        foreach (var check in report.Checks.EnumerateArray())
        {
            if (check.GetProperty("name").GetString() == name)
            {
                return new SignalView(
                    check.GetProperty("status").GetString()!,
                    check.GetProperty("description").GetString() ?? string.Empty,
                    check.GetProperty("data"));
            }
        }

        throw new InvalidOperationException($"The report has no '{name}' signal.");
    }

    private async Task ClearQueueAsync()
    {
        await ExecuteAsync(
            "update platform.processing_run set status = 'Cancelled'::platform.processing_status " +
            "where status = 'Queued'::platform.processing_status;");
    }

    private async Task<Guid> SeedQueuedRunAsync(TimeSpan age)
    {
        var company = Guid.NewGuid();
        var user = Guid.NewGuid();
        var document = Guid.NewGuid();
        var run = Guid.NewGuid();
        _companies.Add(company);

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{company}', 'Ops {company:N}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{user}', 'p1009-{user:N}@example.test', 'Ops');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{document}', 'ops/{document:N}.pdf', 'ops.pdf', 'application/pdf', 1, '{user}', '{company}');" +
            $"insert into platform.processing_run (id, document_id, company_id, created_at) " +
            $"values ('{run}', '{document}', '{company}', now() - interval '{age.TotalSeconds} seconds');");

        return run;
    }

    private async Task HeartbeatAsync(bool fresh)
    {
        var age = fresh ? "0 seconds" : "2 hours";
        await ExecuteAsync(
            $"insert into platform.worker_heartbeat (id, last_seen_at, enabled) values (1, now() - interval '{age}', true) " +
            "on conflict (id) do update set last_seen_at = excluded.last_seen_at, enabled = excluded.enabled;");
    }

    private async Task DeleteHeartbeatAsync() =>
        await ExecuteAsync("delete from platform.worker_heartbeat;");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }
}

using System.Net;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Proves the host boots, reaches PostgreSQL, and reports its state through the two probes the
/// orchestrator depends on.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HealthEndpointTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Liveness_reports_healthy_without_touching_the_database()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_reports_healthy_when_migrations_have_been_applied()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"readiness returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}

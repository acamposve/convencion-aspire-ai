using Convenciones.Worker;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests;

public sealed class WorkerConnectionStringTests
{
    [Fact]
    public void Resolve_reads_the_colon_keys_used_by_the_deployment()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Host"] = "db.example.test",
                ["Database:Port"] = "5432",
                ["Database:Name"] = "convenciones",
                ["Database:User"] = "deploy",
                ["Database:Password"] = "not-a-real-password",
            })
            .Build();

        var connection = WorkerConnectionString.Resolve(configuration);

        connection.ShouldNotBeNull();
        connection.ShouldContain("db.example.test");
        connection.ShouldContain("convenciones");
        connection.ShouldContain("deploy");
    }
}

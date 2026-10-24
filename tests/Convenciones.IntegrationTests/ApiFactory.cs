using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Hosts the real API pipeline against the disposable database.
/// </summary>
/// <remarks>
/// The only thing overridden is the connection string. Everything else — exception handling,
/// problem details, health checks, logging — is the production configuration, so a test cannot pass
/// against a pipeline that differs from the one that ships.
/// </remarks>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("ConnectionStrings:convenciones", connectionString);
        builder.UseEnvironment("Testing");
    }
}

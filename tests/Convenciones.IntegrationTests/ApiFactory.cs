using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Hosts the real API pipeline against the disposable database.
/// </summary>
/// <remarks>
/// The only things overridden are the connection string and the token validation settings.
/// Everything else — exception handling, problem details, health checks, the authentication
/// pipeline, the capability middleware and the endpoint security validator — is the production
/// configuration, so a test cannot pass against a pipeline that differs from the one that ships.
/// </remarks>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("ConnectionStrings:convenciones", connectionString);
        builder.UseSetting("Jwt:Issuer", TestAuth.Issuer);
        builder.UseSetting("Jwt:Audience", TestAuth.Audience);
        builder.UseSetting("Jwt:SigningKey", TestAuth.SigningKey);
        builder.UseEnvironment("Testing");
    }
}

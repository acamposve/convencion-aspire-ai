using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Hosts the real API pipeline against the disposable database.
/// </summary>
/// <remarks>
/// The only things overridden are the connection string, the token validation settings and the
/// document storage root. Everything else — exception handling, problem details, health checks, the
/// authentication pipeline, the capability middleware, the upload limits and the endpoint security
/// validator — is the production configuration, so a test cannot pass against a pipeline that differs
/// from the one that ships.
/// </remarks>
public sealed class ApiFactory(string connectionString, string? storageRoot = null)
    : WebApplicationFactory<Program>
{
    /// <summary>
    /// Every host needs a storage root: the API refuses to start without one rather than writing
    /// documents somewhere unintended.
    /// </summary>
    public string StorageRoot { get; } = storageRoot
        ?? Path.Combine(Path.GetTempPath(), $"convenciones-default-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Directory.CreateDirectory(StorageRoot);

        builder.UseSetting("ConnectionStrings:convenciones", connectionString);
        builder.UseSetting("Jwt:Issuer", TestAuth.Issuer);
        builder.UseSetting("Jwt:Audience", TestAuth.Audience);
        builder.UseSetting("Jwt:SigningKey", TestAuth.SigningKey);
        builder.UseSetting("Documents:StorageRoot", StorageRoot);
        builder.UseEnvironment("Testing");
    }
}

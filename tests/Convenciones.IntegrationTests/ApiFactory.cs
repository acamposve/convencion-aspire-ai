using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

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
public sealed class ApiFactory(
    string connectionString,
    string? storageRoot = null,
    ILoggerProvider? loggerProvider = null)
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
        if (loggerProvider is not null)
        {
            builder.ConfigureLogging(logging => logging
                .SetMinimumLevel(LogLevel.Information)
                .AddProvider(loggerProvider));
        }
    }
}

public sealed class CommandCountingLoggerProvider : ILoggerProvider
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public ILogger CreateLogger(string categoryName) => new Counter(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Counter(CommandCountingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (category.StartsWith("Microsoft.EntityFrameworkCore.Database.Command", StringComparison.Ordinal)
                && formatter(state, exception).Contains("Executed DbCommand", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref owner._count);
            }
        }
    }
}
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Convenciones.Worker;

public static class WorkerConnectionString
{
    /// <summary>
    /// Resolves the same connection inputs as the API and the migrator, without re-implementing
    /// quoting: the deployment passes parts, and Npgsql builds the string.
    /// </summary>
    public static string? Resolve(IConfiguration configuration)
    {
        var direct = configuration.GetConnectionString("convenciones");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var host = configuration["Database:Host"];
        var user = configuration["Database:User"];
        var password = configuration["Database:Password"];

        if (string.IsNullOrWhiteSpace(password))
        {
            var passwordFile = configuration["Database:PasswordFile"];
            if (!string.IsNullOrWhiteSpace(passwordFile) && File.Exists(passwordFile))
            {
                password = File.ReadAllText(passwordFile).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            var encoded = configuration["Database:PasswordBase64"];
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                password = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
        }

        if (string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(user)
            || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        _ = int.TryParse(configuration["Database:Port"], out var port);
        return new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port == 0 ? 5432 : port,
            Database = configuration["Database:Name"] ?? "postgres",
            Username = user,
            Password = password,
            SslMode = Npgsql.SslMode.Require,
        }.ConnectionString;
    }
}

public static class WorkerOptionsFactory
{
    public static WorkerOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Worker");
        _ = bool.TryParse(section["Enabled"], out var enabled);
        _ = bool.TryParse(section["RequireScope"], out var requireScope);
        _ = int.TryParse(section["PollIntervalSeconds"], out var poll);
        _ = int.TryParse(section["LeaseSeconds"], out var lease);
        _ = int.TryParse(section["AttemptTimeoutSeconds"], out var timeout);
        _ = int.TryParse(section["MaxAttempts"], out var maxAttempts);

        var backoff = (section["BackoffSeconds"] ?? "30,120")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var seconds) ? seconds : 30)
            .ToArray();

        var deadlineRaw = section["DeadlineUtc"];
        DateTimeOffset? deadline = null;
        var deadlineInvalid = false;
        if (!string.IsNullOrWhiteSpace(deadlineRaw))
        {
            if (DateTimeOffset.TryParse(
                    deadlineRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                deadline = parsed;
            }
            else
            {
                deadlineInvalid = true;
            }
        }

        var allowedDocumentId = ParseGuid(section["AllowedDocumentId"], out var documentIdInvalid);
        var allowedRunId = ParseGuid(section["AllowedRunId"], out var runIdInvalid);
        var allowedCompanyId = ParseGuid(section["AllowedCompanyId"], out var companyIdInvalid);
        var authorizedCompanyId = ParseGuid(section["AuthorizedCompanyId"], out var authorizedCompanyIdInvalid);

        return new WorkerOptions
        {
            Enabled = enabled,
            DeadlineRaw = deadlineRaw,
            DeadlineUtc = deadline,
            DeadlineInvalid = deadlineInvalid,
            AllowedDocumentId = allowedDocumentId,
            AllowedRunId = allowedRunId,
            AllowedCompanyId = allowedCompanyId,
            AuthorizedCompanyId = authorizedCompanyId,
            ScopeInvalid = documentIdInvalid || runIdInvalid || companyIdInvalid || authorizedCompanyIdInvalid,
            RequireScope = requireScope,
            PollIntervalSeconds = poll == 0 ? 2 : poll,
            LeaseSeconds = lease == 0 ? 600 : lease,
            AttemptTimeoutSeconds = timeout == 0 ? 600 : timeout,
            MaxAttempts = maxAttempts == 0 ? 3 : maxAttempts,
            BackoffSeconds = backoff.Length == 0 ? [30, 120] : backoff,
            HealthFile = section["HealthFile"] ?? "/tmp/worker-ready",
        };
    }

    private static Guid? ParseGuid(string? value, out bool invalid)
    {
        invalid = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Guid.TryParse(value, out var parsed))
        {
            return parsed;
        }

        invalid = true;
        return null;
    }
}

public sealed class UnavailableProcessingStage : IProcessingStage
{
    public Task ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken) =>
        throw new PermanentProcessingException(
            "stage_not_configured",
            "No processing stage is registered; the worker must not claim runs yet.");
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var connectionString = WorkerConnectionString.Resolve(builder.Configuration);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                "No database connection. Set ConnectionStrings__convenciones or Database__Host/User/Password.");
            return 2;
        }

        var options = WorkerOptionsFactory.From(builder.Configuration);
        var deadlineError = WorkerDeadlinePolicy.Validate(options, DateTimeOffset.UtcNow);
        if (deadlineError is not null)
        {
            Console.Error.WriteLine(deadlineError);
            return 3;
        }

        var scopeError = WorkerScopePolicy.Validate(options);
        if (scopeError is not null)
        {
            Console.Error.WriteLine(scopeError);
            return options.ScopeInvalid ? 4 : 5;
        }

        var scopeValidationError = await WorkerScopeValidator.ValidateAsync(
            connectionString,
            options,
            CancellationToken.None).ConfigureAwait(false);
        if (scopeValidationError is not null)
        {
            Console.Error.WriteLine(scopeValidationError);
            return 6;
        }
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new ProcessingQueue(connectionString, options));
        WorkerComposition.AddProcessingStage(builder.Services, builder.Configuration, connectionString);
        builder.Services.AddSingleton(new RetentionOptions
        {
            IntervalMinutes = ReadPositive(builder.Configuration["Retention:IntervalMinutes"], 360),
            BatchSize = ReadPositive(builder.Configuration["Retention:BatchSize"], 100),
        });
        builder.Services.AddSingleton(serviceProvider => new ProcessingRetentionSweeper(
            connectionString,
            serviceProvider.GetRequiredService<RetentionOptions>(),
            serviceProvider.GetRequiredService<WorkerOptions>(),
            serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProcessingRetentionSweeper>>()));
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ProcessingRetentionSweeper>());
        builder.Services.AddHostedService<ProcessingWorker>();

        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static int ReadPositive(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Convenciones.LoadTest;

/// <summary>
/// Runs the real API as a child process on Kestrel over loopback.
/// </summary>
/// <remarks>
/// The harness does not host the API in-process: <c>WebApplicationFactory</c> would replace Kestrel
/// with the in-memory test transport and the HTTP path being measured would not be the one that ships.
/// Configuration goes through the same keys the deployment uses; the signing key is generated per run
/// and never written anywhere.
/// </remarks>
public sealed class ApiProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _log = new();
    private readonly string _storageRoot;
    private readonly HttpClient _probe;

    private ApiProcess(Process process, string baseAddress, string storageRoot)
    {
        _process = process;
        BaseAddress = baseAddress;
        _storageRoot = storageRoot;
        _probe = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(5) };
    }

    public string BaseAddress { get; }

    public static async Task<ApiProcess> StartAsync(
        string connectionString,
        string jwtIssuer,
        string jwtAudience,
        string jwtSigningKey,
        TimeSpan readinessTimeout,
        CancellationToken cancellationToken = default)
    {
        var port = FreePort();
        var baseAddress = $"http://127.0.0.1:{port}";
        var storageRoot = Path.Combine(Path.GetTempPath(), $"convenciones-load-{Guid.NewGuid():N}");

        var startInfo = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = RepositoryPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(RepositoryPaths.ApiAssembly());
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_URLS"] = baseAddress;
        startInfo.Environment["ConnectionStrings__convenciones"] = connectionString;
        startInfo.Environment["Jwt__Issuer"] = jwtIssuer;
        startInfo.Environment["Jwt__Audience"] = jwtAudience;
        startInfo.Environment["Jwt__SigningKey"] = jwtSigningKey;
        startInfo.Environment["Documents__StorageRoot"] = storageRoot;
        startInfo.Environment["Documents__DeletionIntervalSeconds"] = "0";
        startInfo.Environment["Operations__MetricsIntervalSeconds"] = "300";
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        startInfo.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";

        Directory.CreateDirectory(storageRoot);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The API process could not be started.");

        var api = new ApiProcess(process, baseAddress, storageRoot);
        process.OutputDataReceived += (_, args) => api.Record(args.Data);
        process.ErrorDataReceived += (_, args) => api.Record(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await api.WaitUntilReadyAsync(readinessTimeout, cancellationToken).ConfigureAwait(false);
        return api;
    }

    public async ValueTask DisposeAsync()
    {
        _probe.Dispose();

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();

        try
        {
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; a leaked temp directory must not fail a measurement.
        }
    }

    /// <summary>The last lines of the child's output, for a startup failure message.</summary>
    public string RecentLog()
    {
        var builder = new StringBuilder();
        foreach (var line in _log)
        {
            builder.AppendLine(line);
        }

        return builder.Length > 8000 ? builder.ToString()[^8000..] : builder.ToString();
    }

    private async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var giveUpAt = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < giveUpAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The API exited with code {_process.ExitCode} before it became ready.{Environment.NewLine}" +
                    RecentLog());
            }

            try
            {
                using var response = await _probe.GetAsync("/health/ready", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException exception)
            {
                lastError = exception;
            }
            catch (TaskCanceledException exception)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"The API did not become ready within {timeout.TotalSeconds:0} s. Last error: {lastError?.Message}" +
            $"{Environment.NewLine}{RecentLog()}");
    }

    private void Record(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _log.Enqueue(line);
        while (_log.Count > 400)
        {
            _log.TryDequeue(out _);
        }
    }

    private static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

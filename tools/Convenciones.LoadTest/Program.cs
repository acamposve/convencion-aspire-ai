using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.LoadTest;

return await LoadTestProgram.RunAsync(args);

internal static class LoadTestProgram
{
    private const string JwtIssuer = "https://load.convenciones.local";
    private const string JwtAudience = "convenciones-load";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0] switch
            {
                "self-test" or "--self-test" => await SelfTest.RunAsync().ConfigureAwait(false),
                "run" => await RunCommandAsync(Options.Parse(args[1..])).ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'. Use 'run' or 'self-test'."),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"harness failed: {exception.Message}");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<int> RunCommandAsync(Options options)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var targets = new TargetSet(
            Label: "initial benchmark targets — not an SLA",
            ReadP95Ms: 200,
            ReadP99Ms: 500,
            WriteP95Ms: 400,
            WriteP99Ms: 1000,
            UploadP95Ms: 1500,
            UploadP99Ms: 3000,
            MaxServerErrorRatePercent: 0.1,
            QueueWaitP95Seconds: 60,
            RunDurationP95Seconds: 30,
            DrainSeconds: 1800,
            AvailabilityNote: "Not measured locally: 99.9% availability needs sustained observation in an authorized environment.");

        var limitations = new List<string>
        {
            "Availability (99.9%) is not measured: a local run cannot produce it.",
            "Real Azure Document Intelligence/OpenAI latency and cost are not measured; the worker uses a 2 s stub stage.",
            "TLS, nginx and the deployment network path are excluded; the load is HTTP over loopback to Kestrel.",
            "One API instance and one worker; horizontal scaling is out of scope.",
            "The load generator shares the machine with the system under test; generator CPU is reported so contention is visible.",
            "Synthetic PDFs only; extraction and classification quality is out of scope (P1-006).",
        };

        var fixtures = new List<string>();
        var apiScenarios = new List<ApiScenarioReport>();
        var workerScenarios = new List<WorkerScenarioReport>();

        Console.WriteLine($"P2-005 load run '{options.Label}' (scale {options.Scale:0.##})");
        Console.WriteLine("starting postgres:17-alpine and applying migrations + seed...");

        await using var database = new PostgresHarness();
        await database.StartAsync().ConfigureAwait(false);
        Console.WriteLine("postgres ready");

        var fixture = new LoadFixture();
        await fixture.SeedTenantAsync(database).ConfigureAwait(false);
        await fixture.LoadHeadingAsync(database).ConfigureAwait(false);

        var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        fixture.Token = JwtTokenFactory.Create(
            fixture.UserId,
            JwtIssuer,
            JwtAudience,
            signingKey,
            TimeSpan.FromHours(12));

        var apiAddress = "not started";

        if (options.Only is "all" or "api")
        {
            await using var api = await ApiProcess
                .StartAsync(
                    database.ApiConnectionString,
                    JwtIssuer,
                    JwtAudience,
                    signingKey,
                    TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);

            apiAddress = api.BaseAddress;
            Console.WriteLine($"api ready at {apiAddress}");

            using var http = CreateClient(apiAddress);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);

            Console.WriteLine("creating API fixtures...");
            await fixture.CreateDocumentsAsync(http, 40, SyntheticPdf.OfSize(64 * 1024)).ConfigureAwait(false);
            await fixture.CreateAgreementsAsync(http, 60, clausesPerAgreement: 5).ConfigureAwait(false);
            await fixture.PublishAgreementsAsync(http, count: 20).ConfigureAwait(false);

            fixtures.Add(await fixture.DescribeAsync(database).ConfigureAwait(false));
            await database.AnalyzeAsync().ConfigureAwait(false);

            using (var warmup = new HttpRequestMessage(HttpMethod.Get, "/documents"))
            using (await http.SendAsync(warmup).ConfigureAwait(false))
            {
                // Warm the first request path before the clock starts.
            }

            var scenarios = new ApiScenarios(http, fixture);

            Console.WriteLine("api-mixed: 20 req/s for 10 min (85/15 read/write)...");
            apiScenarios.Add(await scenarios
                .MixedAsync(TimeSpan.FromSeconds(600 * options.Scale), 20)
                .ConfigureAwait(false));

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            Console.WriteLine("api-burst: 100 req/s for 60 s...");
            apiScenarios.Add(await scenarios
                .BurstAsync(100, TimeSpan.FromSeconds(60 * options.Scale))
                .ConfigureAwait(false));

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            Console.WriteLine("api-upload: 2 uploads/s of 1 MiB for 5 min...");
            apiScenarios.Add(await scenarios
                .UploadsAsync(
                    TimeSpan.FromSeconds(300 * options.Scale),
                    2,
                    SyntheticPdf.OfSize(1024 * 1024))
                .ConfigureAwait(false));
        }

        if (options.Only is "all" or "worker")
        {
            await database.AnalyzeAsync().ConfigureAwait(false);
            var worker = new WorkerScenarios(database, fixture);

            var burstDocuments = Math.Max(10, (int)Math.Round(70 * options.Scale));
            Console.WriteLine($"worker-burst: {burstDocuments} documents over {600 * options.Scale:0} s...");
            workerScenarios.Add(await worker
                .BurstAsync(burstDocuments, TimeSpan.FromSeconds(600 * options.Scale))
                .ConfigureAwait(false));

            var drainDocuments = Math.Max(20, (int)Math.Round(500 * options.Scale));
            Console.WriteLine($"worker-drain: {drainDocuments} documents, one worker...");
            workerScenarios.Add(await worker
                .DrainAsync(drainDocuments)
                .ConfigureAwait(false));
        }

        var environment = new EnvironmentInfo(
            MachineName: Environment.MachineName,
            OsDescription: RuntimeInformation.OSDescription,
            OsArchitecture: RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            LogicalProcessors: Environment.ProcessorCount,
            GcMemoryLimitBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            DotNetVersion: Environment.Version.ToString(),
            DockerServerVersion: TryDockerVersion(),
            PostgresImage: PostgresHarness.Image,
            ApiBaseAddress: apiAddress,
            ApiConnectionPoolSize: PostgresHarness.ApiPoolSize,
            PostgresMaxConnections: PostgresHarness.MaxConnections);

        var report = new RunReport(
            Label: options.Label,
            StartedAt: startedAt,
            FinishedAt: DateTimeOffset.UtcNow,
            Environment: environment,
            Targets: targets,
            ApiScenarios: apiScenarios,
            WorkerScenarios: workerScenarios,
            Limitations: limitations,
            Fixtures: fixtures);

        RunReportWriter.Write(report, options.OutputPath);

        PrintSummary(report);
        Console.WriteLine($"report written to {Path.GetFullPath(options.OutputPath)}");
        return 0;
    }

    private static HttpClient CreateClient(string baseAddress) =>
        new(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 256,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = TimeSpan.FromSeconds(120),
        };

    private static void PrintSummary(RunReport report)
    {
        Console.WriteLine();
        Console.WriteLine("api scenarios");
        Console.WriteLine(
            "name             reqs    ok  fail   err%     rps   p50ms   p95ms   p99ms  cpu(cores)  peakConcurrency");

        foreach (var scenario in report.ApiScenarios)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{scenario.Name,-16} {scenario.Requested,5} {scenario.Succeeded,5} {scenario.Failed,5} " +
                $"{scenario.ErrorRatePercent,6:0.00} {scenario.ThroughputPerSecond,7:0.0} " +
                $"{scenario.LatencyMs.P50,7:0.0} {scenario.LatencyMs.P95,7:0.0} {scenario.LatencyMs.P99,7:0.0} " +
                $"{scenario.GeneratorCpuCores,10:0.00} {scenario.PeakConcurrency,15}"));
        }

        if (report.WorkerScenarios.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("worker scenarios");
            Console.WriteLine(
                "name             docs   ok  fail  drain(s)  docs/min  wait p95(s)  run p95(s)  maxQueue");

            foreach (var scenario in report.WorkerScenarios)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{scenario.Name,-16} {scenario.Documents,5} {scenario.Completed,4} {scenario.Failed,5} " +
                    $"{scenario.DrainSeconds,9:0.0} {scenario.DocumentsPerMinute,9:0.0} " +
                    $"{scenario.QueueWaitSeconds.P95,11:0.0} {scenario.RunDurationSeconds.P95,11:0.0} " +
                    $"{scenario.MaxQueueDepth,9}"));
            }
        }
    }

    private static string? TryDockerVersion()
    {
        try
        {
            var startInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("version");
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add("{{.Server.Version}}");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            var value = output.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Convenciones.LoadTest — P2-005 capacity harness.

            Usage:
              dotnet run --project tools/Convenciones.LoadTest -- self-test
              dotnet run --project tools/Convenciones.LoadTest -- run --output <path> [--label <name>] [--only all|api|worker] [--scale <factor>]

            self-test does not need Docker or a database. 'run' needs Docker and a Release build of the API.
            --scale multiplies every duration and fixture count (1 by default); use it only for smoke runs.
            """);
    }

    private sealed record Options(string OutputPath, string Label, string Only, double Scale)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var index = 0; index < args.Length; index++)
            {
                var key = args[index];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unexpected argument '{key}'.");
                }

                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException($"Option '{key}' needs a value.");
                }

                values[key] = args[++index];
            }

            var output = values.GetValueOrDefault("--output")
                ?? throw new ArgumentException("--output is required, for example --output docs/operations/p2-005-results/2026-11-22-run1.json");
            var label = values.GetValueOrDefault("--label") ?? "run";
            var only = values.GetValueOrDefault("--only") ?? "all";
            if (only is not ("all" or "api" or "worker"))
            {
                throw new ArgumentException("--only must be all, api or worker.");
            }

            var scale = 1.0;
            if (values.TryGetValue("--scale", out var raw))
            {
                scale = double.Parse(raw, CultureInfo.InvariantCulture);
                if (scale is <= 0 or > 1)
                {
                    throw new ArgumentException("--scale must be in (0, 1].");
                }
            }

            return new Options(output, label, only, scale);
        }
    }
}

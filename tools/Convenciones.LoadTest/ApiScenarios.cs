using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Convenciones.LoadTest;

/// <summary>
/// The API scenarios: sustained mixed read/write, a short burst, and a 1 MiB upload stream.
/// </summary>
public sealed class ApiScenarios(HttpClient api, LoadFixture fixture)
{
    private int _writeNumber;
    public async Task<ApiScenarioReport> MixedAsync(
        TimeSpan duration,
        double ratePerSecond,
        CancellationToken cancellationToken = default)
    {
        var result = await LoadRunner
            .RunAsync(api, ratePerSecond, duration, Mixed, cancellationToken)
            .ConfigureAwait(false);

        return Report("api-mixed", duration, result, "20 req/s sustained, 85/15 read/write");
    }

    public async Task<ApiScenarioReport> BurstAsync(
        double ratePerSecond,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var result = await LoadRunner
            .RunAsync(api, ratePerSecond, duration, Mixed, cancellationToken)
            .ConfigureAwait(false);

        return Report("api-burst", duration, result, "same 85/15 mix at 5x the sustained rate");
    }

    public async Task<ApiScenarioReport> UploadsAsync(
        TimeSpan duration,
        double ratePerSecond,
        byte[] pdf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        var index = 0;
        LoadRunner.LoadRequest NextUpload(int _)
        {
            var number = Interlocked.Increment(ref index);
            var content = new MultipartFormDataContent($"load-{Guid.NewGuid():N}");
            var file = new ByteArrayContent(pdf);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Add(file, "file", $"load-{number:D6}.pdf");

            var request = new HttpRequestMessage(HttpMethod.Post, "/documents") { Content = content };
            return new LoadRunner.LoadRequest(request, "upload");
        }

        var result = await LoadRunner
            .RunAsync(api, ratePerSecond, duration, NextUpload, cancellationToken)
            .ConfigureAwait(false);

        return Report("api-upload", duration, result, "1 MiB PDF per request");
    }

    private static ApiScenarioReport Report(
        string name,
        TimeSpan duration,
        LoadRunner.Result result,
        string note)
    {
        var read = result.Samples.Any(sample => sample.Kind == "read") ? result.Latency("read") : null;
        var write = result.Samples.Any(sample => sample.Kind == "write") ? result.Latency("write") : null;
        var upload = result.Samples.Any(sample => sample.Kind == "upload") ? result.Latency("upload") : null;

        return new ApiScenarioReport(
            Name: name,
            Kind: "api",
            DurationSeconds: (int)Math.Round(duration.TotalSeconds),
            Requested: result.Requested,
            Succeeded: result.Succeeded,
            Failed: result.Failed,
            ThroughputPerSecond: result.Completed / result.ElapsedSeconds,
            ErrorRatePercent: result.Completed == 0 ? 0 : 100.0 * result.Failed / result.Completed,
            LatencyMs: result.Latency(),
            ReadLatencyMs: read,
            WriteLatencyMs: write,
            UploadLatencyMs: upload,
            StatusCodes: result.Outcomes,
            GeneratorCpuCores: result.GeneratorCpuCores,
            MeanConcurrency: result.MeanConcurrency,
            PeakConcurrency: result.PeakConcurrency,
            Notes: [note]);
    }

    private LoadRunner.LoadRequest Mixed(int index) =>
        index % 20 < 3 ? Write(index) : Read(index);

    private LoadRunner.LoadRequest Read(int index)
    {
        var documentId = fixture.DocumentIds[index % fixture.DocumentIds.Count];
        var agreementId = fixture.AgreementIds[index % fixture.AgreementIds.Count];

        return (index % 5) switch
        {
            0 => new(new HttpRequestMessage(HttpMethod.Get, "/documents?page=1&pageSize=25"), "read"),
            1 => new(new HttpRequestMessage(HttpMethod.Get, $"/documents/{documentId}"), "read"),
            2 => new(new HttpRequestMessage(HttpMethod.Get, "/agreements?page=1&pageSize=25"), "read"),
            3 => new(new HttpRequestMessage(HttpMethod.Get, $"/agreements/{agreementId}/clauses"), "read"),
            _ => new(new HttpRequestMessage(HttpMethod.Get, "/public/agreements?page=1&pageSize=25"), "read"),
        };
    }

    private LoadRunner.LoadRequest Write(int index)
    {
        // (company, valid_from) is unique, so every write gets its own day far from the fixtures.
        var number = Interlocked.Increment(ref _writeNumber);
        var validFrom = new DateOnly(2030, 1, 1)
            .AddDays(number)
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var body = new
        {
            companyId = fixture.CompanyId,
            validFrom,
            validTo = (string?)null,
            scope = $"load write {number:D6}",
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/agreements")
        {
            Content = JsonContent.Create(body),
        };

        return new LoadRunner.LoadRequest(request, "write");
    }
}

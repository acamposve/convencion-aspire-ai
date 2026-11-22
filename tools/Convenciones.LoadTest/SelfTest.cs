using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Convenciones.LoadTest;

/// <summary>
/// Proves the generator measures what it claims: against a local stub server it must observe an
/// injected 300 ms delay and an injected 1% server-error rate, with no database and no Docker.
/// </summary>
/// <remarks>
/// This is the part that runs in CI. A harness that silently reports successes for every request would
/// pass a manual run on a healthy system; it fails here.
/// </remarks>
public static class SelfTest
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();

        await using var server = await StubHttpServer.StartAsync(cancellationToken).ConfigureAwait(false);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(server.BaseAddress),
            Timeout = TimeSpan.FromSeconds(30),
        };

        using (var warmup = await client.GetAsync("/ok", cancellationToken).ConfigureAwait(false))
        {
            if (!warmup.IsSuccessStatusCode)
            {
                failures.Add($"warm-up returned {(int)warmup.StatusCode}");
            }
        }

        var ok = await LoadRunner
            .RunAsync(client, 50, TimeSpan.FromSeconds(3), _ => Request("/ok"), cancellationToken)
            .ConfigureAwait(false);
        var slow = await LoadRunner
            .RunAsync(client, 50, TimeSpan.FromSeconds(3), _ => Request("/slow"), cancellationToken)
            .ConfigureAwait(false);
        var failing = await LoadRunner
            .RunAsync(client, 50, TimeSpan.FromSeconds(3), _ => Request("/fail"), cancellationToken)
            .ConfigureAwait(false);

        if (ok.Failed != 0)
        {
            failures.Add($"/ok had {ok.Failed} failures");
        }

        var okP95 = ok.Latency().P95;
        if (okP95 > 250)
        {
            failures.Add($"/ok p95 was {okP95:0.0} ms, expected under 250 ms");
        }

        var slowP95 = slow.Latency().P95;
        if (slowP95 is < 250 or > 900)
        {
            failures.Add($"/slow p95 was {slowP95:0.0} ms, expected 250-900 ms for the injected 300 ms");
        }

        var failRate = failing.Completed == 0 ? 0 : 100.0 * failing.Failed / failing.Completed;
        if (failRate is < 0.2 or > 3.0)
        {
            failures.Add($"error rate was {failRate:0.00}%, expected 0.2-3% for the injected 1%");
        }

        if (failing.Failed == 0)
        {
            failures.Add("the server errors were not counted");
        }

        Console.WriteLine(
            $"self-test: ok p95={okP95:0.0} ms, slow p95={slowP95:0.0} ms, " +
            $"error rate={failRate.ToString("0.00", CultureInfo.InvariantCulture)}% " +
            $"({failing.Failed}/{failing.Completed})");

        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"self-test FAILED: {failure}");
        }

        Console.WriteLine(failures.Count == 0 ? "self-test PASSED" : $"self-test FAILED ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
    }

    private static LoadRunner.LoadRequest Request(string path) =>
        new(new HttpRequestMessage(HttpMethod.Get, path), "read");

    /// <summary>A minimal HTTP/1.1 server over loopback: no ASP.NET Core, no Kestrel, no Docker.</summary>
    private sealed class StubHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly List<Task> _connections = [];
        private Task? _acceptLoop;
        private int _requests;

        private StubHttpServer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseAddress = $"http://127.0.0.1:{port}";
        }

        public string BaseAddress { get; }

        public static Task<StubHttpServer> StartAsync(CancellationToken cancellationToken)
        {
            var server = new StubHttpServer();
            server._acceptLoop = server.AcceptLoopAsync(cancellationToken);
            return Task.FromResult(server);
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);

            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
                // Already stopped.
            }

            if (_acceptLoop is not null)
            {
                try
                {
                    await _acceptLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }
            }

            await Task.WhenAll(_connections).ConfigureAwait(false);
            _cancellation.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);

            while (!linked.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                _connections.Add(HandleAsync(client, linked.Token));

                if (_connections.Count > 512)
                {
                    _connections.RemoveAll(task => task.IsCompleted);
                }
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using var connection = client;

            try
            {
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
                {
                    // Drain the headers; the stub ignores them.
                }

                var path = requestLine.Split(' ') is [_, var target, ..] ? target : "/";
                var status = 200;
                var body = "ok";

                if (path == "/slow")
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
                    body = "slow";
                }
                else if (path == "/fail")
                {
                    var number = Interlocked.Increment(ref _requests);
                    if (number % 100 == 0)
                    {
                        status = 500;
                        body = "fail";
                    }
                }

                var bytes = Encoding.ASCII.GetBytes(body);
                var reason = status == 200 ? "OK" : "Internal Server Error";
                var header = $"HTTP/1.1 {status} {reason}\r\n" +
                             "Content-Type: text/plain\r\n" +
                             $"Content-Length: {bytes.Length}\r\n" +
                             "Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client closed the connection; nothing to report.
            }
        }
    }
}

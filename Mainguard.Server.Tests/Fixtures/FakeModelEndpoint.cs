using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// TI-P2-00 §A.4.3 — a local HTTP listener that scripts model-API responses in order
/// (200 with rate-limit headers, 401, 429 + <c>Retry-After</c>, slow-stream). Used by
/// P2-01 health-check fixtures and the P2-08 "no agent ever sees a raw 429" integration.
/// Replays a fixed queue deterministically so tests assert exact behavior with no clock.
/// </summary>
public sealed class FakeModelEndpoint : IDisposable
{
    /// <summary>Bind attempts before the port contention is treated as a real failure.</summary>
    private const int MaxStartAttempts = 4;

    private readonly HttpListener _listener = new();
    private readonly Queue<ScriptedResponse> _script = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private int _served;

    public FakeModelEndpoint()
    {
        // Same race as the daemon hosts: an OS-assigned port is released before this listener claims it.
        // TestPorts stops THIS process from double-issuing; the bounded retry covers a foreign process
        // taking the port in between.
        BaseAddress = StartOnFreePort(_listener);
        _ = Task.Run(LoopAsync);
    }

    private static Uri StartOnFreePort(HttpListener listener)
    {
        for (var attempt = 1; ; attempt++)
        {
            var address = new Uri($"http://127.0.0.1:{TestPorts.Lease()}/");
            listener.Prefixes.Clear();
            listener.Prefixes.Add(address.ToString());
            try
            {
                listener.Start();
                return address;
            }
            catch (Exception ex) when (attempt < MaxStartAttempts && TestPorts.IsAddressInUse(ex))
            {
                // Lost the port to another process between the lease and the bind — take another.
            }
        }
    }

    /// <summary>The root URL of the fake endpoint (loopback).</summary>
    public Uri BaseAddress { get; }

    /// <summary>How many requests have been served (replay determinism check).</summary>
    public int ServedCount
    {
        get { lock (_gate) { return _served; } }
    }

    public FakeModelEndpoint EnqueueOk(string body = "{\"ok\":true}", int remaining = 100)
        => Enqueue(new ScriptedResponse(200, body, new Dictionary<string, string>
        {
            ["x-ratelimit-remaining-requests"] = remaining.ToString(),
        }));

    public FakeModelEndpoint EnqueueUnauthorized()
        => Enqueue(new ScriptedResponse(401, "{\"error\":\"unauthorized\"}", new Dictionary<string, string>()));

    public FakeModelEndpoint EnqueueRateLimited(int retryAfterSeconds)
        => Enqueue(new ScriptedResponse(429, "{\"error\":\"rate_limited\"}", new Dictionary<string, string>
        {
            ["retry-after"] = retryAfterSeconds.ToString(),
        }));

    public FakeModelEndpoint EnqueueSlowStream(string body, TimeSpan perChunkDelay)
        => Enqueue(new ScriptedResponse(200, body, new Dictionary<string, string>(), perChunkDelay));

    public FakeModelEndpoint Enqueue(ScriptedResponse response)
    {
        lock (_gate)
        {
            _script.Enqueue(response);
        }

        return this;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return; // listener stopped
            }

            ScriptedResponse response;
            lock (_gate)
            {
                response = _script.Count > 0
                    ? _script.Dequeue()
                    : new ScriptedResponse(200, "{\"ok\":true}", new Dictionary<string, string>());
                _served++;
            }

            await WriteAsync(context.Response, response);
        }
    }

    private static async Task WriteAsync(HttpListenerResponse target, ScriptedResponse response)
    {
        target.StatusCode = response.Status;
        foreach (var (key, value) in response.Headers)
        {
            target.Headers[key] = value;
        }

        var bytes = Encoding.UTF8.GetBytes(response.Body);
        target.ContentLength64 = bytes.Length;
        if (response.PerChunkDelay > TimeSpan.Zero)
        {
            foreach (var b in bytes)
            {
                await target.OutputStream.WriteAsync(new[] { b });
                await Task.Delay(response.PerChunkDelay);
            }
        }
        else
        {
            await target.OutputStream.WriteAsync(bytes);
        }

        target.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        _listener.Close();
        _cts.Dispose();
    }

    public sealed record ScriptedResponse(
        int Status, string Body, IReadOnlyDictionary<string, string> Headers, TimeSpan PerChunkDelay = default);
}

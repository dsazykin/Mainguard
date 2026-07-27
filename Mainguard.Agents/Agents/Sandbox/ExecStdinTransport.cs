using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>One exec whose CONTENT travels on stdin (never argv, never env — G-13).</summary>
/// <param name="ContainerId">The container to run in. Validated against
/// <see cref="DockerSocketExecStdinTransport"/>'s id charset before it is ever interpolated into a
/// request line.</param>
/// <param name="User">The uid the exec runs as, as Docker spells it (<c>"0"</c>, <c>"1000"</c>).</param>
/// <param name="Command">argv. Serialised as JSON, so it needs no quoting and cannot escape its field.</param>
/// <param name="Stdin">The bytes the command reads. The only channel a secret is ever allowed on.</param>
public sealed record ExecStdinRequest(string ContainerId, string User, IReadOnlyList<string> Command, byte[] Stdin);

/// <summary>What the exec did: its exit status and (capped) merged output, for diagnostics.</summary>
public sealed record ExecStdinResult(int ExitCode, string Output);

/// <summary>
/// The seam for "run a command in a container and feed it these bytes on stdin". It exists as an
/// interface for exactly one reason: the production implementation talks raw HTTP to the Docker socket,
/// and the unit suite must be able to drive the engine's failure paths (a wedged endpoint, a non-zero
/// exit) without one.
/// </summary>
public interface IExecStdinTransport
{
    Task<ExecStdinResult> RunAsync(ExecStdinRequest request, CancellationToken ct);
}

/// <summary>
/// Delivers exec stdin over the Docker Engine API <b>without</b> Docker.DotNet's hijacked-stream code.
///
/// <para><b>Why this exists — measured, not assumed.</b> Docker.DotNet 3.125.15 (the latest published
/// version; there is nothing to upgrade to) cannot perform an exec with <c>AttachStdin = true</c> against
/// a modern engine. Against Docker Engine 29.4.3, on a container with the jail's exact shape (read-only
/// rootfs, secrets on tmpfs), every variant of its API fails identically:</para>
/// <list type="bullet">
///   <item>write → <c>CloseWrite</c> → <c>ReadOutputToEndAsync</c>: never returns;</item>
///   <item>the same with a delay before the half-close: never returns;</item>
///   <item>skipping the drain and polling <c>InspectContainerExecAsync</c> instead: the exec stays
///         <c>Running</c> forever.</item>
/// </list>
/// <para>In all of them the in-jail file is created and left <b>0 bytes</b>: the exec runs, the command
/// sees an immediate EOF, and the payload never arrives. So it is not merely the read side — the library
/// delivers neither the bytes nor the half-close. An exec with <c>AttachStdin = false</c> works, and so
/// does <c>docker exec -i</c> from the CLI, so the daemon is not at fault.</para>
///
/// <para><b>Why not <c>PUT /containers/{id}/archive</c>.</b> That was the obvious replacement and it is
/// impossible here, which is worth recording so nobody spends the afternoon again. Docker refuses to
/// extract into a container whose rootfs is read-only unless the destination sits inside a <i>volume or
/// bind mount</i>; <c>HostConfig.Tmpfs</c> entries are neither. Both jail secret destinations
/// (<c>/run/secrets</c>, <see cref="ContainerSpecBuilder.AgentHome"/>) are tmpfs on a
/// <c>ReadonlyRootfs = true</c> container, so the call fails with
/// <c>400 "container rootfs is marked read-only"</c>. Declaring the same tmpfs through
/// <c>HostConfig.Mounts</c> instead gets past that check and is <b>worse</b>: the call then reports
/// success while the bytes land in the image layer <i>underneath</i> the tmpfs, invisible to the
/// container — a silent loss that any test asserting "the API returned OK" would call green.</para>
///
/// <para><b>What this is.</b> The three calls the operation actually needs — create, start-with-upgrade,
/// inspect — spoken directly over the daemon endpoint, which is what Docker.DotNet does internally and
/// what <c>docker exec -i</c> does. The hijack is the whole trick: <c>POST /exec/{id}/start</c> with
/// <c>Upgrade: tcp</c> turns the connection into a raw duplex stream, stdin is written onto it, and
/// <see cref="SocketShutdown.Send"/> half-closes so the command reads a real EOF while the read half
/// stays open for its output.</para>
///
/// <para><b>Pinned to <see cref="ApiVersion"/> on purpose.</b> 1.41 is the newest API that the
/// end-of-life Docker 20.10.24 in today's <c>MainguardEnv</c> speaks, and modern engines still serve it,
/// so one pinned version covers the VM before and after the upgrade this change exists to survive.
/// Nothing here uses a field newer than that.</para>
/// </summary>
public sealed class DockerSocketExecStdinTransport : IExecStdinTransport
{
    /// <summary>The Engine API version every request is pinned to (see the class remarks).</summary>
    public const string ApiVersion = "v1.41";

    /// <summary>How much exec output is kept for the diagnostic. The commands on this path are silent
    /// when they succeed; a runaway writer must not be able to buy unbounded memory on a spawn.</summary>
    private const int MaxCapturedOutputBytes = 8 * 1024;

    /// <summary>Cap on a control-plane JSON response. Generous rather than tight: these bodies are a few
    /// hundred bytes, and a cap that truncated one would turn a working call into a JSON parse error.</summary>
    private const int MaxResponseBytes = 1024 * 1024;

    /// <summary>Docker ids and names are drawn from this set. Checked because the id is interpolated
    /// into a request line: a value containing CR/LF would otherwise be able to inject a second HTTP
    /// request onto the socket. Ids come from the daemon, so this can never fire in practice — which is
    /// exactly why it is asserted rather than assumed.</summary>
    private static readonly Regex SafeId = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    private readonly Func<Uri> _endpoint;

    /// <param name="endpoint">Resolved lazily, on first use: constructing an agent environment must not
    /// require a live daemon (<c>Wsl2AgentEnvironment</c> is built in unit tests that never spawn).</param>
    public DockerSocketExecStdinTransport(Func<Uri> endpoint) =>
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    /// <summary>Binds to the same endpoint the shared <see cref="IDockerClient"/> uses, so there is one
    /// daemon address in the process and not two that can drift apart.</summary>
    public static DockerSocketExecStdinTransport For(IDockerClient docker)
    {
        ArgumentNullException.ThrowIfNull(docker);
        return new DockerSocketExecStdinTransport(() => docker.Configuration.EndpointBaseUri);
    }

    public async Task<ExecStdinResult> RunAsync(ExecStdinRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SafeId.IsMatch(request.ContainerId))
        {
            throw new ArgumentException(
                $"'{request.ContainerId}' is not a syntactically valid Docker container id or name; it is "
                + "interpolated into an HTTP request line and must not be able to carry control characters.",
                nameof(request));
        }

        var execId = await CreateExecAsync(request, ct).ConfigureAwait(false);

        // The exec id goes into two more request lines, and it arrived over the wire. Same check, same
        // reason — a response is not more trustworthy than a caller just because the daemon sent it.
        if (!SafeId.IsMatch(execId))
        {
            throw new DockerApiException(
                System.Net.HttpStatusCode.InternalServerError,
                $"The Docker endpoint returned an exec id that is not a valid identifier: '{Trim(execId)}'.");
        }

        var output = await StartWithStdinAsync(execId, request.Stdin, ct).ConfigureAwait(false);
        var exitCode = await InspectExitCodeAsync(execId, ct).ConfigureAwait(false);
        return new ExecStdinResult(exitCode, output);
    }

    // ---- the three calls ------------------------------------------------------------------------

    private async Task<string> CreateExecAsync(ExecStdinRequest request, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new ExecCreateBody(
            request.User, AttachStdin: true, AttachStdout: true, AttachStderr: true, Tty: false,
            request.Command.ToArray()));

        var json = await SendAsync(
            $"POST /{ApiVersion}/containers/{request.ContainerId}/exec HTTP/1.1",
            body, ct).ConfigureAwait(false);

        using var doc = ParseJson(json, "exec create");
        return doc.RootElement.TryGetProperty("Id", out var id) && id.GetString() is { Length: > 0 } value
            ? value
            : throw new DockerApiException(
                System.Net.HttpStatusCode.InternalServerError,
                $"The Docker endpoint accepted the exec create for container '{request.ContainerId}' but "
                + $"returned no exec id. Raw response: {Trim(json)}");
    }

    /// <summary>
    /// The hijack. Writes stdin, half-closes so the command reads EOF, then drains the response to the
    /// daemon's own close — which is the signal that the exec finished.
    /// </summary>
    private async Task<string> StartWithStdinAsync(string execId, byte[] stdin, CancellationToken ct)
    {
        using var socket = await ConnectAsync(ct).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var body = "{\"Detach\":false,\"Tty\":false}"u8.ToArray();
        var head =
            $"POST /{ApiVersion}/exec/{execId}/start HTTP/1.1\r\n"
            + "Host: docker\r\n"
            + "Content-Type: application/json\r\n"
            + $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n"
            + "Connection: Upgrade\r\n"
            + "Upgrade: tcp\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);

        var status = StatusLineOf(await ReadHeadersAsync(stream, ct).ConfigureAwait(false));
        // 101 is the hijack; 200 is what an endpoint that does not upgrade answers. Both then carry the
        // raw stream, so both are accepted — anything else is a real failure and must not be written to.
        if (StatusCodeOf(status) is not (101 or 200))
        {
            throw new DockerApiException(
                System.Net.HttpStatusCode.InternalServerError,
                $"The Docker endpoint refused to start exec '{execId}': {status}");
        }

        if (stdin.Length > 0)
        {
            await stream.WriteAsync(stdin, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        // The half-close IS the EOF the in-container command waits for. Shutting down only the send
        // direction keeps the read half open, so the command's output and the daemon's end-of-exec close
        // still arrive — which is what makes the drain below a completion signal rather than a guess.
        socket.Shutdown(SocketShutdown.Send);

        return Demultiplex(await DrainAsync(stream, MaxCapturedOutputBytes, ct).ConfigureAwait(false));
    }

    private async Task<int> InspectExitCodeAsync(string execId, CancellationToken ct)
    {
        var json = await SendAsync($"GET /{ApiVersion}/exec/{execId}/json HTTP/1.1", body: null, ct)
            .ConfigureAwait(false);
        using var doc = ParseJson(json, "exec inspect");
        return doc.RootElement.TryGetProperty("ExitCode", out var code) && code.ValueKind == JsonValueKind.Number
            ? code.GetInt32()
            : throw new DockerApiException(
                System.Net.HttpStatusCode.InternalServerError,
                $"The Docker endpoint reported no ExitCode for exec '{execId}'. Raw response: {Trim(json)}");
    }

    // ---- plumbing -------------------------------------------------------------------------------

    /// <summary>One plain request/response over its own connection. <c>Connection: close</c> makes the
    /// daemon end the body with the socket, so the body is simply "read to EOF" — but it may still be
    /// chunk-framed inside that (it is, for these endpoints), which <see cref="DecodeBody"/> undoes.</summary>
    private async Task<string> SendAsync(string requestLine, byte[]? body, CancellationToken ct)
    {
        using var socket = await ConnectAsync(ct).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var head = new StringBuilder()
            .Append(requestLine).Append("\r\n")
            .Append("Host: docker\r\n")
            .Append("Connection: close\r\n");
        if (body is not null)
        {
            head.Append("Content-Type: application/json\r\n")
                .Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (body is not null)
            await stream.WriteAsync(body, ct).ConfigureAwait(false);

        var headers = await ReadHeadersAsync(stream, ct).ConfigureAwait(false);
        var payload = DecodeBody(await DrainAsync(stream, MaxResponseBytes, ct).ConfigureAwait(false), headers);

        var status = StatusLineOf(headers);
        if (StatusCodeOf(status) is not (>= 200 and <= 299))
        {
            throw new DockerApiException(
                System.Net.HttpStatusCode.InternalServerError,
                $"Docker answered '{status}' to `{requestLine}`: {Trim(payload)}");
        }

        return payload;
    }

    private async Task<Socket> ConnectAsync(CancellationToken ct)
    {
        var endpoint = _endpoint();
        if (!string.Equals(endpoint.Scheme, "unix", StringComparison.OrdinalIgnoreCase))
        {
            // Deliberately typed and specific rather than a silent fallback to the broken library path.
            // The daemon that spawns jails runs INSIDE MainguardEnv and always reaches dockerd over
            // /var/run/docker.sock; anything else is a misconfiguration worth stopping on.
            throw new NotSupportedException(
                $"Exec stdin is delivered over the Docker unix socket, but the configured endpoint is "
                + $"'{endpoint}'. Docker.DotNet's own stdin path cannot be used instead — it does not "
                + "deliver stdin at all against modern engines (see DockerSocketExecStdinTransport).");
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.LocalPath), ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Reads exactly the header block, byte at a time, so the first payload byte is left in the
    /// stream. Buffering past the blank line would swallow the head of a hijacked stream.</summary>
    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        var headers = new StringBuilder();
        var one = new byte[1];
        var matched = 0;
        // Bounded: a peer that never sends the blank line must not be able to grow this without limit.
        while (headers.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0)
                break;

            headers.Append((char)one[0]);
            matched = one[0] switch
            {
                (byte)'\r' when matched is 0 or 2 => matched + 1,
                (byte)'\n' when matched is 1 or 3 => matched + 1,
                _ => 0,
            };
            if (matched == 4)
                break;
        }

        return headers.ToString();
    }

    /// <summary>Reads to EOF, keeping at most <paramref name="maxCaptured"/> bytes but always consuming
    /// the rest — stopping early would leave the socket half-read and the exec's completion unobserved.
    /// The two callers cap differently on purpose: a control-plane response must not be truncated into
    /// invalid JSON, while a command's output is only ever read by a human inside a diagnostic.</summary>
    private static async Task<byte[]> DrainAsync(Stream stream, int maxCaptured, CancellationToken ct)
    {
        using var captured = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;
            if (captured.Length < maxCaptured)
                captured.Write(buffer, 0, Math.Min(read, maxCaptured - (int)captured.Length));
        }

        return captured.ToArray();
    }

    /// <summary>
    /// Turns a drained response body into text, decoding <c>Transfer-Encoding: chunked</c> when the
    /// daemon used it.
    ///
    /// <para>It does, for these endpoints, despite <c>Connection: close</c> — which is worth stating
    /// because the first version of this transport assumed otherwise and handed the chunk framing
    /// straight to the JSON parser. That failed loudly with the raw bytes in the message
    /// (<c>4a\r\n{"Id":…}\r\n0</c>), which is the only reason it took one run to find rather than
    /// surfacing later as an unexplained spawn failure.</para>
    /// </summary>
    internal static string DecodeBody(byte[] raw, string headers)
    {
        var chunked = headers.Contains("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
            && headers.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        if (!chunked)
            return Encoding.UTF8.GetString(raw);

        using var decoded = new MemoryStream();
        var offset = 0;
        while (offset < raw.Length)
        {
            // <hex-size>[;extension]CRLF
            var lineEnd = IndexOfCrlf(raw, offset);
            if (lineEnd < 0)
                break;

            var sizeToken = Encoding.ASCII.GetString(raw, offset, lineEnd - offset).Split(';')[0].Trim();
            if (!int.TryParse(sizeToken, System.Globalization.NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var size) || size <= 0)
            {
                break; // the terminating 0-chunk, or a body that is not really chunked after all
            }

            offset = lineEnd + 2;
            size = Math.Min(size, raw.Length - offset);
            if (size <= 0)
                break;

            decoded.Write(raw, offset, size);
            offset += size + 2; // skip the chunk's trailing CRLF
        }

        return Encoding.UTF8.GetString(decoded.ToArray());
    }

    private static int IndexOfCrlf(byte[] raw, int from)
    {
        for (var i = from; i + 1 < raw.Length; i++)
        {
            if (raw[i] == (byte)'\r' && raw[i + 1] == (byte)'\n')
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Strips Docker's 8-byte stdout/stderr frame headers from a non-TTY stream. Best effort by design:
    /// this text only ever reaches a human inside a diagnostic, so a stream that does not parse is
    /// returned as-is rather than costing the caller its real error.
    /// </summary>
    private static string Demultiplex(byte[] raw)
    {
        if (raw.Length == 0)
            return string.Empty;

        var text = new StringBuilder();
        var offset = 0;
        while (offset + 8 <= raw.Length)
        {
            // byte 0 is the stream id (0/1/2) and bytes 1..3 are zero — if that does not hold, this is
            // not a framed stream and guessing further would corrupt the message.
            if (raw[offset] > 2 || raw[offset + 1] != 0 || raw[offset + 2] != 0 || raw[offset + 3] != 0)
                return Encoding.UTF8.GetString(raw).Trim();

            var size = (raw[offset + 4] << 24) | (raw[offset + 5] << 16) | (raw[offset + 6] << 8) | raw[offset + 7];
            offset += 8;
            if (size < 0 || offset + size > raw.Length)
                size = raw.Length - offset;

            text.Append(Encoding.UTF8.GetString(raw, offset, size));
            offset += size;
        }

        return text.ToString().Trim();
    }

    private static JsonDocument ParseJson(string payload, string operation)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new DockerApiException(
                System.Net.HttpStatusCode.InternalServerError,
                $"The Docker endpoint's {operation} response was not JSON ({ex.Message}). Raw: {Trim(payload)}");
        }
    }

    /// <summary>The numeric status, or null when the line is not a status line. Parsed rather than
    /// substring-matched: <c>line.Contains(" 2")</c> would also be satisfied by a reason phrase, which is
    /// the kind of "check" that reports success for a failed request.</summary>
    private static int? StatusCodeOf(string statusLine)
    {
        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            ? code
            : null;
    }

    private static string StatusLineOf(string headers) =>
        headers.Split("\r\n", StringSplitOptions.None).FirstOrDefault()?.Trim() ?? "(no status line)";

    private static string Trim(string text) =>
        text.Length <= 512 ? text.Trim() : text[..512].Trim() + "…";

    /// <summary>The exec-create request body. A record so the field names are the wire names and a typo
    /// is a compile error rather than a field Docker silently ignores.</summary>
    private sealed record ExecCreateBody(
        string User, bool AttachStdin, bool AttachStdout, bool AttachStderr, bool Tty, string[] Cmd);
}

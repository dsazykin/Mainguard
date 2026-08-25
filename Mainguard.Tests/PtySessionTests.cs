using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Tests.TestTools;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-03 §3–4 / plan §6 rows 3–6 + §5 invariant 1 — the PTY shim: isatty, echo round-trip,
/// Ctrl+C interrupt, kill, resize propagation. The Unix probes run on Linux AND macOS (forkpty —
/// the macos-host substrate spawns daemon-side PTYs on the Mac) and skip on Windows with a
/// reason; a ConPTY smoke exercises the Windows path during self-verify.
/// </summary>
public sealed class PtySessionTests
{
    [UnixOnlyFact]
    public async Task PtySession_Spawn_CatEcho_ShouldRoundTripBytes()
    {
        using var session = PtyProcessShim.Spawn("/bin/cat", Array.Empty<string>(), TempDir(), Env(), 80, 24);

        var payload = Encoding.UTF8.GetBytes("mainguard-pty-echo\n");
        await session.IO.WriteAsync(payload);
        await session.IO.FlushAsync();

        var output = await ReadUntilAsync(session, s => s.Contains("mainguard-pty-echo"), TimeSpan.FromSeconds(5));
        Assert.Contains("mainguard-pty-echo", output);
    }

    [UnixOnlyFact]
    public async Task PtySession_Isatty_ShouldBeTrue()
    {
        using var session = PtyProcessShim.Spawn(
            "/bin/sh", new[] { "-c", "test -t 0 && printf ISATTY_YES" }, TempDir(), Env(), 80, 24);

        var output = await ReadUntilAsync(session, s => s.Contains("ISATTY_YES"), TimeSpan.FromSeconds(5));
        Assert.Contains("ISATTY_YES", output);
    }

    [UnixOnlyFact]
    public async Task PtySession_CtrlC_ShouldInterruptForegroundProcess()
    {
        // /bin/cat blocks forever reading stdin — with no interrupt it never exits, so its ExitCode
        // task completing IS the proof that 0x03 (Ctrl+C) reached the PTY, the line discipline raised
        // SIGINT, and the foreground process was interrupted (edge matrix: "Ctrl+C interrupts").
        //
        // We assert that observable contract, not a specific 130 exit code: Porta.Pty's Unix layer
        // folds signal-termination into ExitCode 0 (WEXITSTATUS is 0 for a signalled child and the
        // WTERMSIG value is a private field it never surfaces), so the interrupt code is unrecoverable
        // through the public API.
        using var session = PtyProcessShim.Spawn("/bin/cat", Array.Empty<string>(), TempDir(), Env(), 80, 24);

        await session.IO.WriteAsync(new byte[] { 0x03 });
        await session.IO.FlushAsync();

        var completed = await Task.WhenAny(session.ExitCode, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(completed == session.ExitCode,
            "0x03 (Ctrl+C) did not interrupt the foreground process — its ExitCode never completed.");
    }

    [UnixOnlyFact]
    public async Task PtySession_Kill_ShouldCompleteExitCode()
    {
        // 'sleep 30' would run for 30s if left alone; Kill() (SIGKILL) reaping it makes ExitCode
        // complete promptly. As above, Porta.Pty reports 0 for a signalled child, so we assert the
        // observable contract — the ExitCode task COMPLETES after Kill() — not a specific 137 code.
        using var session = PtyProcessShim.Spawn(
            "/bin/sh", new[] { "-c", "sleep 30" }, TempDir(), Env(), 80, 24);

        session.Kill();

        var completed = await Task.WhenAny(session.ExitCode, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(completed == session.ExitCode,
            "Kill() did not reap the child — its ExitCode never completed.");
    }

    [UnixOnlyFact]
    public async Task PtySession_Resize_ShouldPropagateToWinsize()
    {
        using var session = PtyProcessShim.Spawn(
            "/bin/sh", new[] { "-c", "sleep 0.4; stty size" }, TempDir(), Env(), 80, 24);

        session.Resize(120, 40); // stty size prints "rows cols" → "40 120"

        var output = await ReadUntilAsync(session, s => s.Contains("40 120"), TimeSpan.FromSeconds(5));
        Assert.Contains("40 120", output);
    }

    [WindowsOnlyFact]
    public async Task PtySession_ConPty_ShouldCaptureOutput_AndComplete()
    {
        var whoami = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        using var session = PtyProcessShim.Spawn(whoami, Array.Empty<string>(), TempDir(), Env(), 80, 24);

        var output = await ReadUntilAsync(session, s => s.Trim().Length > 0, TimeSpan.FromSeconds(10));
        Assert.False(string.IsNullOrWhiteSpace(output));

        var exit = await WaitWithTimeout(session.ExitCode, TimeSpan.FromSeconds(10));
        Assert.Equal(0, exit);
    }

    private static async Task<string> ReadUntilAsync(PtySession session, Func<string, bool> predicate, TimeSpan timeout)
    {
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var read = await session.IO.ReadAsync(buffer.AsMemory(), cts.Token);
                if (read <= 0)
                {
                    break; // EOF
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (predicate(sb.ToString()))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out — return what we have; the caller asserts.
        }
        catch (IOException)
        {
            // PTY closed.
        }

        return sb.ToString();
    }

    private static async Task<int> WaitWithTimeout(Task<int> exitCode, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(exitCode, Task.Delay(timeout));
        Assert.True(completed == exitCode, "ExitCode did not complete within the timeout.");
        return await exitCode;
    }

    private static string TempDir() => Path.GetTempPath();

    private static IReadOnlyDictionary<string, string> Env()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            if (kv.Key is string k && kv.Value is string v)
            {
                env[k] = v;
            }
        }

        env["TERM"] = "xterm-256color";
        return env;
    }
}

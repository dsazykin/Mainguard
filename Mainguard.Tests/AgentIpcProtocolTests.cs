using System;
using Mainguard.Agents.Agents.Ipc;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// PR3 — the coordinator→daemon spawn channel's pure pieces: the newline-delimited JSON codec and
/// the <c>mainguard-agent</c> shim script the daemon writes into the coordinator's read-only IPC dir.
/// </summary>
public class AgentIpcProtocolTests
{
    [Fact]
    public void Request_RoundTrips()
    {
        var line = AgentIpcProtocol.SerializeRequest(new AgentIpcRequest("spawn", "claude-code", "split the work"));
        Assert.DoesNotContain('\n', line);

        var parsed = AgentIpcProtocol.TryParseRequest(line);
        Assert.NotNull(parsed);
        Assert.Equal("spawn", parsed!.Op);
        Assert.Equal("claude-code", parsed.AgentKind);
        Assert.Equal("split the work", parsed.TaskPrompt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"agentKind\":\"x\"}")] // no op
    [InlineData("{\"op\":\"\"}")]         // empty op
    [InlineData("[1,2,3]")]
    public void MalformedRequest_ParsesToNull_NeverThrows(string line)
    {
        Assert.Null(AgentIpcProtocol.TryParseRequest(line));
    }

    [Fact]
    public void Response_SerializesAsOneLine_OmittingNulls()
    {
        var ok = AgentIpcProtocol.SerializeResponse(new AgentIpcResponse(Ok: true, AgentId: "a1"));
        Assert.DoesNotContain('\n', ok);
        Assert.Contains("\"agentId\":\"a1\"", ok, StringComparison.Ordinal);
        Assert.DoesNotContain("error", ok, StringComparison.Ordinal);

        var error = AgentIpcProtocol.SerializeResponse(new AgentIpcResponse(Ok: false, Error: "refused"));
        Assert.Contains("\"ok\":false", error, StringComparison.Ordinal);
        Assert.Contains("refused", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ShimScript_SpeaksTheProtocol_AtTheFixedMountPath()
    {
        // The script is python3 (part of the pre-baked jail toolchain — G-16: nothing is baked into
        // the image), dials the fixed in-jail socket path by default, and emits the contract's ops.
        Assert.StartsWith("#!/usr/bin/env python3", AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains(AgentIpcPaths.SandboxSocketPath, AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains("MAINGUARD_IPC_SOCKET", AgentSpawnShim.Script, StringComparison.Ordinal);

        // Phase 3 — the shim emits the coordinator contract §3 surface. `list` is still accepted on the
        // command line as an alias of `status` (an existing coordinator transcript keeps working), but it
        // is no longer an op ON THE WIRE: both spellings send `status`, so the daemon has one code path
        // to scope and one to test.
        Assert.Contains("\"op\": \"spawn\"", AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains("\"op\": \"status\"", AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains("\"op\": \"prompt\"", AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains("\"op\": \"verify\"", AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains("(\"status\", \"list\")", AgentSpawnShim.Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every op the DAEMON serves a worker has a way for the worker to reach it. An exhaustiveness check
    /// over <see cref="AgentIpcRequest.WorkerOps"/> rather than one assertion per op, because the failure
    /// it guards against is the one this branch keeps finding: a complete handler with nothing able to
    /// call it. <c>commit_work</c> is the newest instance — a daemon that can commit for a worker is worth
    /// nothing if the worker's only executable has no subcommand for it.
    /// </summary>
    [Fact]
    public void TheWorkerShim_CanReachEveryOpTheDaemonServesAWorker()
    {
        foreach (var op in AgentIpcRequest.WorkerOps)
        {
            Assert.Contains($"\"op\": \"{op}\"", WorkerPlanShim.Script, StringComparison.Ordinal);
        }

        // …and the command line a worker actually types for the newest one.
        Assert.Contains("argv[1] == \"commit\"", WorkerPlanShim.Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both shims are Python living inside a C# string literal, so nothing in the C# build has an opinion
    /// about whether they parse. A syntax error would ship to every jail and surface as a worker that
    /// cannot reach the daemon at all — indistinguishable, from the daemon's side, from a worker that
    /// simply never called. Compiled with the real interpreter; skipped where there is none, because a
    /// missing python3 on a dev box is not evidence about the script.
    /// </summary>
    [Fact]
    public void BothShims_AreValidPython()
    {
        foreach (var (name, source) in new[]
                 {
                     ("mainguard-agent", AgentSpawnShim.Script),
                     ("mainguard-plan", WorkerPlanShim.Script),
                 })
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"mg-shim-{Guid.NewGuid():N}-{name}.py");
            System.IO.File.WriteAllText(path, source);
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo("python3")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                start.ArgumentList.Add("-m");
                start.ArgumentList.Add("py_compile");
                start.ArgumentList.Add(path);

                using var process = System.Diagnostics.Process.Start(start);
                if (process is null)
                {
                    return; // no python3 on this box — nothing measured, nothing claimed
                }

                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, $"{name} is not valid python: {stderr}");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return; // python3 is not installed here
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public void IpcPaths_AreTheFixedInJailLayout()
    {
        Assert.Equal("/opt/mainguard/ipc", AgentIpcPaths.SandboxMount);
        Assert.Equal("/opt/mainguard/ipc/daemon.sock", AgentIpcPaths.SandboxSocketPath);
        Assert.Equal("mainguard-agent", AgentIpcPaths.ShimFileName);

        // The outbox is a fixed child of the IPC mount, not a target of its own: the read-only mount
        // keeps covering the shim and the instructions while the mailbox nests inside it.
        Assert.Equal("/opt/mainguard/ipc/outbox", AgentIpcPaths.SandboxOutboxPath);
        Assert.Equal(AgentIpcPaths.SandboxMount + "/" + AgentIpcPaths.OutboxDirName, AgentIpcPaths.SandboxOutboxPath);
    }

    /// <summary>
    /// Both shims speak the channel through ONE transport. They each carried their own copy of
    /// <c>call()</c> before, and the copies had already drifted (one took a timeout, the other did not);
    /// a jail's only route to the daemon is not a place where two implementations may disagree — least of
    /// all now that choosing between two transports lives inside it.
    /// </summary>
    [Fact]
    public void BothShims_EmbedTheOneSharedTransport()
    {
        Assert.Contains(AgentIpcShimTransport.PythonSource, AgentSpawnShim.Script, StringComparison.Ordinal);
        Assert.Contains(AgentIpcShimTransport.PythonSource, WorkerPlanShim.Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The socket is the channel; the outbox is what the channel becomes where a bind-mounted socket is
    /// inert (macOS — the daemon is on the host, the jail is in the engine's Linux VM, and virtiofs does
    /// not proxy AF_UNIX). The fallback is gated on the outbox being WRITABLE, and that gate is
    /// load-bearing: where the socket is real the outbox sits inside the read-only mount, so a daemon
    /// that is genuinely down still reports as a daemon that is down instead of parking the CLI on a poll
    /// loop until its deadline.
    /// </summary>
    [Fact]
    public void TheShimTransport_FallsBackToTheOutbox_OnlyWhenTheOutboxIsWritable()
    {
        var source = AgentIpcShimTransport.PythonSource;

        Assert.Contains(AgentIpcPaths.SandboxOutboxPath, source, StringComparison.Ordinal);
        Assert.Contains("MAINGUARD_IPC_OUTBOX", source, StringComparison.Ordinal);
        Assert.Contains("os.access(OUTBOX_PATH, os.W_OK)", source, StringComparison.Ordinal);

        // The socket is tried FIRST — the outbox is the exception, not the default.
        Assert.True(
            source.IndexOf("_call_socket(request, timeout)", StringComparison.Ordinal)
            < source.IndexOf("_call_outbox(request, timeout)", StringComparison.Ordinal),
            "the shim reaches for the outbox before it has tried the socket");

        // Staged then renamed, so the daemon can never claim half a request.
        Assert.Contains("os.rename(staged, request_path)", source, StringComparison.Ordinal);
    }
}

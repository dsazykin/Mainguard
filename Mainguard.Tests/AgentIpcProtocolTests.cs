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

    [Fact]
    public void IpcPaths_AreTheFixedInJailLayout()
    {
        Assert.Equal("/opt/mainguard/ipc", AgentIpcPaths.SandboxMount);
        Assert.Equal("/opt/mainguard/ipc/daemon.sock", AgentIpcPaths.SandboxSocketPath);
        Assert.Equal("mainguard-agent", AgentIpcPaths.ShimFileName);
    }
}

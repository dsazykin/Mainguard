using System;
using System.Linq;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// PR3 — the pure docker-exec argv builder that starts an installed CLI inside its jail under a
/// TTY. The shape is load-bearing: <c>-i -t</c> (interactive TTY so login prompts work), the agent
/// uid, the workspace workdir, and the fixed wrapper that sources the P2-01 credential file and
/// puts the coordinator's spawn shim on PATH — with the CLI argv passed purely positionally.
/// </summary>
public class SandboxCliLaunchTests
{
    [Fact]
    public void BuildDockerExecArgv_ShapesTheFullInteractiveExec()
    {
        var (command, args) = SandboxCliLaunch.BuildDockerExecArgv(
            "ctr-abc", new[] { "claude", "--permission-mode", "plan" }, agentUid: 1000);

        Assert.Equal("docker", command);
        Assert.Equal(
            new[]
            {
                "exec", "-i", "-t", "-e", "TERM=xterm-256color", "-u", "1000", "-w", "/workspace",
                "ctr-abc", "sh", "-c", SandboxCliLaunch.WrapperScript, "mainguard-launch",
                "claude", "--permission-mode", "plan",
            },
            args);
    }

    [Fact]
    public void WrapperScript_SourcesCredentials_PutsIpcOnPath_AndExecsArgv()
    {
        // The wrapper is fixed text: credentials come from the agent-owned tmpfs (never argv/env —
        // G-13), the IPC mount joins PATH only when present, and "$@" hands off without rewriting.
        // Spelled through the constant on purpose: the wrapper's guard is `[ -r … ]`, so a wrapper that
        // sources a path the spec no longer mounts fails SILENTLY and the agent runs with no credentials.
        Assert.Contains(CredTmpfsSpec.DefaultCredentialPath, SandboxCliLaunch.WrapperScript, StringComparison.Ordinal);
        Assert.Contains(AgentIpcPaths.SandboxMount, SandboxCliLaunch.WrapperScript, StringComparison.Ordinal);
        Assert.EndsWith("exec \"$@\"", SandboxCliLaunch.WrapperScript, StringComparison.Ordinal);

        // Argv-safety of the sh -c pattern: the script text contains no single quotes and no
        // placeholder interpolation — user data can only ever arrive as positional arguments.
        Assert.DoesNotContain('\'', SandboxCliLaunch.WrapperScript);
        Assert.DoesNotContain("{0}", SandboxCliLaunch.WrapperScript, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDockerExecArgv_RefusesEmptyLaunch()
    {
        Assert.Throws<ArgumentException>(() =>
            SandboxCliLaunch.BuildDockerExecArgv("ctr-abc", null!, 1000));
        Assert.Throws<ArgumentException>(() =>
            SandboxCliLaunch.BuildDockerExecArgv("ctr-abc", Array.Empty<string>(), 1000));
        Assert.Throws<ArgumentException>(() =>
            SandboxCliLaunch.BuildDockerExecArgv("ctr-abc", new[] { " " }, 1000));
    }

    [Fact]
    public void BuildDockerExecArgv_RefusesEmptyContainerId()
    {
        Assert.Throws<ArgumentException>(() =>
            SandboxCliLaunch.BuildDockerExecArgv(" ", new[] { "claude" }, 1000));
    }

    [Fact]
    public void BuildDockerExecArgv_NeverWrapsInAHostShell()
    {
        // The daemon-side command is the docker binary exec'd directly; the only `sh` is the fixed
        // in-container wrapper (the same pattern as the sandbox engine's secret writer).
        var (command, args) = SandboxCliLaunch.BuildDockerExecArgv("ctr-abc", new[] { "claude" }, 1000);
        Assert.Equal("docker", command);
        Assert.Equal("exec", args.First());
    }
}

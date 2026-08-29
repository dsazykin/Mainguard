using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// The agent-IPC channel measured <b>from inside a real jail</b>, by running the real shim.
///
/// <para><b>Why this file exists.</b> The channel had complete coverage and was completely unreachable on
/// macOS. Every test of it dialled the daemon's Unix socket from the daemon's own process — same kernel,
/// same filesystem — and passed. In a real jail on that platform the same socket is inert: the daemon
/// runs natively on the Mac, the jail runs inside the container engine's Linux VM, and Docker's file
/// sharing (virtiofs / gRPC-FUSE) does not proxy AF_UNIX across that boundary. The socket bind-mounts in,
/// stat()s as a socket, and every <c>connect()</c> to it returns ECONNREFUSED against a demonstrably
/// listening daemon. All four coordinator tools of contract §3 and the entire worker plan gate were dead,
/// with the suite green.</para>
///
/// <para>So the assertion here is deliberately end-to-end and deliberately in the jail: spawn a hardened
/// container with the endpoint mounted, execute the shim the daemon wrote, and require the daemon's own
/// answer on stdout. Nothing between the CLI and the handler is stubbed, which is the only arrangement
/// that could have caught this.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class AgentIpcJailDockerTests
{
    private readonly ITestOutputHelper _out;

    public AgentIpcJailDockerTests(ITestOutputHelper output) => _out = output;

    /// <summary>Short by necessity: the endpoint binds a Unix socket beneath this root, and
    /// <c>sun_path</c> holds 104 bytes on macOS. See <see cref="TestDataRootIsolation"/>.</summary>
    private static string NewRoot() => Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "ipcd");

    /// <summary>
    /// The production path, unmodified: whichever transport this platform's mount can carry is the one
    /// the shim finds. Before the outbox existed this test failed on macOS with
    /// <c>cannot reach the Mainguard daemon: [Errno 111] Connection refused</c> and passed on Linux,
    /// which is the whole shape of the defect in one assertion.
    /// </summary>
    [RequiresDockerFact]
    public async Task TheRealShim_ReachesTheDaemon_FromInsideARealJail()
    {
        await using var fx = new SandboxFixture();
        using var ipc = new AgentIpcServer(NewRoot());
        var agentId = "ipcjail1";
        var dir = ipc.CreateEndpoint(agentId, (request, id, _) =>
            Task.FromResult(new AgentIpcResponse(Ok: true, Status: $"reached:{request.Op}:{id}")));

        var handle = await fx.SpawnAsync(
            agentId: agentId, ipcDirPath: dir, ipcOutboxPath: AgentIpcPaths.OutboxIn(dir));

        var result = await fx.ExecAsync(handle.ContainerId,
            "sh", "-c", AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Coordinator) + " status");
        _out.WriteLine($"mainguard-agent status => exit {result.ExitCode} out='{result.Stdout.Trim()}' err='{result.Stderr.Trim()}'");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"reached:status:{agentId}", result.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same call with the socket taken away, so the file-framed transport is what carries it — on
    /// every platform, not only the one where it is the only option. Without this, the outbox would be
    /// exercised end-to-end exclusively on a developer's Mac and would rot on the Linux CI leg that gates
    /// merges, which is precisely the arrangement that let the socket's own gap survive.
    /// </summary>
    [RequiresDockerFact]
    public async Task TheOutbox_CarriesTheChannel_WhenTheSocketCannotBeDialled()
    {
        await using var fx = new SandboxFixture();
        using var ipc = new AgentIpcServer(NewRoot());
        var agentId = "ipcjail2";
        var dir = ipc.CreateEndpoint(agentId, (request, id, _) =>
            Task.FromResult(new AgentIpcResponse(Ok: true, Status: $"outbox:{request.Op}:{id}")));

        var handle = await fx.SpawnAsync(
            agentId: agentId, ipcDirPath: dir, ipcOutboxPath: AgentIpcPaths.OutboxIn(dir));

        // Attribution: point the socket at a path that cannot exist, so a pass is only explicable by the
        // outbox. The jail's mount is read-only, so the shim cannot create it either.
        var result = await fx.ExecAsync(handle.ContainerId, "sh", "-c",
            "MAINGUARD_IPC_SOCKET=" + AgentIpcPaths.SandboxMount + "/no-such.sock "
            + AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Coordinator) + " status");
        _out.WriteLine($"socket-less status => exit {result.ExitCode} out='{result.Stdout.Trim()}' err='{result.Stderr.Trim()}'");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"outbox:status:{agentId}", result.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The paired negative, and the reason the fallback is gated on WRITABILITY rather than tried blindly:
    /// with neither transport available the shim must report a daemon it cannot reach, promptly. A shim
    /// that fell back unconditionally would sit in its poll loop for a full deadline — and for the plan
    /// gate's <c>await</c>, whose deadline is deliberately none, forever.
    /// </summary>
    [RequiresDockerFact]
    public async Task WithNeitherTransportAvailable_TheShimReportsAnUnreachableDaemon_RatherThanHanging()
    {
        await using var fx = new SandboxFixture();
        using var ipc = new AgentIpcServer(NewRoot());
        var agentId = "ipcjail3";
        var dir = ipc.CreateEndpoint(agentId, (_, _, _) => Task.FromResult(new AgentIpcResponse(Ok: true)));

        // The endpoint dir is mounted, the outbox is NOT — which is exactly a jail on a substrate whose
        // socket works, talking to a daemon that has gone away.
        var handle = await fx.SpawnAsync(agentId: agentId, ipcDirPath: dir);

        var result = await fx.ExecAsync(handle.ContainerId, "sh", "-c",
            "MAINGUARD_IPC_SOCKET=" + AgentIpcPaths.SandboxMount + "/no-such.sock "
            + AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Coordinator) + " status");
        _out.WriteLine($"no-transport status => exit {result.ExitCode} err='{result.Stderr.Trim()}'");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cannot reach the Mainguard daemon", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the outbox does NOT cost: the shim and the operating instructions stay the daemon's files.
    /// The read-write mount is nested inside the read-only one and covers <c>outbox/</c> alone, so an
    /// agent can post a request and cannot rewrite the program that posts it.
    /// </summary>
    [RequiresDockerFact]
    public async Task TheOutboxIsWritable_AndTheRestOfTheIpcMountIsStillNot()
    {
        await using var fx = new SandboxFixture();
        using var ipc = new AgentIpcServer(NewRoot());
        var agentId = "ipcjail4";
        var dir = ipc.CreateEndpoint(agentId, (_, _, _) => Task.FromResult(new AgentIpcResponse(Ok: true)));

        var handle = await fx.SpawnAsync(
            agentId: agentId, ipcDirPath: dir, ipcOutboxPath: AgentIpcPaths.OutboxIn(dir));

        var probe = await fx.ExecAsync(handle.ContainerId, "sh", "-c",
            "touch " + AgentIpcPaths.SandboxOutboxPath + "/probe && echo OUTBOX-WRITABLE; "
            + "touch " + AgentIpcPaths.SandboxMount + "/probe 2>/dev/null && echo IPC-WRITABLE; "
            + "echo tampered > " + AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Coordinator)
            + " 2>/dev/null && echo SHIM-WRITABLE; true");
        _out.WriteLine($"mount posture => '{probe.Stdout.Trim()}'");

        Assert.Contains("OUTBOX-WRITABLE", probe.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("IPC-WRITABLE", probe.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("SHIM-WRITABLE", probe.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The brief/task separation, measured at the only place it is decided: the bytes the real shim
    /// puts on the wire.</b>
    ///
    /// <para><b>The defect this pins.</b> The shim used to send
    /// <c>{"op":"spawn","agentKind":argv[2],"taskPrompt":" ".join(argv[3:])}</c> — no title at all — and
    /// the daemon filled the hole with <c>request.Title ?? request.TaskPrompt</c>. So the "brief" a worker
    /// was handed by <c>mainguard-plan brief</c> was the task, verbatim, and MAINGUARD.md's "what you are
    /// here to plan (never the task itself)" was false by fallback. Every daemon-side test could hold
    /// <i>because it constructed the request itself</i> and supplied a Title no real coordinator ever
    /// sent; the gap lived entirely in the one component no such test runs — the shim.</para>
    ///
    /// <para>So this asserts on the parsed request as the daemon received it, from a real jail, through
    /// the real shim: the title and the task must arrive as two different strings.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task TheRealShimsSpawn_SendsTheTitleAndTheTaskAsSeparateFields()
    {
        await using var fx = new SandboxFixture();
        using var ipc = new AgentIpcServer(NewRoot());
        var agentId = "ipcjail5";
        AgentIpcRequest? seen = null;
        var dir = ipc.CreateEndpoint(agentId, (request, _, _) =>
        {
            seen = request;
            return Task.FromResult(new AgentIpcResponse(Ok: true, AgentId: "w-1"));
        });

        var handle = await fx.SpawnAsync(
            agentId: agentId, ipcDirPath: dir, ipcOutboxPath: AgentIpcPaths.OutboxIn(dir));

        // Spelled exactly as the coordinator's operating instructions teach it — unquoted task tail
        // included, which is the form a model actually produces.
        var result = await fx.ExecAsync(handle.ContainerId, "sh", "-c",
            AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Coordinator)
            + " spawn claude-code --title 'Fix the token clock'"
            + " --task rewrite TokenClock so expiry is computed in UTC and add boundary tests");
        _out.WriteLine($"spawn => exit {result.ExitCode} out='{result.Stdout.Trim()}' err='{result.Stderr.Trim()}'");
        _out.WriteLine($"daemon saw: title='{seen?.Title}' taskPrompt='{seen?.TaskPrompt}'");

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(seen);
        Assert.Equal("Fix the token clock", seen!.Title);
        Assert.Equal(
            "rewrite TokenClock so expiry is computed in UTC and add boundary tests", seen.TaskPrompt);

        // The point of the whole change: what the worker is handed up front is NOT the work.
        Assert.NotEqual(seen.Title, seen.TaskPrompt);
    }

    /// <summary>
    /// The pre-fix invocation — a bare positional prompt — is <b>refused by the shim itself</b>, without a
    /// round trip, and the refusal names the form that works.
    ///
    /// <para>Refusing rather than deriving is the decision: a derived title is how <c>brief == task</c>
    /// came back the first time. A coordinator that gets this message has everything it needs to retry
    /// correctly on its next turn.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task TheRealShimsSpawn_RefusesTheOldTitlelessForm_AndSaysWhatToRunInstead()
    {
        await using var fx = new SandboxFixture();
        using var ipc = new AgentIpcServer(NewRoot());
        var agentId = "ipcjail6";
        var reached = false;
        var dir = ipc.CreateEndpoint(agentId, (_, _, _) =>
        {
            reached = true;
            return Task.FromResult(new AgentIpcResponse(Ok: true, AgentId: "w-1"));
        });

        var handle = await fx.SpawnAsync(
            agentId: agentId, ipcDirPath: dir, ipcOutboxPath: AgentIpcPaths.OutboxIn(dir));

        var result = await fx.ExecAsync(handle.ContainerId, "sh", "-c",
            AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Coordinator)
            + " spawn claude-code rewrite TokenClock so expiry is computed in UTC");
        _out.WriteLine($"titleless spawn => exit {result.ExitCode} err='{result.Stderr.Trim()}'");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(reached, "a title-less spawn reached the daemon — the shim guessed instead of refusing");
        Assert.Contains("--title", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("--task", result.Stderr, StringComparison.Ordinal);
    }
}

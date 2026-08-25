using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mainguard.Protos.V1;
using Mainguard.Server.Runtime;
using Mainguard.Server.Services;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// ISSUES-LOG #23 — <b>an attach to a real agent that has no terminal must say so.</b>
///
/// <para>The case is a daemon restart under a surviving jail.
/// <see cref="AgentSessionReconciler"/> adopts the container back into the session store, so
/// <c>ListAgents</c> keeps reporting it correctly (<c>state=Working, role=coordinator</c>) — but the
/// terminal cannot come back with it: the CLI runs under a <c>docker exec -it</c> whose daemon-side
/// forkpty died with the previous process, and Docker has no re-attach for a running exec. The attach
/// then found no bound session, no pending bind, and fell into the P2-02 echo, which emits <b>nothing
/// at all</b> until the user types. The client's only "has the CLI come up?" signal is the first output
/// frame, so the coordinator surface sat on "Still starting the coordinator" for six hours over an agent
/// everything else on the daemon knew was running.</para>
///
/// <para>One notice frame is the whole difference between a screen that lies and one that can be
/// recovered. The echo stays for ids the daemon has never heard of — this is a statement about a session
/// we hold, not a catch-all.</para>
/// </summary>
public class TerminalDetachedAttachTests
{
    [Fact]
    public async Task Attach_ToAKnownAgentWithNoBoundTerminal_SaysSo_InsteadOfSilence()
    {
        using var fixture = new DaemonFixture();
        // A live session with no CLI bound to it — exactly what the reconciler adopts after a restart.
        fixture.Services.GetRequiredService<AgentSessionStore>()
            .Spawn(kind: "claude-code", role: "coordinator", agentId: "adopted-1", repoHash: "repo-1");

        var client = new TerminalService.TerminalServiceClient(fixture.CreateChannel());
        using var call = client.Attach(fixture.AuthHeaders());
        await call.RequestStream.WriteAsync(new TerminalInput { AgentId = "adopted-1" });

        // The frame arrives unprompted: the client never has to type to learn what happened, which is
        // the property that was missing — an attach that emits nothing reads as a CLI still starting up.
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var notice = call.ResponseStream.Current.Raw.ToStringUtf8();
        Assert.Equal(TerminalGrpcService.DetachedNotice, notice);
        Assert.Contains("No terminal is attached", notice, System.StringComparison.Ordinal);
        Assert.Contains("Restart", notice, System.StringComparison.Ordinal); // the recovery is named
    }

    /// <summary>Input to a detached session is DISCARDED, never echoed: a terminal that types back looks
    /// like it is talking to the CLI while reaching nothing at all.</summary>
    [Fact]
    public async Task Attach_ToADetachedAgent_DoesNotEchoInput()
    {
        using var fixture = new DaemonFixture();
        fixture.Services.GetRequiredService<AgentSessionStore>()
            .Spawn(kind: "claude-code", role: "coordinator", agentId: "adopted-2", repoHash: "repo-1");

        var client = new TerminalService.TerminalServiceClient(fixture.CreateChannel());
        using var call = client.Attach(fixture.AuthHeaders());
        await call.RequestStream.WriteAsync(new TerminalInput { AgentId = "adopted-2" });
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None)); // the notice

        await call.RequestStream.WriteAsync(new TerminalInput { Data = ByteString.CopyFromUtf8("hello\n") });
        await call.RequestStream.CompleteAsync();

        // Nothing more: the stream ends with the request stream rather than reflecting the keystrokes.
        Assert.False(await call.ResponseStream.MoveNext(CancellationToken.None));
    }

    /// <summary>The scoping half, and what keeps the test above from being a blanket behaviour change: an
    /// id the daemon holds no session for keeps the P2-02 echo. The notice is about a session we HAVE.</summary>
    [Fact]
    public async Task Attach_ToAnUnknownAgentId_StillEchoes()
    {
        using var fixture = new DaemonFixture();
        var client = new TerminalService.TerminalServiceClient(fixture.CreateChannel());
        using var call = client.Attach(fixture.AuthHeaders());

        await call.RequestStream.WriteAsync(new TerminalInput { AgentId = "never-heard-of-it" });
        await call.RequestStream.WriteAsync(new TerminalInput { Data = ByteString.CopyFromUtf8("echo hi\n") });

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal("echo hi\n", call.ResponseStream.Current.Raw.ToStringUtf8());

        await call.RequestStream.CompleteAsync();
    }
}

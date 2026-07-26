using Xunit;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// The decision table behind <see cref="SandboxFixture.TcpProbeAsync"/>, asserted without Docker.
///
/// <para>The live test stands up real peers for the cases it can arrange deterministically (a peer
/// that serves, a peer that RSTs, a closed port, an unroutable address). This covers the rest of the
/// table — most importantly <b>exit 28</b>, a connect that timed out because the SYN was DROPPED,
/// which needs a packet filter to produce and cannot be arranged from inside a jail with
/// <c>CapDrop ALL</c>. Leaving that branch to chance is how a classifier ends up with a case nobody
/// ever exercised.</para>
///
/// <para>The rule being pinned: <b>reachability is whether the TCP handshake completed</b>, not
/// whether the conversation afterwards went well. Everything that fails <i>after</i> a connection
/// exists — a reset, an empty reply, a protocol error — is a reachable peer.</para>
/// </summary>
public sealed class TcpProbeClassifierTests
{
    [Theory]
    // Counters present: they decide, whatever the exit code says.
    [InlineData("connects=1 tconnect=0.000531 code=403", 0, true)]    // served a reply
    [InlineData("connects=1 tconnect=0.000604 code=000", 56, true)]   // accepted, then RST — the CI case
    [InlineData("connects=1 tconnect=0.000210 code=000", 52, true)]   // accepted, then empty reply
    [InlineData("connects=0 tconnect=0.000000 code=000", 7, false)]   // refused / no route
    [InlineData("connects=0 tconnect=0.000000 code=000", 28, false)]  // SYN dropped, connect timed out
    // A connect that completed slowly (a dropped SYN the retransmit rescued) is still a completed
    // connect — the delay is the backstop test's business, not the classifier's.
    [InlineData("connects=1 tconnect=1.002318 code=403", 0, true)]
    public void CountersDecide(string writeOut, int exitCode, bool expected) =>
        Assert.Equal(expected, SandboxFixture.ConnectCompleted(writeOut, exitCode));

    [Theory]
    // No counters at all (curl died before emitting them) — fall back to the exit code. The three
    // connect-phase failures are unreachable; anything else happened after a connection existed.
    [InlineData("", 0, true)]
    [InlineData("", 56, true)]
    [InlineData("", 6, false)]    // could not resolve
    [InlineData("", 7, false)]    // could not connect
    [InlineData("", 28, false)]   // timed out
    [InlineData("garbage", 7, false)]
    public void ExitCodeDecides_WhenCurlEmittedNoCounters(string writeOut, int exitCode, bool expected) =>
        Assert.Equal(expected, SandboxFixture.ConnectCompleted(writeOut, exitCode));

    [Fact]
    public void APartialWriteOutIsStillRead()
    {
        // curl writes the -w string as one unit, but a truncated exec stream could deliver only part
        // of it. A single readable counter is enough to decide, and must not be ignored in favour of
        // the exit-code fallback.
        Assert.True(SandboxFixture.ConnectCompleted("connects=1", 56));
        Assert.False(SandboxFixture.ConnectCompleted("connects=0", 7));
        Assert.True(SandboxFixture.ConnectCompleted("tconnect=0.4", 56));
    }
}

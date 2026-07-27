using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Mainguard.Tests.TestTools;

namespace Mainguard.Tests;

/// <summary>
/// The helper's own tests — the deterministic half of a fix for a non-deterministic bug.
///
/// <para>PR #263 removed the racy <c>FreePort()</c> shape from <c>Mainguard.Server.Tests</c> and left
/// the two copies here alone, reasoning that these call sites want a DEAD port rather than one to bind,
/// so <c>AddressInUse</c> is not the hazard. That is true and it is not the whole picture: the inverse
/// hazard is real. Releasing the socket and returning the bare number makes "nothing is listening here"
/// a hope, and a foreign process taking the port turns a test that expects a connection to FAIL into
/// one that gets a connection.</para>
///
/// <para>A race cannot be failed on demand, so what is pinned here is the two mechanisms that close it,
/// each driven through its own seam so the branch under test is genuinely taken — the trap #263's own
/// concurrency test fell into, where the kernel simply never repeated and the assertion passed with the
/// fix disabled. Every negative claim here is paired with a control, because "it rejected the occupied
/// port" is otherwise trivially satisfiable by an allocator that returns nothing and a retry that never
/// stops retrying.</para>
/// </summary>
public sealed class DeadPortAllocationTests
{
    /// <summary>A raw ephemeral port, with none of the helper's checking — the "fresh candidate" a seam needs.</summary>
    private static int RawEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    // -------------------------------------------------------------------------------------------
    // The probe. Everything below reads its verdict, so it is pinned in BOTH directions first: a
    // constant-false IsListening would make every rejection test below pass for the wrong reason.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void IsListening_IsTrue_WhileBound_AndFalse_OnceReleased()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            Assert.True(TestPorts.IsListening(port), "a bound, listening loopback port must read as occupied");
        }
        finally
        {
            listener.Stop();
        }

        // The same port, same process, nothing changed but the listener going away.
        Assert.False(TestPorts.IsListening(port), "a released loopback port must read as dead");
    }

    // -------------------------------------------------------------------------------------------
    // Mechanism 1 — deadness is CHECKED, not assumed.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void LeaseDeadPort_ReturnsARealPort_WithNothingListeningOnIt()
    {
        var port = TestPorts.LeaseDeadPort();

        // Non-vacuity first: a helper that returned 0 (or never returned) would otherwise satisfy
        // "nothing is listening on it" trivially.
        Assert.InRange(port, 1, 65535);
        Assert.False(TestPorts.IsListening(port));
    }

    /// <summary>
    /// The deterministic proof. The candidate source hands the loop a port that is genuinely OCCUPIED —
    /// exactly what a foreign process on the runner does to us — and the helper must reject it and ask
    /// for another. Waiting for a real steal would be waiting for the race, which is the thing that
    /// cannot be scheduled.
    /// </summary>
    [Fact]
    public void LeaseDeadPort_RejectsAnOccupiedCandidate_AndAsksForAnother()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        try
        {
            var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            var offered = 0;

            var port = TestPorts.LeaseDeadPortFrom(() =>
            {
                offered++;
                return offered == 1 ? occupiedPort : RawEphemeralPort();
            });

            // It really did reject the first candidate rather than accept it — with the IsListening
            // check removed this is 1, and `port` is `occupiedPort`.
            Assert.Equal(2, offered);
            Assert.NotEqual(occupiedPort, port);

            // ...and what came back is a real, genuinely dead port, so "rejected the occupied one"
            // cannot be satisfied by returning nothing.
            Assert.InRange(port, 1, 65535);
            Assert.False(TestPorts.IsListening(port));
        }
        finally
        {
            occupied.Stop();
        }
    }

    /// <summary>
    /// The search is BOUNDED. A candidate source that never yields a free port must fail loudly rather
    /// than spin until the CI job times out.
    /// </summary>
    [Fact]
    public void LeaseDeadPort_GivesUp_WhenEveryCandidateIsOccupied()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        try
        {
            var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            var offered = 0;

            var error = Assert.Throws<InvalidOperationException>(
                () => TestPorts.LeaseDeadPortFrom(() =>
                {
                    offered++;
                    return occupiedPort;
                }));

            Assert.Equal(TestPorts.MaxProbes, offered);
            Assert.Contains("could not find an unoccupied loopback port", error.Message);
        }
        finally
        {
            occupied.Stop();
        }
    }

    // -------------------------------------------------------------------------------------------
    // Mechanism 2 — the residue the check cannot remove is TOLERATED, and only that.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The retry proof. The body's port is taken out from under it — the inverse race, simulated by
    /// binding it inside the body — and the run must be repeated on a different dead port rather than
    /// reported as a failure.
    /// </summary>
    [Fact]
    public async Task OnDeadPortAsync_RetriesOnAFreshPort_WhenThePortIsTakenMidRun()
    {
        var attempts = 0;
        var ports = new List<int>();
        TcpListener? thief = null;

        try
        {
            await TestPorts.OnDeadPortAsync(port =>
            {
                attempts++;
                ports.Add(port);

                if (attempts == 1)
                {
                    // Exactly what a foreign process does to us, only on cue.
                    thief = new TcpListener(IPAddress.Loopback, port);
                    thief.Start();
                    throw new InvalidOperationException("the endpoint answered when it should have been dead");
                }

                return Task.CompletedTask;
            });
        }
        finally
        {
            thief?.Stop();
        }

        // It RETRIED — with the retry removed the first attempt's exception escapes and this test
        // fails on that exception, never reaching here.
        Assert.Equal(2, attempts);

        // ...and on a genuinely different port, so "retried" cannot be satisfied by re-running against
        // the port that had just been stolen.
        Assert.Equal(2, ports.Count);
        Assert.NotEqual(ports[0], ports[1]);
    }

    /// <summary>
    /// The control, and the more important half: the retry is NARROW. A body that fails while its port
    /// is STILL dead has found a real defect, and must surface on the first attempt. Without this, the
    /// tolerance above would quietly re-run — and eventually rethrow — every genuine regression, which
    /// is how a stabiliser becomes a blind spot.
    /// </summary>
    [Fact]
    public async Task OnDeadPortAsync_DoesNotRetry_AFailureOnAPortThatIsStillDead()
    {
        var attempts = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestPorts.OnDeadPortAsync(_ =>
            {
                attempts++;
                throw new InvalidOperationException("a real defect, not a stolen port");
            }));

        Assert.Equal(1, attempts);
        Assert.Contains("a real defect", error.Message);
    }
}

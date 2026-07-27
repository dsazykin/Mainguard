using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Mainguard.Server;
using Xunit;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// The fixtures' own tests — the deterministic half of a fix for a non-deterministic bug.
///
/// <para>The defect was a TOCTOU: four copies of <c>FreePort()</c> asked the OS for an ephemeral port,
/// released it, and handed the number to a caller that bound it later. The same <c>phase2</c> merge
/// commit produced a green run and an <c>AddressInUseException</c> run. A race cannot be failed on
/// demand, so what is pinned here is the two mechanisms that close it, each driven through its own
/// injectable seam so the branch under test is actually taken:</para>
/// <list type="number">
/// <item><see cref="TestPorts"/> never returns a port this process already issued.</item>
/// <item><see cref="TestDaemonHost"/> retries a real host start on a fresh port when the bind loses,
/// bounded, and does NOT retry anything that is not address-in-use.</item>
/// </list>
///
/// <para>Each assertion is written so it cannot pass for the wrong reason: the "no duplicates" tests
/// assert how many ports came back (an allocator that returned nothing would fail), and the retry test
/// asserts the attempt COUNT (a first-attempt success would fail), with a control proving an
/// uncontended start really does report one attempt.</para>
/// </summary>
public sealed class TestPortAllocationTests
{
    private static DaemonOptions Template() => new()
    {
        LocalDev = true,
        TokenPath = TestDaemonHost.TempTokenPath("mg-portalloc"),
    };

    private static int PortOf(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    // ---------------------------------------------------------------------------------------------
    // Mechanism 1 — the allocator.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The deterministic proof. The candidate source hands the loop a port THIS PROCESS ALREADY ISSUED —
    /// the exact collision two duplicated <c>FreePort()</c> helpers used to produce — and the allocator
    /// must reject it and ask for another. Waiting for the kernel to offer a duplicate on its own would
    /// be waiting for the race, which is the thing that cannot be scheduled.
    /// </summary>
    [Fact]
    public void Lease_RejectsAPortThisProcessAlreadyIssued()
    {
        var alreadyIssued = TestPorts.Lease();
        Assert.True(TestPorts.WasIssued(alreadyIssued));

        var candidatesOffered = 0;
        var port = TestPorts.LeaseFrom(() =>
        {
            candidatesOffered++;
            var listener = candidatesOffered == 1
                ? new TcpListener(IPAddress.Loopback, alreadyIssued) // the collision
                : new TcpListener(IPAddress.Loopback, 0);            // a genuinely fresh port
            listener.Start();
            return listener;
        });

        // It really did reject the first candidate rather than accepting it — with the duplicate check
        // removed this is 1, and `port` is `alreadyIssued`.
        Assert.Equal(2, candidatesOffered);
        Assert.NotEqual(alreadyIssued, port);

        // ...and it returned a real port, so "no duplicate" cannot be satisfied by returning nothing.
        Assert.InRange(port, 1, 65535);
        Assert.True(TestPorts.WasIssued(port));
    }

    /// <summary>
    /// The end-to-end shape of the same property, under the concurrency the xunit runner actually
    /// creates: parallel test classes leasing at once must never collide with each other.
    /// </summary>
    [Fact]
    public void Lease_ReturnsDistinctPorts_UnderConcurrentCallers()
    {
        const int Workers = 4;
        const int PerWorker = 12;
        var leased = new ConcurrentBag<int>();

        Parallel.For(0, Workers, _ =>
        {
            for (var i = 0; i < PerWorker; i++)
            {
                leased.Add(TestPorts.Lease());
            }
        });

        var ports = leased.ToArray();

        // Non-vacuity first: the allocator handed back every port asked for. Without this line an
        // allocator that returned nothing at all would trivially have "no duplicates".
        Assert.Equal(Workers * PerWorker, ports.Length);
        Assert.All(ports, p => Assert.InRange(p, 1, 65535));
        Assert.Equal(ports.Length, ports.Distinct().Count());
    }

    /// <summary>
    /// The blocker seam the "port already bound" test depends on: the port comes back ALREADY BOUND, so
    /// there is no window in which it could be stolen, and it is booked out like any other lease.
    /// </summary>
    [Fact]
    public void LeaseBoundListener_HoldsThePort_AndBooksItOut()
    {
        var blocker = TestPorts.LeaseBoundListener();
        try
        {
            var port = PortOf(blocker);
            Assert.InRange(port, 1, 65535);
            Assert.True(TestPorts.WasIssued(port));

            // It is genuinely occupied — this is what makes DaemonAuthTests.PortAlreadyBound meaningful.
            var second = new TcpListener(IPAddress.Loopback, port);
            Assert.Throws<SocketException>(() => second.Start());
        }
        finally
        {
            blocker.Stop();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Mechanism 2 — the retry, against a REAL Kestrel daemon (an in-proc TestServer binds nothing and
    // could not observe any of this).
    // ---------------------------------------------------------------------------------------------

    /// <summary>The control. An uncontended start reports ONE attempt — so the "2" below means something.</summary>
    [Fact]
    public async Task StartAsync_UncontendedStart_TakesOneAttempt()
    {
        await using var daemon = await TestDaemonHost.StartAsync(Template());

        Assert.Equal(1, daemon.StartAttempts);
        Assert.Contains(daemon.Urls, u => new Uri(u).Port == daemon.Port);
    }

    /// <summary>
    /// The retry proof. The first leased port is one that is already bound — exactly what a foreign
    /// process on the runner does to us — and the daemon must still come up, on a different port.
    /// </summary>
    [Fact]
    public async Task StartAsync_RetriesOnAFreshPort_WhenTheFirstOneIsAlreadyBound()
    {
        var blocker = TestPorts.LeaseBoundListener();
        try
        {
            var blockedPort = PortOf(blocker);
            var leases = 0;

            await using var daemon = await TestDaemonHost.StartAsync(
                Template(),
                leasePort: () => ++leases == 1 ? blockedPort : TestPorts.Lease());

            // It RETRIED: a first-attempt success would report 1 here, so this cannot pass by the
            // blocker having quietly failed to occupy the port.
            Assert.Equal(2, daemon.StartAttempts);
            Assert.Equal(2, leases);

            // And it is genuinely serving on the port it settled on, not the blocked one.
            Assert.NotEqual(blockedPort, daemon.Port);
            Assert.Contains(daemon.Urls, u => new Uri(u).Port == daemon.Port);
            Assert.DoesNotContain(daemon.Urls, u => new Uri(u).Port == blockedPort);
        }
        finally
        {
            blocker.Stop();
        }
    }

    /// <summary>
    /// The retry is BOUNDED. A permanently occupied port must fail loudly after
    /// <see cref="TestDaemonHost.MaxStartAttempts"/> tries rather than spin until the CI job times out.
    /// </summary>
    [Fact]
    public async Task StartAsync_GivesUp_WhenEveryAttemptLosesThePort()
    {
        var blocker = TestPorts.LeaseBoundListener();
        try
        {
            var blockedPort = PortOf(blocker);
            var attempts = 0;

            var ex = await Assert.ThrowsAsync<AggregateException>(() => TestDaemonHost.StartAsync(
                Template(),
                leasePort: () => { attempts++; return blockedPort; }));

            Assert.Equal(TestDaemonHost.MaxStartAttempts, attempts);
            Assert.Equal(TestDaemonHost.MaxStartAttempts, ex.InnerExceptions.Count);
            Assert.All(ex.InnerExceptions, inner => Assert.True(TestPorts.IsAddressInUse(inner)));
        }
        finally
        {
            blocker.Stop();
        }
    }

    /// <summary>
    /// The retry is NARROW. A startup failure that is not a lost port — here the gateway bind policy
    /// refusing a wildcard — must surface on the first attempt with its own message. Retrying it would
    /// turn <c>GatewayListenerBindTests</c>' "fails loudly" assertions into slow, confusing timeouts.
    /// </summary>
    [Fact]
    public async Task StartAsync_DoesNotRetry_AFailureThatIsNotAddressInUse()
    {
        var leases = 0;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => TestDaemonHost.StartAsync(
            Template() with { GatewayBindAddress = "0.0.0.0" },
            leasePort: () => { leases++; return TestPorts.Lease(); }));

        // Exactly one attempt: a gateway-enabled start leases two ports (control + gateway) per attempt.
        Assert.Equal(2, leases);
        Assert.IsNotType<AggregateException>(ex);
        Assert.False(TestPorts.IsAddressInUse(ex));
    }
}

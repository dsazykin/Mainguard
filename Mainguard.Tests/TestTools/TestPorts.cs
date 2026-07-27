using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Tests.TestTools;

/// <summary>
/// The one loopback-port helper for this assembly. Replaces the two hand-rolled <c>FreePort()</c>
/// copies in <c>DaemonStreamTests</c> and <c>DaemonAuthTests</c> — the same
/// <c>Start()</c>/read-the-number/<c>Stop()</c>/return shape that PR #263 removed from
/// <c>Mainguard.Server.Tests</c>. The two test projects cannot share code (this client-side tier must
/// not reference the server assembly — the same reason <c>RequiresLibvtermFact</c> and
/// <c>DaemonTransportMaterial</c> exist twice), so the helper is duplicated, not referenced.
///
/// <para><b>This is deliberately NOT a copy of <c>Mainguard.Server.Tests.Fixtures.TestPorts</c>.</b>
/// That one solves the problem its callers have: they BIND the port they were handed, so two callers
/// receiving the same number is an <c>AddressInUseException</c>, and it answers with in-process
/// exclusivity (a port is never re-issued, and rejected candidates are held OPEN while probing because
/// releasing one immediately is what makes the kernel offer it straight back) plus a bind retry.</para>
///
/// <para><b>Nothing in this assembly binds a leased port.</b> All four call sites want the opposite
/// property — a port where NOTHING is listening, so a connection attempt is refused — and two of them
/// never open a socket at all. In-process exclusivity buys those callers nothing: two tests that both
/// want a dead port can safely be handed the same dead port. So the mechanism is not dedup; it is the
/// inverse-direction hazard the #263 change did not cover, and which is why these two copies could not
/// simply be left alone:</para>
///
/// <para><b>The inverse race.</b> The old helper released the socket and returned the bare number, so
/// "nothing is listening here" was a hope, not a fact — a foreign process on the runner could take the
/// port between the lease and the assertion, and a test expecting a connection to FAIL would instead
/// get one. That is rarer than <c>AddressInUse</c> and it fails in the other direction, but it is not
/// an absence of a race. Two mechanisms close it, and neither is sufficient alone:</para>
/// <list type="number">
/// <item><see cref="LeaseDeadPort"/> VERIFIES the port is refusing connections before handing it over,
/// so deadness is checked rather than assumed.</item>
/// <item><see cref="OnDeadPortAsync"/> tolerates the residue the check cannot remove: if the body fails
/// AND the port has meanwhile been taken, the run is retried on a fresh dead port instead of being
/// reported as a defect. A body that fails while the port is STILL dead is never retried — masking that
/// would trade a rare wrong failure for a permanent blind spot.</item>
/// </list>
/// </summary>
internal static class TestPorts
{
    /// <summary>How many candidates to try before giving up on finding one nothing is listening on.</summary>
    internal const int MaxProbes = 32;

    /// <summary>How many times <see cref="OnDeadPortAsync"/> will re-run a body whose port was stolen.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>
    /// Loopback answers a connect attempt immediately in both directions — accepted if something is
    /// listening, refused if not — so this budget only ever expires on a pathologically wedged stack,
    /// and a candidate that expires it is discarded rather than trusted.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// A loopback port that was refusing connections a moment ago. The caller must still be prepared
    /// for a foreign process to take it — use <see cref="OnDeadPortAsync"/> for any test whose verdict
    /// depends on the port staying dead.
    /// </summary>
    public static int LeaseDeadPort() => LeaseDeadPortFrom(BindEphemeralThenRelease);

    /// <summary>
    /// The candidate-source seam. Only this helper's own tests pass <paramref name="candidates"/> — it
    /// is how they hand the loop a port that IS occupied and then observe that the loop rejects it and
    /// asks for another, which a live run cannot be made to do on demand.
    /// </summary>
    internal static int LeaseDeadPortFrom(Func<int> candidates)
    {
        for (var probe = 0; probe < MaxProbes; probe++)
        {
            var port = candidates();
            if (!IsListening(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException(
            $"TestPorts could not find an unoccupied loopback port in {MaxProbes} probes.");
    }

    /// <summary>
    /// Whether something is accepting TCP connections on this loopback port right now.
    ///
    /// <para>Only <see cref="SocketError.ConnectionRefused"/> — the kernel saying "nothing is bound
    /// here" — counts as dead. Every other outcome, including a connect that never answers, is treated
    /// as occupied: the cost of discarding a usable port is one more probe, while the cost of handing
    /// out a live one is the flake this exists to prevent.</para>
    /// </summary>
    public static bool IsListening(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (SocketException error)
        {
            return error.SocketErrorCode != SocketError.ConnectionRefused;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a port nothing is listening on, and — bounded by
    /// <see cref="MaxAttempts"/> — re-runs it on a fresh one if it failed BECAUSE the port stopped
    /// being dead underneath it.
    ///
    /// <para>The retry is narrow on purpose. A body that fails while its port is still refusing
    /// connections has found a real defect and is rethrown on the first attempt, so this cannot turn a
    /// genuine regression into a slow, confusing pass.</para>
    /// </summary>
    public static async Task OnDeadPortAsync(Func<int, Task> body, int maxAttempts = MaxAttempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = LeaseDeadPort();
            try
            {
                await body(port).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (attempt < maxAttempts && IsListening(port))
            {
                // A foreign process took the port between the lease and the assertion, so the premise
                // the body was handed stopped being true. Try again on a fresh dead port.
            }
        }
    }

    private static int BindEphemeralThenRelease()
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
}

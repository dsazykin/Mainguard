using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// The one loopback-port allocator for this assembly. Replaces the four hand-rolled
/// <c>FreePort()</c>/<c>GetFreePort()</c> copies (transport-security, auth, reconnect, gateway-bind) and
/// <see cref="FakeModelEndpoint"/>'s own — copies that were independent racers handing the SAME port to
/// two different hosts, which is how <c>phase2</c> produced a passing and a failing run off one merge
/// commit (<c>AddressInUseException: Failed to bind to address https://127.0.0.1:33631</c>).
///
/// <para><b>What this fixes, and what it deliberately does not.</b> Asking the OS for port 0 and then
/// releasing the socket is a TOCTOU by construction: the port is free between <c>Stop()</c> and the
/// daemon's own bind, and anything may take it. This type closes the half of that window it CAN close —
/// a port handed out once is never handed out again <i>in this process</i>, and rejected candidates are
/// held open while probing so the kernel is forced to pick a different one. It cannot close the other
/// half: another process on the runner may still grab the port. That residue is the reason
/// <see cref="TestDaemonHost"/> retries the bind on a fresh port; the two mechanisms are complementary
/// and neither is sufficient alone.</para>
///
/// <para>For tests that need a port to be <i>occupied</i> (the "port already bound → typed failure" case),
/// use <see cref="LeaseBoundListener"/>: it never releases the socket, so there is no window at all.</para>
/// </summary>
internal static class TestPorts
{
    /// <summary>How many candidate ports to probe before giving up on finding an unissued one.</summary>
    internal const int MaxProbes = 64;

    private static readonly object Gate = new();
    private static readonly HashSet<int> IssuedPorts = new();

    /// <summary>
    /// A loopback port this process has not issued before. The socket used to discover it is closed
    /// before returning, so the caller must be prepared for a foreign process to take it — start real
    /// hosts through <see cref="TestDaemonHost"/>, which retries.
    /// </summary>
    public static int Lease() => LeaseFrom(BindEphemeral);

    /// <summary>
    /// A started <see cref="TcpListener"/> holding a port this process has not issued before — for the
    /// tests that need the port to be busy. The socket is never released, so unlike <see cref="Lease"/>
    /// there is no window in which the port could be stolen. Caller stops it.
    /// </summary>
    public static TcpListener LeaseBoundListener() => LeaseListener(BindEphemeral);

    /// <summary>
    /// The candidate-source seam. Only the allocator's own tests pass <paramref name="candidates"/> — it
    /// is how they hand the loop a port this process has ALREADY issued and then observe that the loop
    /// rejects it and asks for another, which a live run cannot be made to do on demand.
    /// </summary>
    internal static int LeaseFrom(Func<TcpListener> candidates)
    {
        var listener = LeaseListener(candidates);
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static TcpListener BindEphemeral()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    /// <summary>Whether this port has already been handed out. Exists for the allocator's own tests.</summary>
    public static bool WasIssued(int port)
    {
        lock (Gate)
        {
            return IssuedPorts.Contains(port);
        }
    }

    /// <summary>
    /// Whether this failure (or anything it wraps) is the kernel refusing a bind because the address is
    /// already taken — the ONLY failure a listener start may be retried on. Kestrel surfaces it as an
    /// <see cref="AddressInUseException"/> wrapped in an <c>IOException</c>, which <c>DaemonHost</c> in
    /// turn wraps in a <c>DaemonStartupException</c>, so the whole chain has to be walked. Everything
    /// else — a bad gateway bind, a malformed address, a broken cert — must surface unchanged, or a
    /// retry loop would turn a real defect into a timeout.
    /// </summary>
    public static bool IsAddressInUse(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is Microsoft.AspNetCore.Connections.AddressInUseException)
            {
                return true;
            }

            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }

            // HttpListener (FakeModelEndpoint) reports the conflict as a raw error code: WSAEADDRINUSE
            // and EADDRINUSE for the managed Unix listener, ERROR_SHARING_VIOLATION /
            // ERROR_ALREADY_EXISTS for the Windows http.sys prefix registration.
            if (e is HttpListenerException { ErrorCode: 10048 or 98 or 32 or 183 })
            {
                return true;
            }
        }

        return false;
    }

    private static TcpListener LeaseListener(Func<TcpListener> candidates)
    {
        // Candidates the kernel offered that we had already issued. Held OPEN for the duration of the
        // loop: releasing one immediately is what makes the kernel offer it straight back, so the loop
        // would spin on the same number instead of moving on.
        var rejected = new List<TcpListener>();
        try
        {
            for (var probe = 0; probe < MaxProbes; probe++)
            {
                var listener = candidates();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;

                lock (Gate)
                {
                    if (IssuedPorts.Add(port))
                    {
                        return listener;
                    }
                }

                rejected.Add(listener);
            }

            throw new InvalidOperationException(
                $"TestPorts could not find an unissued loopback port in {MaxProbes} probes " +
                $"({IssuedCount()} already issued in this process).");
        }
        finally
        {
            foreach (var listener in rejected)
            {
                listener.Stop();
            }
        }
    }

    private static int IssuedCount()
    {
        lock (Gate)
        {
            return IssuedPorts.Count;
        }
    }
}

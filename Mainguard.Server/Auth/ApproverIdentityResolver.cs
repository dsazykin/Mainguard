using System;
using System.Runtime.InteropServices;
using Grpc.Core;

namespace Mainguard.Server.Auth;

/// <summary>
/// Resolves the approver identity <b>daemon-side</b> for a plan approval (OPS SA-1 / F2 — binding). The
/// identity is derived from the authenticated connection, NEVER from a client-supplied field — a client
/// cannot influence it (there is no such proto field). A client-set identity would let token-holding host
/// malware forge an attributable approval; deriving it here removes the trivial audit forgery.
///
/// <para><b>Honest residual (OPS §1.1) — what this identity is NOT.</b> The control plane is loopback
/// TCP, which carries <b>no peer credential</b>: there is no <c>SO_PEERCRED</c> to read on a TCP socket
/// (that is a Unix-domain-socket facility), so the daemon cannot observe who is on the other end. The
/// resolved value is the <b>daemon's own</b> OS identity — a constant, identical for every caller. It is
/// therefore an <i>attribution of the host session</i>, not a distinguishing identity: it records which
/// machine/user account the daemon runs as, and it can never tell two callers apart, nor distinguish the
/// human from host malware holding a valid token (a host-un-forgeable presence factor is deferred, OPS
/// §10.1). What it does close, and all it closes, is the trivial forgery of a <i>different</i> identity
/// asserted in the request. Making this identity discriminate between callers means moving the transport
/// to a Unix domain socket (or another peer-authenticated channel) — a trust-model decision, not a
/// refactor, so the docs must not imply it has already happened.</para>
/// </summary>
public interface IApproverIdentityResolver
{
    /// <summary>The daemon-derived approver identity for the connection behind <paramref name="context"/>.</summary>
    string Resolve(ServerCallContext context);
}

/// <summary>
/// The default host-identity resolver. It reports the identity of the process doing the resolving — the
/// <b>daemon's</b> effective uid on Linux (<c>uid:&lt;euid&gt;</c>), the daemon's OS user name elsewhere
/// (<c>os:&lt;name&gt;</c>). It ignores the request entirely: identity is never taken from the message
/// (SA-1/F2).
///
/// <para><b>It does not read the peer's credential, because loopback TCP does not carry one.</b> The name
/// is aspirational, not descriptive: <c>SO_PEERCRED</c> is a Unix-domain-socket facility, and the daemon
/// binds TCP. Under the host-trust boundary (loopback + same host + same OS user) the daemon's own
/// identity is the best available stand-in for the caller's, but it is a CONSTANT — every caller on the
/// box resolves to the same string, so this value can attribute an approval to the host session and
/// nothing finer. Do not read an approval record as proof of <i>which</i> local principal approved.
/// Changing that requires changing the transport, which is a deliberate trust-model decision (see the
/// interface docs); this class must not pretend it already has peer credentials.</para>
/// </summary>
public sealed class PeerCredentialIdentityResolver : IApproverIdentityResolver
{
    public string Resolve(ServerCallContext context)
    {
        // The request/message is deliberately never consulted (SA-1/F2). Only the daemon's own identity
        // is used — see the class docs for why the CONNECTION cannot supply one over loopback TCP.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                return $"uid:{geteuid()}"; // the DAEMON's euid, not the caller's
            }
            catch (Exception)
            {
                // Fall through to the OS user name if the libc call is unavailable.
            }
        }

        return $"os:{Environment.UserName}";
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}

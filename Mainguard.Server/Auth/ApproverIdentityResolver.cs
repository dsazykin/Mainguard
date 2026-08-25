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
/// <b>daemon's</b> OS user name, on every platform, as <c>os:&lt;name&gt;</c>. It ignores the request
/// entirely: identity is never taken from the message (SA-1/F2).
///
/// <para><b>Why one format everywhere.</b> Linux used to take a separate branch returning
/// <c>uid:&lt;euid&gt;</c> from a raw <c>geteuid()</c>. That numeric shape was a leftover of the original
/// (since-retracted) claim that this value came from <c>SO_PEERCRED</c> — a peer credential IS a number
/// (<c>struct ucred</c> has no name) — and MG-16 corrected only the documentation, leaving the format
/// behind it. Nothing needed it: on Windows/WSL2 the daemon runs inside the Linux VM under
/// <c>mainguardd.service</c> (<c>User=mainguard</c>, a real <c>/etc/passwd</c> entry created by the image
/// build), so the same field that reads <c>os:&lt;name&gt;</c> on a macOS host rendered a bare
/// <c>uid:1000</c> there — the identical daemon-session attribution, in a shape that says nothing to the
/// person reading their own audit trail. <c>Environment.UserName</c> on Unix resolves through the passwd
/// database (<c>getpwuid</c>), <b>not</b> <c>$USER</c>/<c>$LOGNAME</c> — verified: it ignores those
/// variables even when they are set to a different value — so unifying on it neither depends on the unit's
/// environment block nor introduces an env-spoofable identity.</para>
///
/// <para><b>The <c>uid:</c> last resort.</b> When the euid has no passwd entry at all (an unmapped uid in a
/// user namespace, say) <c>Environment.UserName</c> on Linux returns the empty string — verified — and a
/// bare <c>"os:"</c> is a useless actor to write into an audit record. Only in that case does this fall
/// back to <c>uid:&lt;euid&gt;</c>, which cannot fail. Both forms name the SAME thing (see below); the
/// fallback is about never recording a blank, not about a second kind of identity.</para>
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
        // On Unix this is a passwd lookup on the effective uid, NOT $USER/$LOGNAME, so it is not
        // spoofable through the daemon's environment.
        var name = Environment.UserName; // the DAEMON's user, not the caller's
        if (!string.IsNullOrWhiteSpace(name))
        {
            return $"os:{name}";
        }

        // Last resort only: no passwd entry for the euid, so .NET handed back "". A bare "os:" is a
        // useless actor in an audit record; the raw euid at least names the same account. Same identity,
        // uglier shape — never the normal path.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                return $"uid:{geteuid()}";
            }
            catch (Exception)
            {
                // libc unavailable — nothing better than the (blank) name is left.
            }
        }

        return $"os:{name}";
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}

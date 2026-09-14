using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Mainguard.Server.Runtime;

/// <summary>
/// The uid/gid (and, on Linux, the pid) of the process on the other end of a Unix-domain socket, read
/// from the kernel rather than claimed by the peer.
///
/// <para><b>F62.</b> The agent IPC endpoints accepted any connection that could reach the socket path and
/// treated it as the jail's — identity was purely positional ("only its jail has the mount"). That is
/// true of the jail, but it was not true of the HOST: the socket sat at mode 0666 under a directory tree
/// another local user could traverse, so a second account on the same Mac could connect to a
/// coordinator's endpoint and drive spawn/plan requests under the operator's keys. The path modes are
/// tightened separately; this is the evidence layer that lets the endpoint notice a peer that is not the
/// one it has been talking to.</para>
///
/// <para><b>Why the check is "same peer as last time" and not a uid allowlist.</b> There is no uid to
/// allowlist. The daemon runs as the operator; the agent runs as uid 1000 <i>inside</i> a container,
/// which the host sees through userns-remap as an arbitrary subuid, and on macOS through a file-sharing
/// layer in a different kernel entirely. Any hardcoded expectation would be wrong on at least one
/// supported substrate — and wrong in the direction that breaks a working agent. Pinning the first
/// observed peer needs no such knowledge and still refuses the second, different, local user.</para>
/// </summary>
public readonly record struct UnixPeerCredentials(uint Uid, uint Gid, int Pid)
{
    // Linux: SOL_SOCKET / SO_PEERCRED, returning `struct ucred { pid_t pid; uid_t uid; gid_t gid; }`.
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    /// <summary>
    /// Reads the connected peer's credentials, or null when the platform cannot supply them (Windows, or
    /// a socket that is not AF_UNIX). Never throws: a missing answer degrades to "unknown peer", which
    /// callers treat as "do not pin", not as "deny".
    /// </summary>
    public static UnixPeerCredentials? TryRead(Socket socket)
    {
        if (socket is null || socket.AddressFamily != AddressFamily.Unix)
        {
            return null;
        }

        try
        {
            if (OperatingSystem.IsLinux())
            {
                Span<byte> buffer = stackalloc byte[12];
                var read = socket.GetRawSocketOption(SolSocket, SoPeerCred, buffer);
                if (read < 12)
                {
                    return null;
                }

                var pid = BitConverter.ToInt32(buffer[..4]);
                var uid = BitConverter.ToUInt32(buffer[4..8]);
                var gid = BitConverter.ToUInt32(buffer[8..12]);
                return new UnixPeerCredentials(uid, gid, pid);
            }

            if (OperatingSystem.IsMacOS())
            {
                // getpeereid(2) is the portable BSD spelling; LOCAL_PEERCRED's xucred layout is not
                // stable enough across releases to parse by hand, and the pid is not available this way.
                if (getpeereid(socket.Handle, out var euid, out var egid) != 0)
                {
                    return null;
                }

                return new UnixPeerCredentials(euid, egid, Pid: -1);
            }
        }
        catch (Exception)
        {
            // A socket closed under us, or a kernel that refuses the option. The caller degrades.
        }

        return null;
    }

    /// <summary>How this peer reads in a log line or an audit entry.</summary>
    public override string ToString() =>
        Pid >= 0
            ? $"uid={Uid} gid={Gid} pid={Pid}"
            : $"uid={Uid} gid={Gid}";

    [DllImport("libc", SetLastError = true)]
    private static extern int getpeereid(IntPtr socket, out uint euid, out uint egid);
}

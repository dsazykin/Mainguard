using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Bootstrap;

namespace Mainguard.Agents.Daemon;

/// <summary>
/// Resolves the daemon session token ACROSS the host/VM boundary (audit fix). The daemon writes its
/// token where <em>it</em> runs — <c>~/.mainguard/daemon.token</c>, which in the shipped topology is
/// <b>inside the MainguardEnv VM</b> — while the Windows client used to read only
/// <c>%LocalAppData%\Mainguard\daemon.token</c>, a file nothing writes on a real install, so every RPC
/// failed at token read and the control center could never authenticate.
///
/// <para>This locator owns the candidate set:</para>
/// <list type="number">
///   <item>The local per-user file (<see cref="DaemonPaths.TokenFilePath"/>) — a <c>--local-dev</c>
///   daemon on the same OS.</item>
///   <item>On Windows: the in-VM daemon's file over the 9P bridge,
///   <c>\\wsl.localhost\MainguardEnv\home\mainguard\.mainguard\daemon.token</c>. (Touching that path also
///   wakes the distro, which is desirable — systemd then brings <c>mainguardd</c> up.)</item>
/// </list>
///
/// <para>When several candidates exist (a dev machine running both topologies), the <b>freshest</b>
/// file wins: the daemon rotates its token on every start, so the most recently written token belongs
/// to the daemon that most recently claimed loopback :5250. The client re-reads per call, so a daemon
/// restart heals on the next RPC.</para>
/// </summary>
public static class DaemonTokenLocator
{
    /// <summary>The VM user whose home holds the daemon state (the tarball's default user).</summary>
    public const string VmUserName = "mainguard";

    /// <summary>The candidate token files for this OS, in declaration order (selection is by
    /// freshest write, not list order).</summary>
    public static IReadOnlyList<string> CandidatePaths()
    {
        var candidates = new List<string> { DaemonPaths.TokenFilePath() };
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(VmTokenUncPath());
        }

        return candidates;
    }

    /// <summary>The Windows-facing UNC path of the in-VM daemon's token file.</summary>
    public static string VmTokenUncPath(string distroName = WslCommands.DistroName, string vmUser = VmUserName)
        => $@"\\wsl.localhost\{distroName}\home\{vmUser}\.mainguard\daemon.token";

    /// <summary>
    /// The freshest candidate token file's path, or <c>null</c> when none exists / none is readable.
    /// This is the single selection point: everything session-scoped the client needs — the token AND
    /// the MG-19 mTLS material — must be read from the directory this returns, so a token can never be
    /// paired with another daemon's certificates.
    /// </summary>
    public static string? TryResolveTokenPath(IReadOnlyList<string>? candidates = null)
    {
        var best = (candidates ?? CandidatePaths())
            .Select(path =>
            {
                try
                {
                    var info = new FileInfo(path);
                    return info.Exists ? (Path: path, Stamp: info.LastWriteTimeUtc) : default;
                }
                catch
                {
                    return default; // an unreachable UNC candidate is simply not a candidate right now
                }
            })
            .Where(c => c.Path is not null)
            .OrderByDescending(c => c.Stamp)
            .FirstOrDefault();

        return best.Path;
    }

    /// <summary>
    /// The directory of the freshest candidate token file — the daemon's session directory, holding both
    /// <c>daemon.token</c> and the MG-19 transport credentials. Null when no candidate exists.
    /// </summary>
    public static string? TryResolveSessionDirectory(IReadOnlyList<string>? candidates = null)
    {
        var path = TryResolveTokenPath(candidates);
        return path is null ? null : Path.GetDirectoryName(path);
    }

    /// <summary>
    /// The session directory, or an actionable <see cref="InvalidOperationException"/> naming every path
    /// probed — the same failure shape <see cref="ReadToken"/> raises.
    /// </summary>
    public static string ResolveSessionDirectory(IReadOnlyList<string>? candidates = null)
    {
        var resolved = candidates ?? CandidatePaths();
        return TryResolveSessionDirectory(resolved) ?? throw new InvalidOperationException(
            "No Mainguard daemon session was found — the daemon has probably never started. "
            + $"Paths probed: {string.Join(", ", resolved)}. "
            + "Run Mainguard setup (or start mainguardd) and try again.");
    }

    /// <summary>
    /// Reads the current session token from the freshest existing candidate, or <c>null</c> when no
    /// candidate exists / none is readable. Never throws — a missing daemon is a state, not a fault.
    /// </summary>
    public static string? TryReadToken(IReadOnlyList<string>? candidates = null)
    {
        var path = TryResolveTokenPath(candidates);
        if (path is null)
        {
            return null;
        }

        try
        {
            var token = File.ReadAllText(path).Trim();
            return token.Length > 0 ? token : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the current session token, throwing an actionable <see cref="InvalidOperationException"/>
    /// (naming every path probed) when no candidate holds one — the error a failed RPC surfaces
    /// instead of a bare <c>FileNotFoundException</c>.
    /// </summary>
    public static string ReadToken(IReadOnlyList<string>? candidates = null)
    {
        var resolved = candidates ?? CandidatePaths();
        return TryReadToken(resolved) ?? throw new InvalidOperationException(
            "No Mainguard daemon session token was found — the daemon has probably never started. "
            + $"Paths probed: {string.Join(", ", resolved)}. "
            + "Run Mainguard setup (or start mainguardd) and try again.");
    }
}

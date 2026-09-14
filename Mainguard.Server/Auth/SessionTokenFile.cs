using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Mainguard.Server.Auth;

/// <summary>
/// Owns the daemon's per-session bearer token and its on-disk home. The token is
/// 256 bits from <see cref="RandomNumberGenerator"/>, written to a file readable
/// only by the current user:
/// <list type="bullet">
///   <item>Linux: <c>~/.mainguard/daemon.token</c>, mode <c>0600</c>.</item>
///   <item>Windows: <c>%LocalAppData%\Mainguard\daemon.token</c>, ACL restricted to the current user.</item>
/// </list>
/// Prints nothing (G-13): a client reads the token from this file, never from stdout.
/// </summary>
public sealed class SessionTokenFile
{
    /// <summary>The absolute path of the token file for this OS/user.</summary>
    public string Path { get; }

    /// <summary>The current session token (hex-encoded 256-bit value).</summary>
    public string Token { get; }

    private SessionTokenFile(string path, string token)
    {
        Path = path;
        Token = token;
    }

    /// <summary>The default per-user token path for the running OS (shared with the client).</summary>
    public static string DefaultPath() => Mainguard.Agents.Daemon.DaemonPaths.TokenFilePath();

    /// <summary>
    /// Generates a fresh 256-bit token <b>in memory only</b> — nothing is written to disk.
    ///
    /// <para><b>F55.</b> Minting and persisting are separate acts because a daemon that loses the port
    /// race must not have touched the surviving daemon's files. The old <see cref="Create"/> wrote the
    /// token during <c>ConfigureServices</c>, which runs long before Kestrel binds: a second instance
    /// started against the same data root rotated <c>daemon.token</c> (and the mTLS material beside it)
    /// and only then discovered the port was taken, leaving every live client authenticating with a
    /// token the running daemon had never heard of. <see cref="Persist"/> is now called from the host's
    /// <c>ApplicationStarted</c> hook, i.e. only after the port has actually been won.</para>
    /// </summary>
    public static SessionTokenFile Mint(string? path = null)
    {
        path ??= DefaultPath();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return new SessionTokenFile(System.IO.Path.GetFullPath(path), token);
    }

    /// <summary>
    /// Generates a fresh token and writes it user-only-readable to <paramref name="path"/> (or the OS
    /// default). Equivalent to <see cref="Mint"/> + <see cref="Persist"/>; kept for callers that are
    /// not behind a port race (tests, tooling).
    /// </summary>
    public static SessionTokenFile Create(string? path = null)
    {
        var file = Mint(path);
        file.Persist();
        return file;
    }

    /// <summary>
    /// Writes this token to <see cref="Path"/>, readable only by the current user. Idempotent.
    ///
    /// <para>On Unix the file is pre-created at <c>0600</c> so the bytes never land under a permissive
    /// mode, and the mode is re-asserted after the write (umask can widen it). On Windows the DACL is
    /// tightened <b>before</b> the token is written (F64): the previous order created the file under the
    /// inherited ACL, wrote the secret into it, and only then reduced the ACL — so the token existed on
    /// disk, readable by whoever the inherited ACEs named, for the duration of the write.</para>
    /// </summary>
    public void Persist()
    {
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Create empty, tighten, then write. The DACL is in place before the secret is.
            using (new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
            }

            RestrictWindows(Path);
            File.WriteAllText(Path, Token);
            return;
        }

        using (new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.WriteAllText(Path, Token);
        // Re-assert 0600 (WriteAllText above may have widened it via umask).
        File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        // The current user already owns a file it just created and an owner may always
        // rewrite the DACL by path — so we tighten it here (no SeRestorePrivilege, no
        // owner change needed). Disable inheritance, drop inherited ACEs, grant full
        // control to this user only.
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRule(rule);
        }

        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}

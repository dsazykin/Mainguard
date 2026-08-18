using System.Runtime.InteropServices;
using Xunit;

namespace Mainguard.Tests.TestTools;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only on Linux, skipping (with a reason, never failing)
/// elsewhere. The forkpty PTY probes are Linux-only by nature; the authoritative run is the
/// Docker/Linux CI leg (P2-03 test-platform reality). Skipping keeps the Windows self-verify green.
/// </summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    /// <param name="because">Why this one is Linux-only, when it is not the forkpty default — the skip
    /// reason a human reads in the Windows run has to name the actual dependency, or a permanently
    /// skipped test looks like a permanently passing one.</param>
    public LinuxOnlyFactAttribute(string? because = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Skip = because is null
                ? "Linux-only PTY test (forkpty). Runs in the Docker/Linux CI leg; skipped on this platform."
                : $"Linux-only: {because}. Runs in the Docker/Linux CI leg; skipped on this platform.";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that runs only on Windows (ConPTY path), skipping with a reason
/// elsewhere.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Windows-only PTY test (ConPTY). Skipped on this platform.";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for any-Unix behavior (forkpty, unix file modes, unix sockets):
/// runs on Linux and macOS, skips (with a reason, never failing) on Windows. Use
/// <see cref="LinuxOnlyFactAttribute"/> only for genuinely Linux-bound dependencies (cgroups,
/// /proc, the in-VM daemon) — the macos-host substrate runs the daemon on macOS, so "not
/// Windows" no longer implies Linux.
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute(string? because = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = because is null
                ? "Unix-only test. Skipped on Windows."
                : $"Unix-only: {because}. Skipped on Windows.";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that runs only on macOS (macos-host substrate specifics),
/// skipping with a reason elsewhere.
/// </summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute(string? because = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Skip = because is null
                ? "macOS-only test (macos-host substrate). Skipped on this platform."
                : $"macOS-only: {because}. Skipped on this platform.";
        }
    }
}

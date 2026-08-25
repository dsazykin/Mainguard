using System.Runtime.InteropServices;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>Runs only on Linux (forkpty), skipping with a reason elsewhere.</summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Skip = "Linux-only PTY test (forkpty). Runs in the Docker/Linux CI leg; skipped on this platform.";
        }
    }
}

/// <summary>Runs only on Windows (ConPTY), skipping with a reason elsewhere.</summary>
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

/// <summary>Runs on Linux and macOS (any-Unix behavior: forkpty, unix file modes), skipping on
/// Windows with a reason. Prefer this over <see cref="LinuxOnlyFactAttribute"/> unless the
/// dependency is genuinely Linux-bound (cgroups, /proc, the in-VM daemon).</summary>
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

/// <summary>Runs only on macOS (macos-host substrate specifics), skipping with a reason elsewhere.</summary>
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

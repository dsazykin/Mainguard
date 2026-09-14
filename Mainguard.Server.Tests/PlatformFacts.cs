using System;
using System.Diagnostics;
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

/// <summary>
/// Runs only on Linux <b>and</b> only where <c>python3</c> can actually be launched — the shim tests
/// that execute the real script the daemon wrote.
///
/// <para>A composite gate, in the same shape as <c>RequiresDockerAndOptInFactAttribute</c>, because
/// xunit honours exactly one <see cref="FactAttribute"/> per method and both halves of the condition
/// are real. The python half used to be an inline <c>if (!IsOnPath("python3")) return;</c>, which
/// reports <b>Passed</b>: a permanently-green test that measured nothing. The condition is unchanged;
/// only its expression moves, so the outcome now reads as Skipped with the reason attached.</para>
/// </summary>
public sealed class LinuxOnlyRequiresPython3FactAttribute : FactAttribute
{
    public LinuxOnlyRequiresPython3FactAttribute(string? because = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Skip = "Linux-only test (the in-VM daemon path). Skipped on this platform.";
        }
        else if (!Python3Availability.IsAvailable)
        {
            Skip = because is null
                ? "python3 is not launchable on this box — the real shim cannot be run here. (The pre-baked jail toolchain has it; a bare CI box may not.)"
                : $"python3 is not launchable on this box: {because}.";
        }
    }
}

/// <summary>
/// Probes once (and caches) whether <c>python3</c> can actually be STARTED. The guard this replaces
/// scanned <c>PATH</c> for a file named <c>python3</c>; launching it is the same condition expressed
/// more strictly (a PATH entry that exists but is not executable would have passed the scan and then
/// failed the test).
/// </summary>
internal static class Python3Availability
{
    private static readonly Lazy<bool> _probe = new(Probe);

    public static bool IsAvailable => _probe.Value;

    private static bool Probe()
    {
        try
        {
            var start = new ProcessStartInfo("python3")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("--version");

            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // python3 is not installed here
        }
    }
}

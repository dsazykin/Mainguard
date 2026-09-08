using System;
using System.Diagnostics;
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

/// <summary>
/// A <see cref="FactAttribute"/> for tests that need REAL network egress (P2-15: the RFC 3161
/// round-trip against a live TSA). Opt-in via <c>MAINGUARD_NETWORK_TESTS=1</c> — the nightly
/// network leg sets it; PR CI and local runs skip with the reason visible, so an offline machine
/// (or a TSA outage) can never fail a build the spec calls deterministic.
/// </summary>
public sealed class RequiresNetworkFactAttribute : FactAttribute
{
    public const string EnableVariable = "MAINGUARD_NETWORK_TESTS";

    public RequiresNetworkFactAttribute(string? because = null)
    {
        if (System.Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            Skip = because is null
                ? $"Network-gated test — set {EnableVariable}=1 to run (nightly leg)."
                : $"Network-gated: {because} — set {EnableVariable}=1 to run (nightly leg).";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for tests that drive the shim scripts through the REAL python
/// interpreter, skipping with a reason where there is none.
///
/// <para>These tests used to open with <c>if (no python3) return;</c> — which reports <b>Passed</b>.
/// A permanently-green test that measured nothing is worse than no test, because the green is read as
/// evidence. The condition is unchanged (can <c>python3</c> be launched at all?); only its expression
/// moves, from a silent early return to a skip xunit reports as Skipped with the reason attached.</para>
///
/// <para>Skipping is expressed by setting <see cref="FactAttribute.Skip"/> from the constructor, NOT by
/// throwing: this repo is on xunit 2.9.3 (v2 core), where <c>Assert.Skip</c> reports as a FAILURE.</para>
/// </summary>
public sealed class RequiresPython3FactAttribute : FactAttribute
{
    public RequiresPython3FactAttribute(string? because = null)
    {
        if (!Python3Availability.IsAvailable)
        {
            Skip = because is null
                ? "python3 is not launchable on this box — the shim scripts cannot be compiled or run here."
                : $"python3 is not launchable on this box: {because}.";
        }
    }
}

/// <summary><see cref="RequiresPython3FactAttribute"/> for a <see cref="TheoryAttribute"/>. Several of
/// the shim suites are data-driven, and a <c>[Theory]</c> cannot wear a <c>[Fact]</c>-derived
/// attribute — without this the conversion would have to turn a table of cases into one case, which is
/// exactly the coverage the table exists to give.</summary>
public sealed class RequiresPython3TheoryAttribute : TheoryAttribute
{
    public RequiresPython3TheoryAttribute(string? because = null)
    {
        if (!Python3Availability.IsAvailable)
        {
            Skip = because is null
                ? "python3 is not launchable on this box — the shim scripts cannot be compiled or run here."
                : $"python3 is not launchable on this box: {because}.";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for the shim tests that measure a command line THROUGH A REAL SHELL,
/// so they need <c>bash</c> as well as <c>python3</c>.
///
/// <para><b>The composite is the point.</b> The guard this replaces was commented "no python3/bash on
/// this box" and only ever caught a missing <i>bash</i>: with bash present and python3 absent,
/// <c>bash -c "python3 …"</c> exits 127 with empty stdout, which the helper maps to a real result
/// carrying <c>Refusal = "bash: python3: command not found"</c> — so the guard was skipped and the test
/// failed on an assertion that reads like a shim defect and is not one. Both halves are probed here,
/// up front, so the reason a run does not measure anything is stated instead of discovered.</para>
/// </summary>
public sealed class RequiresPython3AndBashFactAttribute : FactAttribute
{
    public RequiresPython3AndBashFactAttribute(string? because = null)
    {
        var missing = !Python3Availability.IsAvailable
            ? "python3"
            : !BashAvailability.IsAvailable ? "bash" : null;
        if (missing is not null)
        {
            Skip = because is null
                ? $"{missing} is not launchable on this box — a command line cannot be measured through a real shell here."
                : $"{missing} is not launchable on this box: {because}.";
        }
    }
}

/// <summary>
/// Probes once (and caches) whether <c>python3</c> can actually be STARTED. A PATH scan would report
/// a name that exists but is not executable as present; launching it is the condition the tests
/// really depend on, and is exactly what their old inline guards were catching (a null
/// <see cref="Process"/> or a <see cref="System.ComponentModel.Win32Exception"/> from
/// <see cref="Process.Start(ProcessStartInfo)"/>).
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

/// <summary>The same probe for <c>bash</c> — the second half of
/// <see cref="RequiresPython3AndBashFactAttribute"/>.</summary>
internal static class BashAvailability
{
    private static readonly Lazy<bool> _probe = new(Probe);

    public static bool IsAvailable => _probe.Value;

    private static bool Probe()
    {
        try
        {
            var start = new ProcessStartInfo("bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("exit 0");

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
            return false; // no bash here
        }
    }
}

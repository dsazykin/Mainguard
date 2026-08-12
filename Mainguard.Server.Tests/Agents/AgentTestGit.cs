using System;
using System.Diagnostics;
using System.IO;
using Mainguard.Agents.Agents;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// Test-only git CLI helper for the P2-06 integration tests (running commands inside agent
/// worktrees, scripting agent pushes, and driving the Windows-side fetch/merge round-trip).
/// Not a production runner — the daemon services under test route through the shared
/// <c>GitService.RunGit</c> primitive.
/// </summary>
internal static class AgentTestGit
{
    /// <summary>A disposable temp VM-root directory for a test (holds repos/ and worktrees/).</summary>
    internal static string NewVmRoot()
    {
        var path = Path.Combine(VmRootBase, "mainguard-vmroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Where VM roots go — the first candidate MEASURED to allow a script written there to be executed.
    ///
    /// <para><b>This used to be <see cref="Path.GetTempPath"/> unconditionally, and that is how the branch
    /// guard was measured by nothing for its whole existence.</b> These tests write a real
    /// <c>reference-transaction</c> hook into a real per-agent repository and then expect git to run it.
    /// git decides a hook exists with <c>access(path, X_OK)</c>, which returns <c>EACCES</c> on a mount
    /// flagged <c>noexec</c> whatever the mode bits say — git prints a hint and carries on. On a developer
    /// box and on a CI runner <c>/tmp</c> is exec-capable, so everything passed. Inside this product's own
    /// jail — where the whole suite runs under <c>.mainguard/verify</c> — <c>/tmp</c> is a Docker tmpfs and
    /// Docker's default tmpfs flags are <c>nosuid,nodev,noexec</c>. There, one hook test failed and three
    /// more went VACUOUSLY green: they assert that ordinary git is NOT blocked, which an inert hook
    /// satisfies perfectly.</para>
    ///
    /// <para>The probe is the product's own <see cref="AgentBranchGuard.MeasureHookCanRun"/> rather than a
    /// second opinion, so the harness cannot come to a different conclusion about "executable" than the
    /// code under test does. The fallback is the test assembly's own directory: inside the jail that is
    /// under <c>/workspace</c>, the ext4 bind mount, which is the only writable exec-capable surface
    /// there — every other one is a tmpfs.</para>
    ///
    /// <para>Nothing changes on a host: <c>/tmp</c> is measured first and wins, so this is a no-op
    /// everywhere the suite already worked.</para>
    /// </summary>
    private static readonly Lazy<string> _vmRootBase = new(ResolveVmRootBase);

    internal static string VmRootBase => _vmRootBase.Value;

    private static string ResolveVmRootBase()
    {
        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("MAINGUARD_TEST_VM_ROOT"),
                     Path.GetTempPath(),
                     Path.Combine(AppContext.BaseDirectory, "vmroots"),
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !CanExecuteScriptsIn(candidate))
            {
                continue;
            }

            return candidate;
        }

        // Every candidate refused. Return temp anyway rather than throwing from a static initialiser —
        // the many tests here that never touch a hook are unaffected by the mount, and the ones that DO
        // assert the guard is armed as an explicit precondition, so they fail with the real reason
        // instead of passing vacuously.
        return Path.GetTempPath();
    }

    private static bool CanExecuteScriptsIn(string directory)
    {
        var probeDir = Path.Combine(directory, "mainguard-execprobe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(probeDir);
            var probe = Path.Combine(probeDir, "probe");

            // '\n' explicitly, and the same shape as the real hook: a CRLF script is `bad interpreter`,
            // which is the other way a written-and-chmodded file fails to run.
            File.WriteAllText(probe, "#!/bin/sh\nexit 0\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    probe,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return AgentBranchGuard.MeasureHookCanRun(probe) is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            return false;
        }
        finally
        {
            DeleteTree(probeDir);
        }
    }

    internal static (int Code, string Out, string Err) Run(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>Runs git and throws if it fails — for test setup steps that must succeed.</summary>
    internal static string RunChecked(string workDir, params string[] args)
    {
        var (code, output, err) = Run(workDir, args);
        if (code != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({code}): {err}");
        }

        return output;
    }

    /// <summary>Sets a committer identity in a worktree so agent commits succeed.</summary>
    internal static void SetIdentity(string workDir)
    {
        RunChecked(workDir, "config", "user.name", "agent-a1");
        RunChecked(workDir, "config", "user.email", "agent@mainguard.local");
    }

    internal static void DeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Never fail a test from cleanup.
        }
    }
}

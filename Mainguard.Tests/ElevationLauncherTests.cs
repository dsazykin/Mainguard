using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-48 regression tests for <see cref="RunAsElevationLauncher"/>'s result-file hygiene. The launch
/// itself (ShellExecuteEx + UAC) is Windows-only and exercised by the human install matrix; what IS
/// portable — and was the bug — is the handling of the JSON result file across attempts: a stale
/// <c>elevated-result.json</c> from an earlier run must never be read back as the current attempt's
/// outcome (a launch that failed before the helper ever ran would otherwise "succeed" on stale data).
/// </summary>
public class ElevationLauncherTests
{
    [Fact]
    public async Task ConstructSandbox_DeletesStaleResultFile_BeforeAnyLaunch()
    {
        var dir = Directory.CreateTempSubdirectory("mainguard-elevation-test").FullName;
        try
        {
            var resultPath = Path.Combine(dir, "elevated-result.json");
            File.WriteAllText(resultPath, new ElevatedHelperResult
            {
                FeaturesEnabled = true,
                RebootRequired = false,
                ResumeTaskRegistered = false,
            }.Serialize());

            // A helper path that exists nowhere → the launch fails before any process starts…
            var missingHelper = Path.Combine(dir, "Mainguard.Installer.Elevated.exe");
            var launcher = new RunAsElevationLauncher(missingHelper, "resume.exe", resultPath);

            await Assert.ThrowsAsync<FileNotFoundException>(
                () => launcher.ConstructSandboxAsync(CancellationToken.None));

            // …and the stale result must already be gone, so no code path can mistake it for success.
            Assert.False(File.Exists(resultPath),
                "the stale elevated-result.json survived a failed launch attempt");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---- MG-9: nothing outside this installation is ever launched as administrator ---------------

    [Fact]
    public async Task ConstructSandbox_HelperOutsideTheInstallDirectory_IsRefused_BeforeAnyUacPrompt()
    {
        // The defect: File.Exists was the ONLY check between a path and `runas`. "It exists" says
        // nothing about what it is — a helper resolved from anywhere but our own install directory
        // would be launched as administrator on the user's consent to a prompt naming Mainguard.
        var dir = Directory.CreateTempSubdirectory("mainguard-elevation-test").FullName;
        try
        {
            // A helper that genuinely EXISTS, so only the location can be what refuses it.
            var strayHelper = Path.Combine(dir, "Mainguard.Installer.Elevated.exe");
            File.WriteAllText(strayHelper, "stub");
            var launcher = new RunAsElevationLauncher(
                strayHelper, "resume.exe", Path.Combine(dir, "elevated-result.json"));

            var ex = await Assert.ThrowsAsync<BootstrapException>(
                () => launcher.ConstructSandboxAsync(CancellationToken.None));

            Assert.Contains("elevated helper", ex.Message);
            Assert.Contains("not part of this installation", ex.Message);
            Assert.Contains("Nothing on your machine was changed", ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    // A relative name resolves against a working directory an attacker may control.
    [InlineData("resume.exe")]
    // …and a quote in the path would terminate the quoted --resume-target argument early and append
    // attacker-chosen trailing arguments to the ELEVATED command line.
    [InlineData("\" & calc.exe & \"")]
    public async Task ConstructSandbox_ResumeTargetOutsideTheInstallDirectory_IsRefused(string resumeTarget)
    {
        // The helper itself is valid here (co-located with the running test binary), so the resume
        // target is the only thing left that can refuse the launch.
        var baseDir = AppContext.BaseDirectory;
        var helper = Path.Combine(baseDir, "mainguard-pathtest-helper.exe");
        var resultPath = Path.Combine(Path.GetTempPath(), "mainguard-path-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(helper, "stub");
        try
        {
            var launcher = new RunAsElevationLauncher(helper, resumeTarget, resultPath);

            var ex = await Assert.ThrowsAsync<BootstrapException>(
                () => launcher.ConstructSandboxAsync(CancellationToken.None));

            Assert.Contains("resume target", ex.Message);
            Assert.Contains("not part of this installation", ex.Message);
        }
        finally
        {
            File.Delete(helper);
            try { File.Delete(resultPath); } catch { }
        }
    }

    [Fact]
    public async Task ConstructSandbox_MissingHelper_ReportsActionablePath()
    {
        var dir = Directory.CreateTempSubdirectory("mainguard-elevation-test").FullName;
        try
        {
            var missingHelper = Path.Combine(dir, "Mainguard.Installer.Elevated.exe");
            var launcher = new RunAsElevationLauncher(
                missingHelper, "resume.exe", Path.Combine(dir, "elevated-result.json"));

            var ex = await Assert.ThrowsAsync<FileNotFoundException>(
                () => launcher.ConstructSandboxAsync(CancellationToken.None));
            Assert.Contains(missingHelper, ex.Message);
            Assert.Contains("reinstall or rebuild", ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

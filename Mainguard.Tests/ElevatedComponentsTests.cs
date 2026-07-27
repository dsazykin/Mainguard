using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>MG-15 — the elevated pieces do not live where the user can rewrite them.</b>
///
/// <para>The finding: Mainguard installs per-user, so the directory the elevated helper and the
/// <c>/RL HIGHEST /SC ONLOGON</c> resume target are resolved from is writable by the very account that
/// task runs as — and an ONLOGON HIGHEST task fires <b>with no UAC prompt</b>. A single file write is a
/// full local privilege escalation. Neither of the earlier mitigations reaches it: path validation
/// (MG-9) stops an <i>arbitrary</i> exe, not a <i>replaced</i> one at the legitimate path, and ACLs stop
/// nothing at all because same-user malware carries the same token.</para>
///
/// <para><b>What is proven here vs on the Windows matrix.</b> These tests prove the POLICY: which
/// locations count as protected, that a no-prompt elevated run level is only ever granted to a target
/// in one, which copy of the helper a launch prefers, that a promote is monotonic, and that the
/// uninstall removes everything the install creates. The Windows facts they stand on —
/// <c>%ProgramFiles%</c> really is administrator-owned, <c>schtasks /RL</c> really does what its
/// documentation says — are the manual install matrix's job. The rules are written syntactically
/// precisely so the Windows cases run on the Linux CI leg.</para>
/// </summary>
public class ElevatedComponentsTests
{
    /// <summary>A synthetic Windows machine, as data. Note the deliberate trap in the user roots:
    /// <c>C:\Program Files\Redirected</c> is BOTH under a protected root and under a user root, which is
    /// how the deny-wins ordering gets exercised.</summary>
    private static ProtectedLocationPolicy WindowsLike() => ProtectedLocationPolicy.Create(
        protectedRoots: new[] { @"C:\Program Files", @"C:\Program Files (x86)", @"C:\Windows" },
        userWritableRoots: new[]
        {
            @"C:\Users\victim",
            @"C:\Users\victim\AppData\Local",
            @"C:\Program Files\Redirected",
        });

    // ---- the protected-location policy ------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Program Files\Mainguard\elevated")]
    [InlineData(@"C:\Program Files (x86)\Mainguard\elevated")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files")]                              // the root itself counts
    [InlineData(@"C:/Program Files/Mainguard/elevated")]           // forward slashes canonicalise
    public void AnAdministratorOwnedDirectoryIsProtected(string path)
        => Assert.True(WindowsLike().IsProtected(path));

    [Theory]
    // The actual MG-15 location: Mainguard's per-user Velopack install.
    [InlineData(@"C:\Users\victim\AppData\Local\Mainguard\current")]
    [InlineData(@"C:\Users\victim\Downloads")]
    // Deny wins over allow: nested under Program Files, but declared user-writable.
    [InlineData(@"C:\Program Files\Redirected\Mainguard")]
    // Not under any protected root at all.
    [InlineData(@"D:\apps\Mainguard")]
    // Near-miss on the name — a StartsWith check would call this protected.
    [InlineData(@"C:\Program Files Extra\Mainguard")]
    // Uncomputable answers must be the RESTRICTIVE ones, never the permissive one.
    [InlineData(@"C:\Program Files\Mainguard\..\..\Users\victim")]
    [InlineData(@"relative\path")]
    [InlineData(@"\\server\share\Mainguard")]
    [InlineData("")]
    public void EverythingElseIsNotProtected(string path)
        => Assert.False(WindowsLike().IsProtected(path));

    [Fact]
    public void AMachineWithNoAdministratorOwnedRoot_ProtectsNothing()
    {
        // Fail-closed: an empty allow-list must not degrade into "everything is fine".
        var nothing = ProtectedLocationPolicy.Create(Array.Empty<string>(), new[] { @"C:\Users\victim" });

        Assert.False(nothing.IsProtected(@"C:\Program Files\Mainguard"));
        Assert.Null(nothing.PreferredInstallRoot);
    }

    // ---- the run-level rule: THE MG-15 fix --------------------------------------------------------

    [Fact]
    public void ANoPromptElevatedTask_IsNeverRegisteredAgainstAUserWritableTarget()
    {
        // This is the finding, stated as a test. The target is exactly where Mainguard installs today.
        const string perUserApp = @"C:\Users\victim\AppData\Local\Mainguard\current\Mainguard.Pro.App.exe";

        var level = ResumeTaskPolicy.RunLevelFor(perUserApp, WindowsLike());

        Assert.Equal(ScheduledTaskRunLevel.Limited, level);
        Assert.Equal("LIMITED", ResumeTaskPolicy.SchtasksValue(level));
    }

    [Fact]
    public void TheRegistrationBuilderEmitsLimited_ForAPerUserTarget_AndNeverTheWordHighest()
    {
        const string root = @"C:\Users\victim\AppData\Local\Mainguard\current";
        var args = InstallerCommands.RegisterResumeTask(root + @"\Mainguard.Pro.App.exe", root, WindowsLike());

        // Derived in the BUILDER, so no caller can forget it and no caller can override it.
        Assert.Contains("LIMITED", args);
        Assert.DoesNotContain("HIGHEST", string.Join(" ", args), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ONLOGON", args);   // the resume still happens; only the privilege is gone
    }

    [Fact]
    public void AProtectedTargetStillEarnsHighest_SoAFutureRelocationNeedsNoCodeChange()
    {
        const string root = @"C:\Program Files\Mainguard";
        var args = InstallerCommands.RegisterResumeTask(root + @"\Mainguard.Pro.App.exe", root, WindowsLike());

        Assert.Contains("HIGHEST", args);
        Assert.Equal(
            ScheduledTaskRunLevel.Highest,
            ResumeTaskPolicy.RunLevelFor(root + @"\Mainguard.Pro.App.exe", WindowsLike()));
    }

    // ---- which helper a launch picks --------------------------------------------------------------

    [Fact]
    public void TheProtectedHelperWins_WhenItExistsAndIsCurrent()
    {
        var choice = ElevatedHelperResolution.Choose(
            protectedHelperPath: @"C:\Program Files\Mainguard\elevated\Mainguard.Installer.Elevated.exe",
            protectedHelperExists: true,
            protectedHelperIsCurrent: true,
            protectedElevatedDir: @"C:\Program Files\Mainguard\elevated",
            stagedHelperExists: true,
            stagedHelperPath: @"C:\app\elevated-stage\Mainguard.Installer.Elevated.exe",
            stageDir: @"C:\app\elevated-stage",
            fallbackHelperPath: @"C:\app\Mainguard.Installer.Elevated.exe",
            fallbackInstallRoot: @"C:\app");

        Assert.True(choice.RunsFromProtectedLocation);
        Assert.Equal(@"C:\Program Files\Mainguard\elevated", choice.InstallRoot);
    }

    [Fact]
    public void TheStageWins_OnlyWhenTheProtectedCopyIsStale()
    {
        // The update case. This launch is the UAC-prompted one, and its first act is to promote itself,
        // so the user-writable window closes again immediately.
        var choice = ElevatedHelperResolution.Choose(
            protectedHelperPath: @"C:\Program Files\Mainguard\elevated\Mainguard.Installer.Elevated.exe",
            protectedHelperExists: true,
            protectedHelperIsCurrent: false,
            protectedElevatedDir: @"C:\Program Files\Mainguard\elevated",
            stagedHelperExists: true,
            stagedHelperPath: @"C:\app\elevated-stage\Mainguard.Installer.Elevated.exe",
            stageDir: @"C:\app\elevated-stage",
            fallbackHelperPath: @"C:\app\Mainguard.Installer.Elevated.exe",
            fallbackInstallRoot: @"C:\app");

        Assert.False(choice.RunsFromProtectedLocation);
        Assert.Equal(@"C:\app\elevated-stage", choice.InstallRoot);
        Assert.Contains("newer", choice.Reason);
    }

    [Fact]
    public void ASourceBuildFallsBackToTheColocatedHelper_AndSaysSo()
    {
        var choice = ElevatedHelperResolution.Choose(
            protectedHelperPath: null, protectedHelperExists: false, protectedHelperIsCurrent: false,
            protectedElevatedDir: null,
            stagedHelperExists: false, stagedHelperPath: null, stageDir: null,
            fallbackHelperPath: @"C:\app\Mainguard.Installer.Elevated.exe",
            fallbackInstallRoot: @"C:\app");

        Assert.False(choice.RunsFromProtectedLocation);
        Assert.Equal(@"C:\app\Mainguard.Installer.Elevated.exe", choice.HelperPath);
        // The degradation is named, not implied — an unhardened machine must be legible in the log.
        Assert.Contains("this user can write", choice.Reason);
    }

    // ---- the promote decision is monotonic --------------------------------------------------------

    [Fact]
    public void NothingInstalledYet_Promotes()
    {
        var decision = ElevatedComponentPolicy.Decide(new ElevatedComponentIdentity("0.2.5", "aaa"), installed: null);
        Assert.Equal(ElevatedComponentPromotionKind.Promote, decision.Kind);
    }

    [Fact]
    public void AnOlderStage_IsRefusedAsADowngrade()
    {
        // The privileged binary must never move backwards: a rolled-back app would otherwise re-install
        // an older administrator-owned helper over a newer one, undoing every fix it carried.
        var decision = ElevatedComponentPolicy.Decide(
            new ElevatedComponentIdentity("0.2.4", "aaa"),
            new ElevatedComponentIdentity("0.2.5", "bbb"));

        Assert.Equal(ElevatedComponentPromotionKind.RefusedDowngrade, decision.Kind);
        Assert.False(decision.ShouldPromote);
        Assert.Contains("DOWNGRADE", decision.Reason);
    }

    [Fact]
    public void AnUnorderableVersion_IsRefused_NotGuessed()
    {
        var decision = ElevatedComponentPolicy.Decide(
            new ElevatedComponentIdentity("not-a-version", "aaa"),
            new ElevatedComponentIdentity("0.2.5", "bbb"));

        Assert.Equal(ElevatedComponentPromotionKind.RefusedUncomparable, decision.Kind);
        Assert.False(decision.ShouldPromote);
    }

    [Fact]
    public void SameVersionDifferentBytes_Promotes_ButSameBytesDoNot()
    {
        // Without the fingerprint tie-break a rebuilt helper at an unchanged version would never reach
        // the protected root, and the relocation would silently freeze at the first build installed.
        Assert.Equal(
            ElevatedComponentPromotionKind.Promote,
            ElevatedComponentPolicy.Decide(
                new ElevatedComponentIdentity("0.2.5", "aaa"),
                new ElevatedComponentIdentity("0.2.5", "bbb")).Kind);

        Assert.Equal(
            ElevatedComponentPromotionKind.AlreadyCurrent,
            ElevatedComponentPolicy.Decide(
                new ElevatedComponentIdentity("0.2.5", "aaa"),
                new ElevatedComponentIdentity("0.2.5", "AAA")).Kind);
    }

    // ---- install / update / uninstall mechanics (real IO, temp dirs — runs on any OS) --------------

    [Fact]
    public void Install_CopiesTheStage_ThenIsIdempotent_ThenTracksAnUpdate()
    {
        using var sandbox = new Sandbox();
        var plan = sandbox.Plan;

        sandbox.WriteStage(("Mainguard.Installer.Elevated.exe", "v1"), ("sub/lib.dll", "l1"));
        var first = ElevatedComponentInstaller.Install(plan, "0.2.5");

        Assert.True(first.Installed);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(plan.ElevatedDir, "Mainguard.Installer.Elevated.exe")));
        Assert.Equal("l1", File.ReadAllText(Path.Combine(plan.ElevatedDir, "sub", "lib.dll")));
        Assert.True(File.Exists(plan.MarkerPath));

        // Re-running the same stage must not rewrite the protected directory.
        var second = ElevatedComponentInstaller.Install(plan, "0.2.5");
        Assert.False(second.Installed);
        Assert.Equal(ElevatedComponentPromotionKind.AlreadyCurrent, second.Kind);

        // A newer stage replaces it — and REPLACES, so a file retired between builds does not linger as
        // an unowned administrator-owned binary.
        sandbox.WriteStage(("Mainguard.Installer.Elevated.exe", "v2"));
        var third = ElevatedComponentInstaller.Install(plan, "0.2.6");

        Assert.True(third.Installed);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(plan.ElevatedDir, "Mainguard.Installer.Elevated.exe")));
        Assert.False(File.Exists(Path.Combine(plan.ElevatedDir, "sub", "lib.dll")));
    }

    [Fact]
    public void Install_RefusesToDowngradeWhatIsAlreadyInTheProtectedRoot()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteStage(("Mainguard.Installer.Elevated.exe", "new"));
        ElevatedComponentInstaller.Install(sandbox.Plan, "0.2.6");

        sandbox.WriteStage(("Mainguard.Installer.Elevated.exe", "old"));
        var result = ElevatedComponentInstaller.Install(sandbox.Plan, "0.2.4");

        Assert.False(result.Installed);
        Assert.Equal(ElevatedComponentPromotionKind.RefusedDowngrade, result.Kind);
        // The bytes on disk are unchanged — a refusal must not half-apply.
        Assert.Equal("new", File.ReadAllText(
            Path.Combine(sandbox.Plan.ElevatedDir, "Mainguard.Installer.Elevated.exe")));
    }

    [Fact]
    public void ASourceBuildWithNoStage_InstallsNothing_AndIsNotAnError()
    {
        using var sandbox = new Sandbox();

        var result = ElevatedComponentInstaller.Install(sandbox.Plan, "0.2.5");

        Assert.False(result.Installed);
        Assert.Equal(ElevatedComponentPromotionKind.NoStage, result.Kind);
        Assert.False(Directory.Exists(sandbox.Plan.ElevatedDir));
    }

    [Fact]
    public void Uninstall_LeavesNothingBehind_InTheProtectedRoot()
    {
        // "A relocation that leaks files on uninstall is a bug." This is that bug, as a test.
        using var sandbox = new Sandbox();
        sandbox.WriteStage(("Mainguard.Installer.Elevated.exe", "v1"), ("sub/lib.dll", "l1"));
        ElevatedComponentInstaller.Install(sandbox.Plan, "0.2.5");
        Assert.True(Directory.Exists(sandbox.Plan.ProtectedRoot));

        var removal = ElevatedComponentInstaller.Remove(sandbox.Plan);

        Assert.True(removal.Clean);
        Assert.Empty(removal.Leftovers);
        Assert.False(Directory.Exists(sandbox.Plan.ProtectedRoot));
        Assert.False(File.Exists(sandbox.Plan.MarkerPath));
        // And the per-user stage is untouched — the uninstaller owns that half.
        Assert.True(Directory.Exists(sandbox.Plan.StageDir));
    }

    [Fact]
    public void EveryPathTheInstallCreates_IsCoveredByTheUninstallPlan()
    {
        // The structural guard on the test above: someone adding a directory to the install footprint
        // without adding it to the removal targets fails HERE, before it ships as a leak.
        var plan = ElevatedComponentPlan.For(@"C:\Program Files", @"C:\app");

        Assert.All(plan.InstallFootprint, created =>
            Assert.True(
                plan.RemovalTargets.Any(target => TrustedExecutablePath.IsWithinOrEqual(created, target)),
                $"'{created}' is created by the install but removed by nothing."));
    }

    [Fact]
    public void Uninstall_ReportsWhatItCouldNotRemove_RatherThanReturningQuietly()
    {
        // A leak the uninstaller cannot fix must still be SAID, or the user believes their machine is
        // clean while an administrator-owned Mainguard binary is still installed on it. The denial is
        // injected rather than provoked, because "make the OS refuse a delete" is not reproducible
        // across Windows, Linux CI and a root-in-a-container run — and a test that only sometimes
        // exercises its branch is not a test.
        using var sandbox = new Sandbox();
        sandbox.WriteStage(("Mainguard.Installer.Elevated.exe", "v1"));
        ElevatedComponentInstaller.Install(sandbox.Plan, "0.2.5");

        var blocked = ElevatedComponentInstaller.Remove(
            sandbox.Plan,
            remove: _ => throw new UnauthorizedAccessException("Access to the path is denied."));

        Assert.False(blocked.Clean);
        Assert.Contains(sandbox.Plan.ProtectedRoot, blocked.Leftovers);
        Assert.Contains("Access to the path is denied.", blocked.Message);
        Assert.Contains("administrator", blocked.Message, StringComparison.OrdinalIgnoreCase);
        // Still on disk, and now named in the report — which is the whole point.
        Assert.True(Directory.Exists(sandbox.Plan.ProtectedRoot));
    }

    [Fact]
    public void TheHostPlanRefusesAMachineWithNoAdministratorOwnedRoot()
    {
        // Never "install the elevated binary into a user directory anyway" — that is the bug, restated.
        var nothing = ProtectedLocationPolicy.Create(Array.Empty<string>(), new[] { @"C:\Users\victim" });

        Assert.False(ElevatedComponentPlan.TryForHost(nothing, @"C:\app", out _, out var refusal));
        Assert.Contains("no administrator-owned install root", refusal);
    }

    [Fact]
    public void TheHostPlanPutsTheComponentsUnderTheProtectedRoot()
    {
        Assert.True(ElevatedComponentPlan.TryForHost(WindowsLike(), @"C:\app", out var plan, out _));

        Assert.Equal(@"C:\Program Files\Mainguard\elevated", plan.ElevatedDir);
        Assert.Equal(@"C:\app\elevated-stage", plan.StageDir);
        Assert.True(WindowsLike().IsProtected(plan.ElevatedDir));
        Assert.False(WindowsLike().IsProtected(plan.StageDir));
    }

    // ---- the promote marker carries the app root, so the promoted helper still knows the install ----

    [Fact]
    public void ThePromoteRecordsTheAppRoot_SoTheRelocatedHelperCanStillBoundAResumeTarget()
    {
        // Once the helper lives in Program Files it is no longer beside the app, so the root it
        // validates --resume-target against has to come from somewhere. Taking it as an ARGUMENT would
        // let a caller declare C:\Windows\System32 to be "the install"; recording it at promote time
        // puts it in a file only an administrator can write.
        using var sandbox = new Sandbox();
        sandbox.WriteStage(("Mainguard.Installer.Elevated.exe", "v1"));

        ElevatedComponentInstaller.Install(sandbox.Plan, "0.2.5", appRoot: @"C:\app");

        var recorded = ElevatedComponentIdentity.TryParse(
            ElevatedComponentInstaller.TryReadMarker(sandbox.Plan.MarkerPath));
        Assert.Equal(@"C:\app", recorded!.AppRoot);
    }

    [Fact]
    public void AnUnreadableMarkerMeansNothingIsInstalled_WhichReinstalls_NeverKeepsAStaleBuild()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteStage(("Mainguard.Installer.Elevated.exe", "v1"));
        ElevatedComponentInstaller.Install(sandbox.Plan, "0.2.5");
        File.WriteAllText(sandbox.Plan.MarkerPath, "{ not json");

        var again = ElevatedComponentInstaller.Install(sandbox.Plan, "0.2.5");

        Assert.True(again.Installed);
    }

    /// <summary>A temp-directory stand-in for "Program Files plus a per-user install". The paths are
    /// real, the privilege is not — which is exactly the split between what CI can prove and what the
    /// Windows matrix has to.</summary>
    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "mainguard-elev-" + Guid.NewGuid().ToString("N"));
            var installBase = Path.Combine(Root, "ProgramFiles");
            var appDir = Path.Combine(Root, "app");
            Directory.CreateDirectory(installBase);
            Directory.CreateDirectory(appDir);
            Plan = ElevatedComponentPlan.For(installBase, appDir);
        }

        public string Root { get; }

        public ElevatedComponentPlan Plan { get; }

        public void WriteStage(params (string Relative, string Content)[] files)
        {
            if (Directory.Exists(Plan.StageDir))
                Directory.Delete(Plan.StageDir, recursive: true);
            Directory.CreateDirectory(Plan.StageDir);
            foreach (var (relative, content) in files)
            {
                var target = Path.Combine(Plan.StageDir, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, content);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp */ }
        }
    }
}

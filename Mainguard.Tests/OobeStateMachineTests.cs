using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

// TI-P2-21 #3/#4 / plan §6 #3/#4 + §4 edge rows: the OOBE state machine's reboot-resume matrix,
// idempotent double-resume, single-UAC guarantee, the never-RunOnce Scheduled-Task command builder,
// and oobe-state.json schema round-trip (unknown fields tolerated).
public class OobeStateMachineTests
{
    // ---- #3: resume matrix — interrupt at each state → resume completes remaining steps only ------

    [Theory]
    [InlineData(OobeStage.Diagnostics)]
    [InlineData(OobeStage.EnableFeatures)]
    [InlineData(OobeStage.RebootPending)]
    [InlineData(OobeStage.Resumed)]
    [InlineData(OobeStage.ImportVm)]
    public async Task Oobe_StateMachine_ResumeMatrix_RunsOnlyRemaining(OobeStage resumeFrom)
    {
        var store = new FakeStore { State = new OobeState { Stage = resumeFrom, FeaturesEnabled = resumeFrom >= OobeStage.RebootPending } };
        var h = new CountingHandlers();

        var result = await new OobeStateMachine(store).RunAsync(h.Handlers, CancellationToken.None);

        Assert.Equal(OobeRunOutcome.Completed, result.Outcome);
        Assert.Equal(OobeStage.Done, result.State.Stage);

        // Diagnostics only re-runs if we resumed at Diagnostics. Features only if at/ before EnableFeatures.
        Assert.Equal(resumeFrom == OobeStage.Diagnostics ? 1 : 0, h.DiagnosticsCount);
        Assert.Equal(resumeFrom <= OobeStage.EnableFeatures ? 1 : 0, h.EnableCount);
        // ImportVm runs for every resume point (it is always still ahead until Done).
        Assert.Equal(1, h.ImportCount);
    }

    [Fact]
    public async Task Oobe_FreshRun_HappyPath_HandsOffAtReboot_ThenResumesToDone()
    {
        var store = new FakeStore();
        var h = new CountingHandlers { RebootRequired = true };
        var machine = new OobeStateMachine(store);

        // First pass: diagnostics pass, features enabled, reboot pending → hands off.
        var first = await machine.RunAsync(h.Handlers, CancellationToken.None);
        Assert.Equal(OobeRunOutcome.AwaitingReboot, first.Outcome);
        Assert.Equal(OobeStage.RebootPending, store.State!.Stage);
        Assert.True(store.State.FeaturesEnabled);
        Assert.Equal(0, h.ImportCount); // no VM import before the reboot

        // Second pass (the elevated resume Scheduled Task re-invokes): resumes → import → done.
        var second = await machine.RunAsync(h.Handlers, CancellationToken.None);
        Assert.Equal(OobeRunOutcome.Completed, second.Outcome);
        Assert.Equal(OobeStage.Done, store.State.Stage);
        Assert.True(store.State.VmImported);

        // Single UAC: EnableFeatures (the elevated relaunch) ran exactly once across both passes.
        Assert.Equal(1, h.EnableCount);
    }

    [Fact]
    public async Task Oobe_FeaturesAlreadyEnabled_NoReboot_CollapsesStraightToImport()
    {
        // A machine that already has WSL2 on: the elevated helper reports RebootRequired=false, so the
        // OOBE must run diagnostics → enable (no-op) → import → done in a SINGLE pass, with no reboot
        // hand-off — the user is never asked to restart.
        var store = new FakeStore();
        var h = new CountingHandlers { RebootRequired = false };

        var result = await new OobeStateMachine(store).RunAsync(h.Handlers, CancellationToken.None);

        Assert.Equal(OobeRunOutcome.Completed, result.Outcome);
        Assert.Equal(OobeStage.Done, store.State!.Stage);
        Assert.True(store.State.VmImported);
        Assert.Equal(1, h.EnableCount);
        Assert.Equal(1, h.ImportCount);
    }

    // ---- §4 edge row: resume task runs twice → idempotent (no-ops completed steps) ----------------

    [Fact]
    public async Task Oobe_ResumeTaskRunsTwice_IsIdempotent()
    {
        var store = new FakeStore { State = new OobeState { Stage = OobeStage.Done, VmImported = true, FeaturesEnabled = true } };
        var h = new CountingHandlers();

        var result = await new OobeStateMachine(store).RunAsync(h.Handlers, CancellationToken.None);

        Assert.Equal(OobeRunOutcome.Completed, result.Outcome);
        Assert.Equal(0, h.DiagnosticsCount);
        Assert.Equal(0, h.EnableCount);
        Assert.Equal(0, h.ImportCount);
    }

    // ---- Stale "VM imported" flag: user unregistered MainguardEnv between runs → re-import -----------

    [Fact]
    public async Task Oobe_ResumeWithVmImported_ButDistroUnregistered_RewindsAndReimports()
    {
        // The reported bug: setup was Done, then the user ran `wsl --unregister MainguardEnv` (to take a
        // rebuilt payload) and relaunched. The persisted state still says Done/VmImported, but the VM is
        // gone — the machine must NOT report Completed and hand the wizard to the CLI picker; it must
        // rewind to ImportVm and re-provision.
        var store = new FakeStore { State = new OobeState { Stage = OobeStage.Done, VmImported = true, FeaturesEnabled = true, RebootCompleted = true } };
        var h = new CountingHandlers { VmRegistered = false };

        var result = await new OobeStateMachine(store).RunAsync(h.Handlers, CancellationToken.None);

        Assert.Equal(OobeRunOutcome.Completed, result.Outcome);
        Assert.Equal(OobeStage.Done, store.State!.Stage);
        Assert.True(store.State.VmImported);        // re-imported, so the flag is true again
        Assert.Equal(1, h.VmRegisteredProbeCount);  // the staleness was actually checked
        Assert.Equal(1, h.ImportCount);             // and the import genuinely re-ran
        // The banked feature-enablement/reboot progress is preserved — only the VM is redone.
        Assert.Equal(0, h.EnableCount);
        Assert.Equal(0, h.DiagnosticsCount);
    }

    [Fact]
    public async Task Oobe_ResumeWithVmImported_AndDistroStillRegistered_DoesNotReimport()
    {
        // The healthy resume: the VM is still there, so the probe confirms it and the machine no-ops to
        // Completed exactly as before — the probe must not cause a spurious re-import.
        var store = new FakeStore { State = new OobeState { Stage = OobeStage.Done, VmImported = true, FeaturesEnabled = true } };
        var h = new CountingHandlers { VmRegistered = true };

        var result = await new OobeStateMachine(store).RunAsync(h.Handlers, CancellationToken.None);

        Assert.Equal(OobeRunOutcome.Completed, result.Outcome);
        Assert.Equal(1, h.VmRegisteredProbeCount);
        Assert.Equal(0, h.ImportCount);
    }

    [Fact]
    public async Task Oobe_FreshRun_NeverProbesRegistration_BeforeAnyImport()
    {
        // On a fresh install nothing has been imported yet, so the staleness check must not fire (there is
        // no claim to invalidate, and the probe would be a wasted WSL call before the VM even exists).
        var store = new FakeStore();
        var h = new CountingHandlers { RebootRequired = false, VmRegistered = false };

        var result = await new OobeStateMachine(store).RunAsync(h.Handlers, CancellationToken.None);

        Assert.Equal(OobeRunOutcome.Completed, result.Outcome);
        Assert.Equal(0, h.VmRegisteredProbeCount);  // never probed — VmImported was false at load
        Assert.Equal(1, h.ImportCount);             // the fresh import still ran exactly once
    }

    // ---- §4 edge row: diagnostics fail → stop before any modification -----------------------------

    [Fact]
    public async Task Oobe_DiagnosticsFail_StopsBeforeEnablingAnything()
    {
        var store = new FakeStore();
        var h = new CountingHandlers { DiagnosticsPass = false };

        var result = await new OobeStateMachine(store).RunAsync(h.Handlers, CancellationToken.None);

        Assert.Equal(OobeRunOutcome.BlockedByDiagnostics, result.Outcome);
        Assert.Equal(0, h.EnableCount);      // no feature enablement
        Assert.Equal(0, h.ImportCount);      // no VM import
        // The stage never advanced past Diagnostics — nothing was persisted as enabled.
        Assert.True(store.State is null || store.State.Stage == OobeStage.Diagnostics);
    }

    // ---- #4: oobe-state.json schema round-trip; unknown fields tolerated --------------------------

    [Fact]
    public void Oobe_StateFile_SchemaRoundTrip_Stable()
    {
        var state = new OobeState
        {
            Stage = OobeStage.ImportVm,
            FeaturesEnabled = true,
            RebootCompleted = true,
            UpdatedUtc = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero),
        };

        var json = OobeStateJson.Serialize(state);
        var back = OobeStateJson.Deserialize(json);

        Assert.Equal(state.Stage, back.Stage);
        Assert.Equal(state.FeaturesEnabled, back.FeaturesEnabled);
        Assert.Equal(state.RebootCompleted, back.RebootCompleted);
        Assert.Equal(state.SchemaVersion, back.SchemaVersion);
    }

    [Fact]
    public void Oobe_StateFile_UnknownFieldsTolerated()
    {
        // A newer installer wrote a field this build doesn't know — must not crash, and is preserved.
        var json = """
        { "schemaVersion": 1, "stage": "Resumed", "featuresEnabled": true, "futureField": {"k":"v"} }
        """;

        var state = OobeStateJson.Deserialize(json);

        Assert.Equal(OobeStage.Resumed, state.Stage);
        Assert.True(state.FeaturesEnabled);
        Assert.NotNull(state.Extra);
        Assert.True(state.Extra!.ContainsKey("futureField"));
    }

    // ---- #4 / plan §7: resume task uses the elevated Scheduled Task — NEVER RunOnce ---------------

    /// <summary>A synthetic Windows machine: Program Files is administrator-owned, the user profile is
    /// not. Data rather than an environment lookup, which is what lets the Windows rules run on Linux CI.</summary>
    internal static ProtectedLocationPolicy WindowsLikeProtection() => ProtectedLocationPolicy.Create(
        new[] { @"C:\Program Files", @"C:\Windows" },
        new[] { @"C:\Users\victim", @"C:\Users\victim\AppData\Local" });

    [Fact]
    public void Oobe_ResumeTask_UsesScheduledTask_NeverRunOnce()
    {
        var args = InstallerCommands.RegisterResumeTask(
            @"C:\Program Files\Mainguard\Mainguard.Installer.exe", @"C:\Program Files\Mainguard",
            WindowsLikeProtection());
        var joined = string.Join(" ", args);

        Assert.DoesNotContain("RunOnce", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/Create", args);
        Assert.Contains("ONLOGON", args);                       // survives the reboot
        // HIGHEST is granted here — and ONLY here — because this target sits under an administrator-owned
        // root, so its bytes cannot be replaced without already having administrator rights (MG-15).
        Assert.Contains("HIGHEST", args);
        Assert.Contains(InstallerCommands.ResumeTaskName, args);

        // Self-deleting: the unregister builder targets exactly the same task.
        var del = InstallerCommands.UnregisterResumeTask();
        Assert.Contains("/Delete", del);
        Assert.Contains(InstallerCommands.ResumeTaskName, del);
    }

    // ---- MG-9: the ONLOGON/HIGHEST registration refuses to name an arbitrary executable ----------

    [Theory]
    // The registration this builds runs /RL HIGHEST at ONLOGON — elevated, at every logon, with NO UAC
    // prompt. It used to interpolate whatever string it was handed, so anything that could reach the
    // elevated helper could have Windows run any executable as administrator, forever.
    [InlineData(@"C:\Users\victim\Downloads\evil.exe")]      // an arbitrary exe elsewhere on disk
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\attacker\share\evil.exe")]               // off-machine
    [InlineData(@"C:\Program Files\Mainguard\..\..\Windows\System32\cmd.exe")] // traversal out
    [InlineData("evil.exe")]                                  // relative → resolved against a CWD
    [InlineData("C:\\Program Files\\Mainguard\\x.exe\" & calc.exe & \"")] // /TR quoting break-out
    public void Oobe_ResumeTask_RefusesAnyTargetOutsideTheInstall(string target)
    {
        Assert.Throws<ArgumentException>(
            () => InstallerCommands.RegisterResumeTask(
                target, @"C:\Program Files\Mainguard", WindowsLikeProtection()));
    }

    [Fact]
    public void Oobe_ResumeTask_RegistersTheCanonicalFormOfALegitimateTarget()
    {
        var args = InstallerCommands.RegisterResumeTask(
            "C:/Program Files/Mainguard/Mainguard.Pro.App.exe", @"C:\Program Files\Mainguard",
            WindowsLikeProtection());

        var tr = args.SkipWhile(a => a != "/TR").Skip(1).First();
        Assert.Equal("\"C:\\Program Files\\Mainguard\\Mainguard.Pro.App.exe\" --resume", tr);
        // Exactly two quotes: the ones we put there. A validated path cannot contribute a third.
        Assert.Equal(2, tr.Split('"').Length - 1);
    }

    // ---- Invariant 4: elevated helper scope is exactly the two enumerated actions ----------------

    [Fact]
    public void Oobe_ElevatedHelper_ScopeIsExactlyTheEnumeratedActions()
    {
        // MG-15 added the third entry — relocating the elevated components into an administrator-owned
        // root needs one privileged write, and only the already-elevated component can make it. Its
        // safety argument is that it takes NO source or destination from anyone (see
        // InstallerCommands.PrivilegedActionCatalog); the scope is still an enumerated, closed list.
        var catalog = InstallerCommands.PrivilegedActionCatalog();
        Assert.Equal(3, catalog.Count);
        Assert.Contains("enable-windows-optional-features", catalog);
        Assert.Contains("register-resume-scheduled-task", catalog);
        Assert.Contains("install-elevated-components-to-protected-root", catalog);
    }

    [Fact]
    public void Oobe_EnableFeaturesPowerShell_SurfacesBothFeatures_NoRestart()
    {
        var ps = InstallerCommands.EnableFeaturesPowerShell();
        Assert.Contains("Enable-WindowsOptionalFeature", ps);
        Assert.Contains("Microsoft-Windows-Subsystem-Linux", ps);
        Assert.Contains("VirtualMachinePlatform", ps);
        Assert.Contains("-NoRestart", ps); // the OOBE, not DISM, owns the reboot decision
        // The script reads each call's authoritative RestartNeeded and surfaces it on the marker line so
        // the helper never assumes a cold machine (an already-enabled machine reports False → no reboot).
        Assert.Contains("RestartNeeded", ps);
        Assert.Contains(InstallerCommands.RestartNeededMarker, ps);
    }

    [Fact]
    public void Oobe_ElevatedHelperResult_RoundTrips()
    {
        var result = new ElevatedHelperResult { FeaturesEnabled = true, RebootRequired = true, ResumeTaskRegistered = true };
        var back = ElevatedHelperResult.Deserialize(result.Serialize());
        Assert.True(back.FeaturesEnabled);
        Assert.True(back.RebootRequired);
        Assert.True(back.ResumeTaskRegistered);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private sealed class FakeStore : IOobeStateStore
    {
        public OobeState? State { get; set; }
        public OobeState? Load() => State;
        public void Save(OobeState state) => State = state;
        public void Clear() => State = null;
    }

    private sealed class CountingHandlers
    {
        public bool DiagnosticsPass { get; set; } = true;
        public bool RebootRequired { get; set; }
        public int DiagnosticsCount { get; private set; }
        public int EnableCount { get; private set; }
        public int ImportCount { get; private set; }

        /// <summary>When set, a VM-registration probe is wired; its result is what it returns. Null (the
        /// default) wires NO probe — exactly the legacy handler shape the other tests rely on.</summary>
        public bool? VmRegistered { get; set; }
        public int VmRegisteredProbeCount { get; private set; }

        /// <summary>When set, a reboot-evidence probe is wired (audit fix #4); its result is what the
        /// probe reports. Null (the default) wires NO probe — the legacy advance-on-entry behaviour.</summary>
        public bool? Rebooted { get; set; }
        public DateTimeOffset? RebootProbeSawStamp { get; private set; }

        public OobeStageHandlers Handlers => new(
            RunDiagnostics: _ => { DiagnosticsCount++; return Task.FromResult(DiagnosticsPass); },
            EnableFeatures: _ => { EnableCount++; return Task.FromResult(new FeatureEnableResult(true, RebootRequired)); },
            ImportVm: _ => { ImportCount++; return Task.CompletedTask; },
            VmIsRegistered: VmRegistered is { } reg
                ? _ => { VmRegisteredProbeCount++; return Task.FromResult(reg); }
        : null,
            RebootHasCompleted: Rebooted is { } rebooted
                ? (stamp, _) => { RebootProbeSawStamp = stamp; return Task.FromResult(rebooted); }
        : null);
    }

    // ---- Audit fix #4: RebootPending must not advance without evidence of an actual reboot --------

    [Fact]
    public async Task Oobe_RelaunchBeforeReboot_StaysAwaitingReboot_AndImportsNothing()
    {
        // The user closed the wizard at the restart prompt and reopened Mainguard WITHOUT rebooting:
        // the machine must re-show the restart hand-off, never import onto half-enabled features.
        var stamped = DateTimeOffset.UtcNow.AddMinutes(-5);
        var store = new FakeStore
        {
            State = new OobeState { Stage = OobeStage.RebootPending, FeaturesEnabled = true, UpdatedUtc = stamped },
        };
        var h = new CountingHandlers { Rebooted = false };

        var result = await new OobeStateMachine(store).RunAsync(h.Handlers, CancellationToken.None);

        Assert.Equal(OobeRunOutcome.AwaitingReboot, result.Outcome);
        Assert.Equal(OobeStage.RebootPending, store.State!.Stage); // state NOT advanced
        Assert.Equal(0, h.ImportCount);
        Assert.Equal(stamped, h.RebootProbeSawStamp); // the probe judges against the pending stamp
    }

    [Fact]
    public async Task Oobe_ResumeAfterRealReboot_ProceedsToDone()
    {
        var store = new FakeStore
        {
            State = new OobeState { Stage = OobeStage.RebootPending, FeaturesEnabled = true },
        };
        var h = new CountingHandlers { Rebooted = true };

        var result = await new OobeStateMachine(store).RunAsync(h.Handlers, CancellationToken.None);

        Assert.Equal(OobeRunOutcome.Completed, result.Outcome);
        Assert.Equal(OobeStage.Done, store.State!.Stage);
        Assert.True(store.State.RebootCompleted);
        Assert.Equal(1, h.ImportCount);
    }

    [Fact]
    public async Task Oobe_NoRebootProbeWired_KeepsLegacyAdvanceOnEntry()
    {
        var store = new FakeStore
        {
            State = new OobeState { Stage = OobeStage.RebootPending, FeaturesEnabled = true },
        };
        var h = new CountingHandlers(); // Rebooted = null → no probe

        var result = await new OobeStateMachine(store).RunAsync(h.Handlers, CancellationToken.None);

        Assert.Equal(OobeRunOutcome.Completed, result.Outcome);
        Assert.Equal(1, h.ImportCount);
    }

    [Fact]
    public void SystemRebootEvidence_JudgesAgainstBootTime()
    {
        // The stamp was written before this OS session booted → a reboot has happened since.
        Assert.True(SystemRebootEvidence.RebootedSince(SystemRebootEvidence.LastBootTimeUtc().AddMinutes(-1)));
        // The stamp was written after boot (this session) → no reboot yet.
        Assert.False(SystemRebootEvidence.RebootedSince(DateTimeOffset.UtcNow));
    }
}

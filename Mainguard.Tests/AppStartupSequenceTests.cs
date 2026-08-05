using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The Core startup orchestrator over a fake environment: step ORDER (exact status-text sequence),
/// keep-alive-first, blocking vs non-blocking (image build kicked but not awaited), timeout →
/// degraded continuation with the right banner, and the tier-2 declined/accepted branches (accepted
/// re-runs the daemon steps). No Avalonia, no VM — pure ordering/budget/branch logic.
/// </summary>
public class AppStartupSequenceTests
{
    private static readonly TimeSpan TinyBudget = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan TinyPoll = TimeSpan.FromMilliseconds(5);

    private sealed class Recorder : IProgress<StartupProgress>
    {
        public readonly List<StartupProgress> Ticks = new();

        public void Report(StartupProgress value) => Ticks.Add(value);

        public IReadOnlyList<string> Statuses =>
            Ticks.Where(t => t.Status.Length > 0).Select(t => t.Status).ToList();
    }

    private sealed class FakeEnv : IAppStartupEnvironment
    {
        public readonly List<string> Calls = new();
        public bool Reachable = true;
        public int ReachableProbeCount;
        public int Tier1Runs;
        public int OfferCalls;
        public int UpgradeCheckCalls;
        public VmUpgradeAvailability Availability = new(false, "1.0.0", "2.0.0");
        public VmUpgradeDecision Decision = VmUpgradeDecision.Declined;
        public IReadOnlyList<SandboxImageSpec> Missing = Array.Empty<SandboxImageSpec>();
        public bool BuildKicked;

        public bool VmUpgradeDeclinedThisSession { get; set; }

        public void Log(string message) => Calls.Add($"log:{message}");

        public void StartKeepAlive() => Calls.Add("StartKeepAlive");

        public Task WakeVmAsync(CancellationToken ct)
        {
            Calls.Add("WakeVm");
            return Task.CompletedTask;
        }

        public Task<bool> IsDaemonReachableAsync(CancellationToken ct)
        {
            Calls.Add("Reachable");
            ReachableProbeCount++;
            return Task.FromResult(Reachable);
        }

        /// <summary>What the post-budget diagnosis reports. Default: an unattributable failure, so a
        /// test that does not opt into a leg still exercises the honest fallback.</summary>
        public DaemonConnectDiagnosis Diagnosis =
            new(DaemonConnectStage.Undiagnosed, "fake: nothing established");

        /// <summary>The diagnosis returned by the SECOND diagnose call (after a repair), when set.</summary>
        public DaemonConnectDiagnosis? DiagnosisAfterRepair;
        public int DiagnoseCalls;
        public int RepairCalls;
        public DaemonRepairOutcome Repair = new(false, "fake: no repair configured");

        /// <summary>When true, a successful repair makes the daemon reachable — the real effect of
        /// redeploying a daemon the app can actually authenticate against.</summary>
        public bool RepairMakesReachable;

        public Task<DaemonConnectDiagnosis> DiagnoseDaemonConnectAsync(CancellationToken ct)
        {
            Calls.Add("Diagnose");
            DiagnoseCalls++;
            return Task.FromResult(
                DiagnoseCalls > 1 && DiagnosisAfterRepair is { } after ? after : Diagnosis);
        }

        public Task<DaemonRepairOutcome> RepairDaemonAsync(CancellationToken ct)
        {
            Calls.Add("Repair");
            RepairCalls++;
            if (Repair.Repaired && RepairMakesReachable)
            {
                Reachable = true;
            }

            return Task.FromResult(Repair);
        }

        public Task<DaemonRefreshOutcome> RefreshDaemonAsync(CancellationToken ct)
        {
            Calls.Add("Tier1");
            Tier1Runs++;
            return Task.FromResult(new DaemonRefreshOutcome(DaemonRefreshOutcomeKind.UpToDate, null, null, "up to date"));
        }

        public Task<VmUpgradeAvailability> CheckVmUpgradeAsync(CancellationToken ct)
        {
            Calls.Add("CheckUpgrade");
            UpgradeCheckCalls++;
            return Task.FromResult(Availability);
        }

        public Task<VmUpgradeDecision> OfferVmUpgradeAsync(VmUpgradeAvailability availability, CancellationToken ct)
        {
            Calls.Add("Offer");
            OfferCalls++;
            return Task.FromResult(Decision);
        }

        public Task<IReadOnlyList<SandboxImageSpec>> ProbeSandboxImagesAsync(CancellationToken ct)
        {
            Calls.Add("ProbeImages");
            return Task.FromResult(Missing);
        }

        public void KickSandboxImageBuild(IReadOnlyList<SandboxImageSpec> missing)
        {
            Calls.Add("KickBuild");
            BuildKicked = true;
        }
    }

    private static AppStartupSequence Sequence(FakeEnv env) =>
        new(env, TinyBudget, TinyPoll);

    [Fact]
    public async Task HappyPath_reports_exact_status_sequence_and_ends_ready()
    {
        var env = new FakeEnv();
        var rec = new Recorder();

        var result = await Sequence(env).RunAsync(rec, CancellationToken.None);

        Assert.Equal(StartupResult.Ready, result);
        Assert.False(result.IsDegraded);
        Assert.Equal(new[]
        {
            StartupStatus.WakingEnvironment,
            StartupStatus.ConnectingDaemon,
            StartupStatus.CheckingDaemon,
            StartupStatus.CheckingOsUpdate,
            StartupStatus.CheckingImages,
            StartupStatus.Ready,
        }, rec.Statuses);
    }

    [Fact]
    public async Task Keep_alive_is_started_first()
    {
        var env = new FakeEnv();

        await Sequence(env).RunAsync(null, CancellationToken.None);

        var meaningful = env.Calls.Where(c => !c.StartsWith("log:", StringComparison.Ordinal)).ToList();
        Assert.Equal("StartKeepAlive", meaningful[0]);
        Assert.True(
            meaningful.IndexOf("StartKeepAlive") < meaningful.IndexOf("WakeVm"),
            "keep-alive must start before the VM wake");
    }

    [Fact]
    public async Task Daemon_unreachable_within_budget_degrades_with_banner_and_skips_later_steps()
    {
        var env = new FakeEnv
        {
            Reachable = false,
            Diagnosis = new DaemonConnectDiagnosis(
                DaemonConnectStage.DaemonProcessNotRunning, "the unit is 'failed'."),
        };
        var rec = new Recorder();

        var result = await Sequence(env).RunAsync(rec, CancellationToken.None);

        Assert.False(result.DaemonReachable);
        // The banner names the leg that failed and the observation behind it — not the old generic line.
        Assert.Equal(env.Diagnosis.Banner, result.DegradedBanner);
        Assert.Contains("no mainguardd process was found", result.DegradedBanner);
        Assert.Contains("the unit is 'failed'.", result.DegradedBanner);
        Assert.NotEqual(StartupStatus.DaemonUnreachableBanner, result.DegradedBanner);
        Assert.Contains(rec.Ticks, t =>
            t.Stage == StartupStage.ConnectDaemon && t.State == BootstrapStageState.Failed);
        Assert.Contains(StartupStatus.DaemonUnreachableStatus, rec.Statuses);

        // Essentials-only degrade: no tier-1, no tier-2, no image probe once the daemon is out.
        Assert.Equal(0, env.Tier1Runs);
        Assert.Equal(0, env.UpgradeCheckCalls);
        Assert.DoesNotContain("ProbeImages", env.Calls);
        // Keep-alive still ran first.
        Assert.Contains("StartKeepAlive", env.Calls);
    }

    /// <summary>
    /// The owner-reported production bug: MainguardEnv held a daemon predating the pinned-mTLS control
    /// plane, so it published a session token but none of the credentials the client requires. Every RPC
    /// failed at channel construction, the budget lapsed, and the app degraded — permanently, because the
    /// tier-1 refresh that installs the matching daemon only runs AFTER a successful connect. The fix:
    /// diagnose that leg, redeploy the bundled daemon, and connect.
    /// </summary>
    [Fact]
    public async Task Daemon_older_than_this_build_is_repaired_and_the_app_connects()
    {
        var env = new FakeEnv
        {
            Reachable = false,
            Diagnosis = new DaemonConnectDiagnosis(
                DaemonConnectStage.TransportCredentialsMissing,
                "Its session directory 'X' holds a token but not daemon-server.cer and daemon-client.pfx."),
            Repair = new DaemonRepairOutcome(true, "daemon refreshed from 'payload'"),
            RepairMakesReachable = true,
        };
        var rec = new Recorder();

        var result = await Sequence(env).RunAsync(rec, CancellationToken.None);

        // The whole point: the app ENDS UP CONNECTED, with no degraded banner.
        Assert.True(result.DaemonReachable);
        Assert.False(result.IsDegraded);
        Assert.Null(result.DegradedBanner);
        Assert.Equal(1, env.RepairCalls);
        Assert.Contains(StartupStatus.RepairingDaemon, rec.Statuses);
        // And it went on to the normal startup work it would otherwise have skipped.
        Assert.Equal(1, env.Tier1Runs);
        Assert.Contains("ProbeImages", env.Calls);
        Assert.Contains(StartupStatus.Ready, rec.Statuses);
    }

    [Fact]
    public async Task A_repair_that_does_not_complete_reports_the_repairs_own_failure()
    {
        var env = new FakeEnv
        {
            Reachable = false,
            Diagnosis = new DaemonConnectDiagnosis(
                DaemonConnectStage.TransportCredentialsMissing, "session dir 'X' has no credentials."),
            Repair = new DaemonRepairOutcome(false, "refusing to refresh: payload missing its assembly"),
        };

        var result = await Sequence(env).RunAsync(new Recorder(), CancellationToken.None);

        Assert.False(result.DaemonReachable);
        Assert.Equal(1, env.RepairCalls);
        // Both facts survive: the original leg AND why the repair did not fix it.
        Assert.Contains("older than this Mainguard build", result.DegradedBanner);
        Assert.Contains("refusing to refresh: payload missing its assembly", result.DegradedBanner);
    }

    [Fact]
    public async Task A_repair_that_succeeds_but_still_will_not_connect_reports_the_state_after_the_repair()
    {
        var env = new FakeEnv
        {
            Reachable = false, // repair "succeeds" but the daemon still never answers
            Diagnosis = new DaemonConnectDiagnosis(
                DaemonConnectStage.TransportCredentialsMissing, "session dir 'X' has no credentials."),
            Repair = new DaemonRepairOutcome(true, "daemon refreshed"),
            RepairMakesReachable = false,
            DiagnosisAfterRepair = new DaemonConnectDiagnosis(
                DaemonConnectStage.DaemonProcessNotRunning, "the unit is 'activating'."),
        };

        var result = await Sequence(env).RunAsync(new Recorder(), CancellationToken.None);

        Assert.False(result.DaemonReachable);
        Assert.Equal(2, env.DiagnoseCalls); // re-diagnosed AFTER the repair changed the system
        // The banner describes what is true now, not the stale pre-repair verdict.
        Assert.Contains("no mainguardd process was found", result.DegradedBanner);
        Assert.DoesNotContain("older than this Mainguard build", result.DegradedBanner);
    }

    [Fact]
    public async Task A_leg_the_app_cannot_repair_degrades_immediately_without_touching_the_daemon()
    {
        var env = new FakeEnv
        {
            Reachable = false,
            Diagnosis = new DaemonConnectDiagnosis(
                DaemonConnectStage.DistroNotRunning, "'wsl --list --running' did not list MainguardEnv."),
        };

        var result = await Sequence(env).RunAsync(new Recorder(), CancellationToken.None);

        Assert.False(result.DaemonReachable);
        Assert.Equal(0, env.RepairCalls); // never redeploy a daemon over a problem that is not the daemon
        Assert.Contains("Mainguard OS isn't running", result.DegradedBanner);
    }

    /// <summary>Each leg must produce a DISTINCT banner — the defect was one sentence for every cause.</summary>
    [Fact]
    public async Task Every_diagnosed_leg_produces_its_own_distinct_banner()
    {
        var legs = new[]
        {
            DaemonConnectStage.DistroNotRunning,
            DaemonConnectStage.DaemonProcessNotRunning,
            DaemonConnectStage.NoSessionToken,
            DaemonConnectStage.TransportCredentialsMissing,
            DaemonConnectStage.NotListening,
            DaemonConnectStage.TokenRejected,
            DaemonConnectStage.Undiagnosed,
        };

        var banners = new List<string>();
        foreach (var leg in legs)
        {
            var env = new FakeEnv
            {
                Reachable = false,
                Diagnosis = new DaemonConnectDiagnosis(leg, $"observed:{leg}"),
                Repair = new DaemonRepairOutcome(false, "no payload"),
            };
            var result = await Sequence(env).RunAsync(new Recorder(), CancellationToken.None);
            Assert.NotNull(result.DegradedBanner);
            // Every banner carries the observation that produced it.
            Assert.Contains($"observed:{leg}", result.DegradedBanner);
            banners.Add(result.DegradedBanner!);
        }

        Assert.Equal(legs.Length, banners.Distinct().Count());
    }

    [Fact]
    public async Task Missing_images_kick_a_background_build_but_do_not_block()
    {
        var env = new FakeEnv { Missing = new[] { SandboxImages.AgentBase } };
        var rec = new Recorder();

        var result = await Sequence(env).RunAsync(rec, CancellationToken.None);

        Assert.Equal(StartupResult.Ready, result);
        Assert.True(env.BuildKicked, "the build must be kicked");
        Assert.Contains(StartupStatus.InstallingImages, rec.Statuses);
        // The probe was awaited (state known); the build was only kicked — never awaited.
        Assert.Contains("ProbeImages", env.Calls);
        Assert.Contains("KickBuild", env.Calls);
    }

    [Fact]
    public async Task Present_images_do_not_kick_a_build()
    {
        var env = new FakeEnv(); // Missing is empty
        await Sequence(env).RunAsync(null, CancellationToken.None);

        Assert.False(env.BuildKicked);
        Assert.DoesNotContain("KickBuild", env.Calls);
    }

    [Fact]
    public async Task Tier2_declined_sets_session_flag_and_runs_daemon_steps_once()
    {
        var env = new FakeEnv
        {
            Availability = new VmUpgradeAvailability(true, "1.0.0", "2.0.0"),
            Decision = VmUpgradeDecision.Declined,
        };

        var result = await Sequence(env).RunAsync(null, CancellationToken.None);

        Assert.Equal(StartupResult.Ready, result);
        Assert.True(env.VmUpgradeDeclinedThisSession);
        Assert.Equal(1, env.OfferCalls);
        Assert.Equal(1, env.Tier1Runs);
    }

    [Fact]
    public async Task Tier2_accepted_reruns_daemon_reachable_and_tier1_against_the_new_vm()
    {
        var env = new FakeEnv
        {
            Availability = new VmUpgradeAvailability(true, "1.0.0", "2.0.0"),
            Decision = VmUpgradeDecision.UpgradedOk,
        };
        var rec = new Recorder();

        var result = await Sequence(env).RunAsync(rec, CancellationToken.None);

        Assert.Equal(StartupResult.Ready, result);
        // Daemon steps ran a SECOND time against the upgraded VM.
        Assert.Equal(2, env.Tier1Runs);
        Assert.Contains(StartupStatus.ReconnectingAfterUpgrade, rec.Statuses);
        Assert.True(env.Calls.Count(c => c == "Tier1") == 2);
    }

    [Fact]
    public async Task Tier2_failed_keeps_old_vm_and_does_not_rerun()
    {
        var env = new FakeEnv
        {
            Availability = new VmUpgradeAvailability(true, "1.0.0", "2.0.0"),
            Decision = VmUpgradeDecision.UpgradeFailed,
        };

        var result = await Sequence(env).RunAsync(null, CancellationToken.None);

        Assert.Equal(StartupResult.Ready, result);
        Assert.Equal(1, env.Tier1Runs);
        Assert.False(env.VmUpgradeDeclinedThisSession);
    }

    [Fact]
    public async Task Tier2_already_declined_this_session_skips_the_offer_entirely()
    {
        var env = new FakeEnv
        {
            VmUpgradeDeclinedThisSession = true,
            Availability = new VmUpgradeAvailability(true, "1.0.0", "2.0.0"),
        };

        await Sequence(env).RunAsync(null, CancellationToken.None);

        Assert.Equal(0, env.UpgradeCheckCalls);
        Assert.Equal(0, env.OfferCalls);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The tier-1 daemon fast-path (field outage 2026-07: the daemon baked into the MainguardOS tarball
/// never advances with the app, so every new RPC answers Unimplemented — the coordinator-CLI-picker
/// skew). Covers the pure skew decision, the /mnt path translation, the exact in-distro refresh
/// command sequence over a fake <see cref="IWslRunner"/> (incl. the rollback dir and failure
/// recovery), the G-12 no-VM-wide-verbs invariant, and the <see cref="DaemonAutoRefresh"/>
/// orchestration (daemon-down skip, Unimplemented-as-skew, missing-payload skip).
/// </summary>
public class DaemonUpdaterTests
{
    // ---- DaemonUpdatePolicy: the pure skew decision -------------------------------------------

    [Fact]
    public void RefreshNeeded_WhenDaemonPredatesTheRpc()
    {
        // null == the daemon answered Unimplemented — the skew signal itself.
        Assert.True(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", daemonInfo: null));
    }

    [Fact]
    public void RefreshNeeded_WhenDaemonCannotNameItsVersion()
    {
        Assert.True(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", new DaemonVersionInfo("", "0.1.0")));
        Assert.True(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", new DaemonVersionInfo("   ", "")));
    }

    [Fact]
    public void RefreshNeeded_WhenVersionsDiffer()
    {
        Assert.True(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", new DaemonVersionInfo("0.1.0", "0.1.0")));
    }

    [Fact]
    public void RefreshNotNeeded_WhenVersionsMatch()
    {
        Assert.False(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", new DaemonVersionInfo("0.2.0", "0.1.0")));
    }

    [Fact]
    public void RefreshDecision_SemVerAndBuildMetadata()
    {
        // Same SemVer but a DIFFERENT commit on both sides → a genuinely different daemon build (a dev
        // rebuild / same-version hotfix): refresh, so iterating on the daemon actually reaches the VM.
        Assert.True(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0+abc123", new DaemonVersionInfo("0.2.0+def456", "")));
        // Same SemVer, same commit → the same binary → no refresh.
        Assert.False(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0+abc123", new DaemonVersionInfo("0.2.0+abc123", "")));
        // One side has no hash → we can't distinguish builds, so the matched SemVer stands (no refresh).
        Assert.False(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", new DaemonVersionInfo("0.2.0+def456", "")));
        Assert.False(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0+abc123", new DaemonVersionInfo("0.2.0", "")));
        // A SemVer bump always refreshes, regardless of hashes.
        Assert.True(DaemonUpdatePolicy.IsRefreshNeeded("0.3.0+abc123", new DaemonVersionInfo("0.2.0+abc123", "")));
    }

    // ---- MG-15: the decision is MONOTONIC — a refresh must move FORWARD ------------------------

    [Theory]
    // app version, deployed daemon version, expected decision
    [InlineData("0.3.0", "0.2.0", DaemonRefreshDecisionKind.Refresh)]            // newer  → refresh
    [InlineData("0.2.0", "0.2.0", DaemonRefreshDecisionKind.UpToDate)]           // equal  → no-op
    [InlineData("0.2.0", "0.3.0", DaemonRefreshDecisionKind.RefusedDowngrade)]   // older  → REFUSE
    [InlineData("1.0.0", "10.0.0", DaemonRefreshDecisionKind.RefusedDowngrade)]  // not a string compare
    [InlineData("0.2.0", "0.10.0", DaemonRefreshDecisionKind.RefusedDowngrade)]  // 0.2 < 0.10, textually ">"
    [InlineData("0.10.0", "0.2.0", DaemonRefreshDecisionKind.Refresh)]
    // Prerelease precedence: a prerelease is OLDER than its release, and orders within itself.
    [InlineData("1.0.0", "1.0.0-rc.1", DaemonRefreshDecisionKind.Refresh)]
    [InlineData("1.0.0-rc.1", "1.0.0", DaemonRefreshDecisionKind.RefusedDowngrade)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1", DaemonRefreshDecisionKind.Refresh)]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.2", DaemonRefreshDecisionKind.RefusedDowngrade)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", DaemonRefreshDecisionKind.RefusedDowngrade)]
    // Build metadata alone has no precedence — it falls through to the commit-hash rule below.
    [InlineData("0.2.0+abc123", "0.2.0+def456", DaemonRefreshDecisionKind.Refresh)]
    [InlineData("0.2.0+abc123", "0.2.0+abc123", DaemonRefreshDecisionKind.UpToDate)]
    [InlineData("0.2.0", "0.2.0+def456", DaemonRefreshDecisionKind.UpToDate)]
    [InlineData("0.2.0+abc123", "0.2.0", DaemonRefreshDecisionKind.UpToDate)]
    // …but an ORDERED difference still wins over the hash rule, in both directions.
    [InlineData("0.3.0+abc123", "0.2.0+abc123", DaemonRefreshDecisionKind.Refresh)]
    [InlineData("0.1.0+abc123", "0.2.0+def456", DaemonRefreshDecisionKind.RefusedDowngrade)]
    // Unorderable on either side: refuse rather than guess (guessing is where the hole reopens).
    [InlineData("not-a-version", "0.2.0", DaemonRefreshDecisionKind.RefusedUncomparable)]
    [InlineData("0.2.0", "whatever", DaemonRefreshDecisionKind.RefusedUncomparable)]
    public void RefreshDecision_IsMonotonic(string appVersion, string daemonVersion, DaemonRefreshDecisionKind expected)
    {
        var decision = DaemonUpdatePolicy.Decide(appVersion, new DaemonVersionInfo(daemonVersion, ""));

        Assert.Equal(expected, decision.Kind);
        Assert.Equal(expected == DaemonRefreshDecisionKind.Refresh, decision.ShouldRefresh);
        // IsRefreshNeeded is the same decision — both refusals answer false, like "up to date" does.
        Assert.Equal(
            expected == DaemonRefreshDecisionKind.Refresh,
            DaemonUpdatePolicy.IsRefreshNeeded(appVersion, new DaemonVersionInfo(daemonVersion, "")));
    }

    [Fact]
    public async Task AutoRefresh_AppOlderThanTheDeployedDaemon_RefusesLoudly_AndRefreshesNothing()
    {
        // The rolled-back-app case: without the monotonic guard this string-inequality path happily
        // overwrote a NEWER root-run daemon with an older binary and reported success.
        var updater = new RecordingUpdater();
        var log = new List<string>();
        var outcomes = new List<DaemonRefreshOutcome>();

        await DaemonAutoRefresh.RunAsync(
            "0.1.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.9.0", "")),
            updater,
            payloadDirectory: TempPayloadDir(withFile: true),
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero,
            onOutcome: outcomes.Add);

        Assert.Empty(updater.Refreshes);                       // nothing was promoted
        Assert.Contains(log, l => l.Contains("DOWNGRADE"));    // and it was not silent
        var outcome = Assert.Single(outcomes);
        Assert.Equal(DaemonRefreshOutcomeKind.RefusedDowngrade, outcome.Kind);
        // A refusal must NOT masquerade as "up to date" — it needs its own, warning-toned toast.
        var toast = DaemonRefreshToast.TryCompose(outcome);
        Assert.NotNull(toast);
        Assert.True(toast!.IsWarning);
        Assert.Contains("refused", toast.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- /mnt path translation ----------------------------------------------------------------

    [Fact]
    public void ToVmPath_TranslatesTheWindowsPayloadDir_ToItsDrvfsForm()
    {
        Assert.Equal(
            "/mnt/c/Program Files/Mainguard/payload/daemon",
            DaemonUpdater.ToVmPath(@"C:\Program Files\Mainguard\payload\daemon"));
    }

    [Fact]
    public void ToVmPath_PassesNativeLinuxPathsThrough()
    {
        // The Linux CI leg / tests hand native paths — untouched.
        Assert.Equal("/tmp/payload/daemon", DaemonUpdater.ToVmPath("/tmp/payload/daemon"));
    }

    // ---- The refresh command sequence over the IWslRunner seam --------------------------------

    [Fact]
    public async Task Refresh_RunsTheExactInDistroSequence_WithTheRollbackSwap()
    {
        var payload = RealPayloadDir();
        var wsl = RunnerHashing(StagedSums(payload));
        var result = await new DaemonUpdater(wsl).RefreshAsync(payload, CancellationToken.None);

        Assert.True(result.Succeeded);
        var vmPayload = DaemonUpdater.ToVmPath(payload);
        var expected = new[]
        {
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "systemctl", "stop", "mainguardd" },
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "rm", "-rf", "/opt/mainguard.new" },
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "mkdir", "-p", "/opt/mainguard.new" },
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "cp", "-r", vmPayload + "/.", "/opt/mainguard.new/" },
            // MG-9: the staged copy is hashed and matched against the shipped payload BEFORE the swap.
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "find", "/opt/mainguard.new", "-type", "f", "-exec", "sha256sum", "{}", "+" },
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "test", "-e", "/opt/mainguard.new/Mainguard.Server" },
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "mv", "/opt/mainguard.new/Mainguard.Server", "/opt/mainguard.new/mainguardd" },
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "chmod", "0755", "/opt/mainguard.new/mainguardd" },
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "rm", "-rf", "/opt/mainguard.old" },
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "mv", "/opt/mainguard", "/opt/mainguard.old" },
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "mv", "/opt/mainguard.new", "/opt/mainguard" },
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "systemctl", "start", "mainguardd" },
        };
        Assert.Equal(expected.Length, wsl.Calls.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], wsl.Calls[i]);
        }
    }

    [Fact]
    public async Task Refresh_SkipsTheApphostRename_WhenThePayloadShipsItAlreadyRenamed()
    {
        // A build.sh-produced payload already carries `mainguardd` — the probe misses, no mv.
        var payload = RealPayloadDir();
        var wsl = RunnerHashing(
            StagedSums(payload),
            args => args.Contains("test") ? new WslRunResult(1, "", "") : new WslRunResult(0, "", ""));

        var result = await new DaemonUpdater(wsl).RefreshAsync(payload, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("/opt/mainguard.new/Mainguard.Server") && c.Contains("mv"));
        Assert.Contains(wsl.Calls, c => c.Contains("chmod") && c.Contains("/opt/mainguard.new/mainguardd"));
    }

    [Fact]
    public async Task Refresh_WhenThePromoteFails_RestoresTheRollback_AndRestartsTheUnit()
    {
        var payload = RealPayloadDir();
        var wsl = RunnerHashing(
            StagedSums(payload),
            args => args.Contains("mv") && args.Contains("/opt/mainguard.new") && args.Contains("/opt/mainguard")
                ? new WslRunResult(1, "", "mv: cannot move")
                : new WslRunResult(0, "", ""));

        var result = await new DaemonUpdater(wsl).RefreshAsync(payload, CancellationToken.None);

        Assert.False(result.Succeeded);
        // Recovery: the retired install comes back, and the unit is started again.
        Assert.Contains(wsl.Calls, c => c.SequenceEqual(
            new[] { "-d", "MainguardEnv", "-u", "root", "--", "mv", "/opt/mainguard.old", "/opt/mainguard" }));
        Assert.Equal(new[] { "-d", "MainguardEnv", "-u", "root", "--", "systemctl", "start", "mainguardd" }, wsl.Calls[^1]);
    }

    [Fact]
    public async Task Refresh_WhenTheStagingCopyFails_NeverTouchesTheInstallDir_AndRestartsTheUnit()
    {
        var payload = RealPayloadDir();
        var wsl = RunnerHashing(
            StagedSums(payload),
            args => args.Contains("cp")
                ? new WslRunResult(1, "", "cp: no such file or directory")
                : new WslRunResult(0, "", ""));

        var result = await new DaemonUpdater(wsl).RefreshAsync(payload, CancellationToken.None);

        Assert.False(result.Succeeded);
        // The live install was never retired or overwritten…
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("/opt/mainguard.old") && c.Contains("mv"));
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("mv") && c.Contains("/opt/mainguard"));
        // …and the stopped unit is started again (a failed refresh never leaves the daemon down).
        Assert.Equal(new[] { "-d", "MainguardEnv", "-u", "root", "--", "systemctl", "start", "mainguardd" }, wsl.Calls[^1]);
    }

    // ---- MG-9: the staged payload is manifest+hash verified before it is promoted -----------------

    [Theory]
    // A truncated/corrupted file: the copy exited 0 but the bytes in the VM are not the bytes we shipped.
    [InlineData("corrupt")]
    // A partial copy: a file simply never arrived.
    [InlineData("omit")]
    // A polluted staging dir: something that was NOT in our payload would ride into /opt/mainguard.
    [InlineData("extra")]
    public async Task Refresh_StagedPayloadThatDoesNotMatchTheShippedOne_IsNeverPromoted(string mode)
    {
        var payload = RealPayloadDir();
        var sums = mode switch
        {
            "corrupt" => StagedSums(payload, corrupt: DaemonPayloadManifest.RequiredAssembly),
            "omit" => StagedSums(payload, omit: DaemonPayloadManifest.RequiredAssembly),
            _ => StagedSums(payload, extra: "stowaway.so"),
        };
        var wsl = RunnerHashing(sums);

        var result = await new DaemonUpdater(wsl).RefreshAsync(payload, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("refusing to promote", result.Message);
        // The live install must be untouched: no retire, no promote — the whole point of verifying
        // while the payload is still only STAGED.
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("mv") && c.Contains("/opt/mainguard.old"));
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("mv") && c.Contains("/opt/mainguard"));
        // …and the daemon is put back up on the build it was already running.
        Assert.Equal(new[] { "-d", "MainguardEnv", "-u", "root", "--", "systemctl", "start", "mainguardd" }, wsl.Calls[^1]);
    }

    [Fact]
    public async Task Refresh_WhenTheStagedPayloadCannotBeHashed_RefusesRatherThanPromotingBlind()
    {
        // "We could not check" must not read as "it is fine" — that is exactly the pre-MG-9 behaviour.
        var payload = RealPayloadDir();
        var wsl = new RecordingWslRunner
        {
            Responder = args => args.Contains("sha256sum")
                ? new WslRunResult(127, "", "sha256sum: command not found")
                : new WslRunResult(0, "", ""),
        };

        var result = await new DaemonUpdater(wsl).RefreshAsync(payload, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("could not hash the staged payload", result.Message);
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("mv") && c.Contains("/opt/mainguard.old"));
    }

    [Theory]
    [InlineData(false, "is empty")]                        // nothing to promote — would wipe /opt/mainguard
    [InlineData(true, "not a complete daemon build")]      // files, but no Mainguard.Server.dll
    public async Task Refresh_StructurallyInvalidPayload_IsRefusedBeforeTheDaemonIsEvenStopped(
        bool withStrayFile, string expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-bad-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (withStrayFile)
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a daemon");

        var wsl = new RecordingWslRunner();
        var result = await new DaemonUpdater(wsl).RefreshAsync(dir, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Message);
        // Refused before the payload could reach the VM at all: nothing was stopped, copied or moved.
        // The single call that DOES happen is the unconditional `systemctl start` from the recovery
        // path, which is correct — a refused refresh must still leave the daemon running.
        Assert.Equal(
            new[] { new[] { "-d", "MainguardEnv", "-u", "root", "--", "systemctl", "start", "mainguardd" } },
            wsl.Calls);
    }

    [Fact]
    public void PayloadManifest_ParsesRealSha256SumOutput_AndIgnoresAnythingElse()
    {
        var payload = RealPayloadDir(("sub/dir/extra.json", "{}"));
        var parsed = DaemonPayloadManifest.ParseSha256Sums(
            "sha256sum: WARNING: some noise on stdout\n" + StagedSums(payload),
            DaemonUpdateCommands.StagingDir);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(
            Sha256Of(Path.Combine(payload, DaemonPayloadManifest.RequiredAssembly)),
            parsed[DaemonPayloadManifest.RequiredAssembly]);
        Assert.Equal("sub/dir/extra.json", Assert.Single(parsed.Keys, k => k.Contains('/')));
        // A faithful copy has no discrepancy at all.
        Assert.Null(DaemonPayloadManifest.Build(payload).FindDiscrepancy(parsed));
    }

    // ---- G-12: distro-scoped, never the VM-wide shutdown verb ---------------------------------

    [Fact]
    public void G12_NoRefreshBuilderEmitsTheVmWideShutdownVerb_AndAllAreDistroScoped()
    {
        foreach (var builder in DaemonUpdateCommands.AllBuilders())
        {
            Assert.DoesNotContain("--shutdown", builder);
            // Every refresh command runs inside OUR distro only.
            Assert.Equal("-d", builder[0]);
            Assert.Equal(WslCommands.DistroName, builder[1]);
        }
    }

    // ---- DaemonAutoRefresh: the one startup call ----------------------------------------------

    [Fact]
    public async Task AutoRefresh_DaemonUnreachable_SkipsSilently_WithoutRefreshing()
    {
        var updater = new RecordingUpdater();
        var log = new List<string>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => throw new InvalidOperationException("connection refused"),
            updater,
            payloadDirectory: TempPayloadDir(withFile: true),
            log.Add,
            CancellationToken.None,
            queryAttempts: 2,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Empty(updater.Refreshes);
        Assert.Contains(log, l => l.Contains("unreachable"));
    }

    [Fact]
    public async Task AutoRefresh_UnimplementedAnswer_IsTheSkewSignal_AndRefreshes()
    {
        var updater = new RecordingUpdater();
        var log = new List<string>();
        var payload = TempPayloadDir(withFile: true);

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(null), // Unimplemented
            updater,
            payload,
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Equal(new[] { payload }, updater.Refreshes);
        Assert.Contains(log, l => l.Contains("pre-GetDaemonInfo"));
    }

    [Fact]
    public async Task AutoRefresh_MatchingVersions_DoesNothing()
    {
        var updater = new RecordingUpdater();
        var log = new List<string>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.2.0", "0.1.0")),
            updater,
            payloadDirectory: TempPayloadDir(withFile: true),
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Empty(updater.Refreshes);
        Assert.Contains(log, l => l.Contains("up to date"));
    }

    [Fact]
    public async Task AutoRefresh_SkewedButNoShippedPayload_SkipsAndSaysWhy()
    {
        var updater = new RecordingUpdater();
        var log = new List<string>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.1.0", "")),
            updater,
            payloadDirectory: Path.Combine(Path.GetTempPath(), "mainguard-nonexistent-" + Guid.NewGuid().ToString("N")),
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Empty(updater.Refreshes);
        Assert.Contains(log, l => l.Contains("no daemon payload"));
    }

    [Fact]
    public async Task AutoRefresh_SkewedButEmptyPayloadDir_Skips()
    {
        // An empty dir must never trigger a refresh — staging emptiness would wipe /opt/mainguard.
        var updater = new RecordingUpdater();
        var log = new List<string>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(null),
            updater,
            payloadDirectory: TempPayloadDir(withFile: false),
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Empty(updater.Refreshes);
        Assert.Contains(log, l => l.Contains("no daemon payload"));
    }

    [Fact]
    public async Task AutoRefresh_RetriesTheQuery_WhileTheVmBoots_ThenRefreshes()
    {
        var updater = new RecordingUpdater();
        var log = new List<string>();
        var calls = 0;

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => ++calls < 3
                ? throw new InvalidOperationException("still booting")
                : Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.1.0", "0.1.0")),
            updater,
            payloadDirectory: TempPayloadDir(withFile: true),
            log.Add,
            CancellationToken.None,
            queryAttempts: 5,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Equal(3, calls);
        Assert.Single(updater.Refreshes);
    }

    [Fact]
    public async Task AutoRefresh_AFailedRefresh_IsLogged_NeverThrown()
    {
        var updater = new RecordingUpdater { Outcome = new DaemonRefreshResult(false, "could not stop the mainguardd unit") };
        var log = new List<string>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(null),
            updater,
            payloadDirectory: TempPayloadDir(withFile: true),
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Contains(log, l => l.Contains("FAILED") && l.Contains("could not stop"));
    }

    // ---- The typed-outcome seam + the startup-toast policy (extend, never change, the log) ----

    [Fact]
    public async Task AutoRefresh_SuccessfulRefresh_ReportsRefreshedOutcome_WithOldAndNewVersions_AndComposesTheToast()
    {
        var outcomes = new List<DaemonRefreshOutcome>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0+abc123",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.1.0", "0.1.0")),
            new RecordingUpdater(),
            payloadDirectory: TempPayloadDir(withFile: true),
            log: _ => { },
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero,
            onOutcome: outcomes.Add);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(DaemonRefreshOutcomeKind.Refreshed, outcome.Kind);
        Assert.Equal("0.1.0", outcome.PreviousDaemonVersion);
        Assert.Equal("0.2.0", outcome.NewDaemonVersion); // build metadata stripped for display

        var toast = DaemonRefreshToast.TryCompose(outcome);
        Assert.NotNull(toast);
        Assert.Equal("Mainguard OS daemon updated to 0.2.0.", toast!.Message);
        Assert.False(toast.IsWarning);
    }

    [Fact]
    public async Task AutoRefresh_RefreshedFromAPreRpcDaemon_ReportsNullPreviousVersion()
    {
        var outcomes = new List<DaemonRefreshOutcome>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(null), // Unimplemented
            new RecordingUpdater(),
            payloadDirectory: TempPayloadDir(withFile: true),
            log: _ => { },
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero,
            onOutcome: outcomes.Add);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(DaemonRefreshOutcomeKind.Refreshed, outcome.Kind);
        Assert.Null(outcome.PreviousDaemonVersion);
        Assert.Equal("0.2.0", outcome.NewDaemonVersion);
    }

    [Fact]
    public async Task AutoRefresh_UpToDate_ReportsUpToDate_AndComposesNoToast()
    {
        var outcomes = new List<DaemonRefreshOutcome>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.2.0", "0.1.0")),
            new RecordingUpdater(),
            payloadDirectory: TempPayloadDir(withFile: true),
            log: _ => { },
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero,
            onOutcome: outcomes.Add);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(DaemonRefreshOutcomeKind.UpToDate, outcome.Kind);
        Assert.Null(DaemonRefreshToast.TryCompose(outcome));
    }

    [Fact]
    public async Task AutoRefresh_Unreachable_ReportsUnreachable_AndComposesNoToast()
    {
        var outcomes = new List<DaemonRefreshOutcome>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => throw new InvalidOperationException("connection refused"),
            new RecordingUpdater(),
            payloadDirectory: TempPayloadDir(withFile: true),
            log: _ => { },
            CancellationToken.None,
            queryAttempts: 2,
            queryRetryDelay: TimeSpan.Zero,
            onOutcome: outcomes.Add);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(DaemonRefreshOutcomeKind.Unreachable, outcome.Kind);
        Assert.Null(DaemonRefreshToast.TryCompose(outcome));
    }

    [Fact]
    public async Task AutoRefresh_SkewedButNoPayload_ReportsSkipped_AndComposesNoToast()
    {
        var outcomes = new List<DaemonRefreshOutcome>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.1.0", "")),
            new RecordingUpdater(),
            payloadDirectory: TempPayloadDir(withFile: false),
            log: _ => { },
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero,
            onOutcome: outcomes.Add);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(DaemonRefreshOutcomeKind.SkippedNoPayload, outcome.Kind);
        Assert.Null(DaemonRefreshToast.TryCompose(outcome));
    }

    [Fact]
    public async Task AutoRefresh_FailedRefresh_ReportsRefreshFailed_AndComposesTheWarningToast()
    {
        var outcomes = new List<DaemonRefreshOutcome>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.1.0", "0.1.0")),
            new RecordingUpdater { Outcome = new DaemonRefreshResult(false, "could not stop the mainguardd unit") },
            payloadDirectory: TempPayloadDir(withFile: true),
            log: _ => { },
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero,
            onOutcome: outcomes.Add);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(DaemonRefreshOutcomeKind.RefreshFailed, outcome.Kind);
        Assert.Equal("0.1.0", outcome.PreviousDaemonVersion);
        Assert.Null(outcome.NewDaemonVersion);

        var toast = DaemonRefreshToast.TryCompose(outcome);
        Assert.NotNull(toast);
        Assert.True(toast!.IsWarning);
        Assert.Contains("still on 0.1.0", toast.Message);
        Assert.Contains("oobe.log", toast.Message);
    }

    [Fact]
    public async Task AutoRefresh_AThrowingOutcomeCallback_NeverRipplesBack_AndTheLogStaysIntact()
    {
        var log = new List<string>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.2.0", "0.1.0")),
            new RecordingUpdater(),
            payloadDirectory: TempPayloadDir(withFile: true),
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero,
            onOutcome: _ => throw new InvalidOperationException("toast host exploded"));

        // The cosmetic consumer's failure is swallowed; the breadcrumb was already written.
        Assert.Contains(log, l => l.Contains("up to date"));
        Assert.DoesNotContain(log, l => l.Contains("toast host exploded"));
    }

    private static string TempPayloadDir(bool withFile)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-daemon-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (withFile)
        {
            File.WriteAllText(Path.Combine(dir, "mainguardd"), "stub");
        }

        return dir;
    }

    // ---- MG-9 staged-payload integrity: real payload dirs + a VM that can hash them ---------------

    /// <summary>A structurally valid daemon payload on disk: the required managed assembly plus a
    /// couple of siblings, so the manifest has something to be wrong about.</summary>
    private static string RealPayloadDir(params (string Name, string Content)[] extra)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DaemonPayloadManifest.RequiredAssembly), "managed-assembly-bytes");
        File.WriteAllText(Path.Combine(dir, "Mainguard.Server"), "apphost-bytes");
        foreach (var (name, content) in extra)
        {
            var full = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return dir;
    }

    private static string Sha256Of(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>
    /// The <c>sha256sum</c> stdout a FAITHFUL in-VM copy of <paramref name="payloadDir"/> would produce.
    /// <paramref name="corrupt"/> names a payload-relative file whose hash is returned wrong (the
    /// truncated-copy case); <paramref name="omit"/> names one left out entirely (the partial copy).
    /// </summary>
    private static string StagedSums(string payloadDir, string? corrupt = null, string? omit = null, string? extra = null)
    {
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(payloadDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(payloadDir, file).Replace('\\', '/');
            if (relative == omit)
                continue;
            var hash = relative == corrupt ? new string('a', 64) : Sha256Of(file);
            lines.Add($"{hash}  {DaemonUpdateCommands.StagingDir}/{relative}");
        }

        if (extra is not null)
            lines.Add($"{new string('b', 64)}  {DaemonUpdateCommands.StagingDir}/{extra}");

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>A runner that answers the staged-hash probe with <paramref name="sums"/> and succeeds
    /// at everything else — the "healthy VM" baseline every refresh test now needs.</summary>
    private static RecordingWslRunner RunnerHashing(string sums, Func<IReadOnlyList<string>, WslRunResult>? others = null)
        => new()
        {
            Responder = args => args.Contains("sha256sum")
                ? new WslRunResult(0, sums, "")
                : others?.Invoke(args) ?? new WslRunResult(0, "", ""),
        };

    private sealed class RecordingWslRunner : IWslRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public Func<IReadOnlyList<string>, WslRunResult>? Responder { get; set; }

        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult(Responder?.Invoke(args) ?? new WslRunResult(0, "", ""));
        }
    }

    private sealed class RecordingUpdater : IDaemonUpdater
    {
        public List<string> Refreshes { get; } = new();
        public DaemonRefreshResult Outcome { get; set; } = new(true, "refreshed");

        public Task<DaemonRefreshResult> RefreshAsync(string payloadDirectory, CancellationToken ct)
        {
            Refreshes.Add(payloadDirectory);
            return Task.FromResult(Outcome);
        }
    }
}

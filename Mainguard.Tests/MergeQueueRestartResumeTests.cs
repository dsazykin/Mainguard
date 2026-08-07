using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>A daemon killed mid-verification, restarted, with nobody touching anything.</b>
///
/// <para><b>The defect.</b> Queue state is persisted per transition; the in-flight set
/// (<see cref="MergeQueue.IsVerificationInFlight"/>) is memory. So a daemon that dies during a
/// verification leaves a row that says <c>Verifying</c> and a process that says nothing is running, and the
/// entry reports "verifying" — to a human, on the review surface — forever, about a run that no longer
/// exists. <see cref="MergeQueue.ResumeAfterRestartAsync"/> was written for exactly this and had
/// <b>no production caller</b>: it was reachable only from its own unit test. The shipped mitigation was a
/// <c>Clear stalled run</c> button, i.e. a human noticing and pressing something.</para>
///
/// <para><b>What these tests do that the old one did not.</b> The pre-existing coverage called
/// <c>ResumeAfterRestartAsync</c> directly and asserted it works — which was already true and is not the
/// defect. These kill a daemon in the middle of a real run and bring a second one up over the same
/// persisted rows, then make the <i>single production call a repo coming up makes</i>
/// (<see cref="MergeQueueProvisioner.EnsureQueue"/>) and assert where the entry lands. No test here calls
/// <c>ResumeAfterRestartAsync</c>, <c>TryClearStalledVerification</c> or <c>TryDiscard</c>; delete the
/// resume from <c>EnsureQueue</c> and every one of them fails on an entry still frozen at
/// <c>Verifying</c>.</para>
///
/// <para>The queue is built by the production <see cref="MergeQueueProvisioner"/> over a REAL bare mirror
/// and a REAL agent branch, so the resume runs through the real publish, drift check, RT-D2 provenance
/// resolve and flagged-change review. Only the container runtime is a fake — it is the thing a test cannot
/// have — and its jail-liveness answer is the input whose two values this whole change is about.</para>
/// </summary>
public sealed class MergeQueueRestartResumeTests : IDisposable
{
    private const string AgentId = "loom-resume";
    private const string ContainerId = "container-resume";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _vmRoot = NewDir("mainguard-resume-vm-");
    private readonly string _source = NewDir("mainguard-resume-src-");
    private readonly List<string> _artifactDirs = new();

    // The daemon's SQLite, stood in for by one shared pair. The load-bearing property is that BOTH daemon
    // instances read and write the SAME rows — a restart that came back over fresh stores would not be a
    // restart, it would be a different daemon, and it would hide the defect completely.
    private readonly InMemoryMergeQueueStore _rows = new();
    private readonly InMemoryVerificationStore _verifications = new();
    private readonly InMemoryAuditLog _audit = new();

    /// <summary>
    /// <b>The decisive one.</b> Kill the daemon mid-run with the jail still up — which is the ordinary case,
    /// because jails are persistent by design and outlive the daemon — and the restart re-runs the
    /// verification it interrupted and lands the entry on a real terminal, unprompted.
    /// </summary>
    [Fact]
    public async Task DaemonKilledMidVerification_ReRunsItOnRestart_AndTheEntryEndsVerified_WithNoHumanAction()
    {
        var repoHash = SeedAndProvision();
        CommitOnAgentBranch(repoHash);

        // ---- daemon instance 1: a verification that is really executing when the process dies. -------
        var dying = new GatedSandboxEngine(open: false);
        var first = NewDaemon(dying, jailAlive: true).EnsureQueue(repoHash)!;
        var interrupted = first.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        await Reached(dying.Entered, "the verification never reached the jail — there was nothing to interrupt");
        Assert.Equal(WorkerMergeState.Verifying, first.Queue.GetState(AgentId));
        Assert.Equal("Verifying", Row(repoHash).State);

        // The kill. The gate is never released and neither `first` nor `interrupted` is touched again:
        // this object graph is the dead process, and the row it left behind is all that survives it.
        Assert.False(interrupted.IsCompleted);

        // ---- daemon instance 2: same rows, everything else new. --------------------------------------
        var restartedJail = new GatedSandboxEngine(open: false);
        var ctx = NewDaemon(restartedJail, jailAlive: true).EnsureQueue(repoHash)!;

        // Rehydrated frozen — the shape the human used to have to clear by hand.
        Assert.Equal(WorkerMergeState.Verifying, ctx.Queue.GetState(AgentId));

        // ...and the resume is already re-running it. This is also what keeps #310's escape hatch honest:
        // a resume's run is in-flight like any other, so the button refuses instead of yanking the entry
        // out from under a live run (which would make that run's own completion an illegal transition).
        await Reached(restartedJail.Entered,
            "nothing re-ran the interrupted verification — the entry is still frozen at Verifying");
        Assert.True(ctx.Queue.IsVerificationInFlight(AgentId));
        Assert.False(ctx.Queue.TryClearStalledVerification(AgentId, "uid:1000", out var refusal));
        Assert.Contains("running for this entry right now", refusal);

        restartedJail.Release();
        var report = await ctx.Queue.LastResume.WaitAsync(Timeout);

        // A real terminal, reached by a real run, with nobody pressing anything.
        Assert.Equal(new[] { AgentId }, report.ReRun);
        Assert.Empty(report.Stranded);
        Assert.Equal(WorkerMergeState.Verified, ctx.Queue.GetState(AgentId));
        Assert.False(ctx.Queue.IsVerificationInFlight(AgentId));

        // ...persisted, which is what the NEXT restart would read.
        Assert.Equal("Verified", Row(repoHash).State);

        // ...and it is a verification, not a state edit: the command really executed in the jail and the
        // immutable record was written against the queue's authoritative main.
        Assert.Equal(new[] { "npm test" }, restartedJail.Commands.Select(c => string.Join(' ', c)));
        Assert.Equal(ContainerId, restartedJail.LastContainerId);
        var record = _verifications.Latest(repoHash, AgentId);
        Assert.NotNull(record);
        Assert.True(record!.Passed);
        Assert.Equal(ctx.Queue.CurrentMainSha, record.MainSha);

        // No human acted. The two operations a human has here leave audit events; neither is present.
        Assert.DoesNotContain(_audit.Read(), e => e.Type == MergeQueue.StalledVerificationClearedEvent);
        Assert.DoesNotContain(_audit.Read(), e => e.Type == MergeQueue.DiscardedEvent);
        var audited = Assert.Single(_audit.Read(), e => e.Type == MergeQueue.RestartResumeEvent);
        Assert.Equal("rerun", audited.Fields["outcome"]);
        Assert.Equal(AgentId, audited.Fields["agent"]);
    }

    /// <summary>
    /// The same kill, but the jail did not survive it. This entry <b>cannot</b> verify — verification runs
    /// in the worker's own sandbox and host execution is a rejection trigger (§3.2) — so "re-drive it" is
    /// not an available answer, and pretending otherwise is the same lie the frozen row was telling. It is
    /// returned to <c>Working</c> with the reason recorded, and nothing is executed anywhere.
    /// </summary>
    [Fact]
    public async Task DaemonKilledMidVerification_WhenTheJailDidNotSurvive_LandsOnWorking_AndRunsNothing()
    {
        var repoHash = SeedAndProvision();
        CommitOnAgentBranch(repoHash);

        var dying = new GatedSandboxEngine(open: false);
        var first = NewDaemon(dying, jailAlive: true).EnsureQueue(repoHash)!;
        var interrupted = first.Queue.RunVerificationAsync(AgentId, CancellationToken.None);
        await Reached(dying.Entered, "the verification never reached the jail");
        Assert.Equal("Verifying", Row(repoHash).State);
        Assert.False(interrupted.IsCompleted);

        // The restart finds no jail for this agent (the container is gone, not merely unrecorded — the
        // daemon's own resolver falls back to the Docker label listing before it answers null).
        var restartedJail = new GatedSandboxEngine(open: true);
        var ctx = NewDaemon(restartedJail, jailAlive: false).EnsureQueue(repoHash)!;
        var report = await ctx.Queue.LastResume.WaitAsync(Timeout);

        Assert.Equal(new[] { AgentId }, report.Stranded);
        Assert.Empty(report.ReRun);

        // Nothing was run, and nothing was claimed to have been run.
        Assert.Empty(restartedJail.Commands);
        Assert.Null(_verifications.Latest(repoHash, AgentId));

        // The entry is off Verifying, in memory and on disk, and says nothing about a verification.
        Assert.Equal(WorkerMergeState.Working, ctx.Queue.GetState(AgentId));
        Assert.Equal("Working", Row(repoHash).State);
        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Equal("not verified yet", reason); // NOT "verifying", and NOT "verification stalled"

        // Recorded as the daemon reconciling itself — NOT as a human clearing a stalled run, whose `by`
        // field would have had to name somebody who did nothing.
        var audited = Assert.Single(_audit.Read(), e => e.Type == MergeQueue.RestartResumeEvent);
        Assert.Equal("stranded", audited.Fields["outcome"]);
        Assert.DoesNotContain(_audit.Read(), e => e.Type == MergeQueue.StalledVerificationClearedEvent);

        // #310's button still behaves, and now truthfully reports that there is nothing left to clear.
        Assert.False(ctx.Queue.TryClearStalledVerification(AgentId, "uid:1000", out var refusal));
        Assert.Contains("not stuck verifying", refusal);

        // ...and the human's other action is still available on the entry the resume could not finish.
        Assert.True(ctx.Queue.TryDiscard(AgentId, "uid:1000", "its jail is gone", out var discardRefusal),
            discardRefusal);
        Assert.Equal(WorkerMergeState.Discarded, ctx.Queue.GetState(AgentId));
    }

    /// <summary>
    /// An ordinary restart — nothing was mid-verification — must not touch anything. A resume that moved a
    /// settled entry would be worse than the freeze it replaces.
    /// </summary>
    [Fact]
    public async Task RestartWithNothingInterrupted_LeavesEverySettledEntryExactlyWhereItWas()
    {
        var repoHash = SeedAndProvision();
        CommitOnAgentBranch(repoHash);

        var first = NewDaemon(new GatedSandboxEngine(open: true), jailAlive: true).EnsureQueue(repoHash)!;
        await first.Queue.RunVerificationAsync(AgentId, CancellationToken.None);
        Assert.Equal(WorkerMergeState.Verified, first.Queue.GetState(AgentId));

        var restartedJail = new GatedSandboxEngine(open: true);
        var ctx = NewDaemon(restartedJail, jailAlive: true).EnsureQueue(repoHash)!;
        var report = await ctx.Queue.LastResume.WaitAsync(Timeout);

        Assert.Empty(report.ReRun);
        Assert.Empty(report.Stranded);
        Assert.Empty(restartedJail.Commands);
        Assert.Equal(WorkerMergeState.Verified, ctx.Queue.GetState(AgentId));
        Assert.DoesNotContain(_audit.Read(), e => e.Type == MergeQueue.RestartResumeEvent);
    }

    /// <summary>
    /// The resume must not block the caller. <see cref="MergeQueueProvisioner.EnsureQueue"/> runs inside a
    /// gRPC handler (<c>ProvisionRepo</c>, every jailed spawn), and a resume runs the repo's whole test
    /// suite per interrupted entry — inline, that is a fix for a stuck queue that produces a stuck daemon.
    /// </summary>
    [Fact]
    public async Task EnsureQueue_ReturnsImmediately_WhileTheResumesVerificationIsStillRunning()
    {
        var repoHash = SeedAndProvision();
        CommitOnAgentBranch(repoHash);

        var dying = new GatedSandboxEngine(open: false);
        var first = NewDaemon(dying, jailAlive: true).EnsureQueue(repoHash)!;
        _ = first.Queue.RunVerificationAsync(AgentId, CancellationToken.None);
        await Reached(dying.Entered, "the verification never reached the jail");

        // This jail never answers until we say so — so if EnsureQueue awaited the resume, it would hang here.
        var restartedJail = new GatedSandboxEngine(open: false);
        var provisioner = NewDaemon(restartedJail, jailAlive: true);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ctx = provisioner.EnsureQueue(repoHash)!;
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5),
            $"EnsureQueue blocked for {clock.Elapsed} — it must not await the resume");
        Assert.False(ctx.Queue.LastResume.IsCompleted);

        restartedJail.Release();
        await ctx.Queue.LastResume.WaitAsync(Timeout);
        Assert.Equal(WorkerMergeState.Verified, ctx.Queue.GetState(AgentId));
    }

    // ---- harness ---------------------------------------------------------

    /// <summary>
    /// One daemon instance's merge-queue graph: a fresh registry and provisioner (a restart keeps nothing
    /// in memory) over the SHARED persisted stores (a restart keeps everything on disk).
    /// </summary>
    /// <param name="jailAlive">Whether this instance's jail lookup answers for <see cref="AgentId"/> —
    /// the production seam is <c>GatewayServiceRegistration.ResolveVerificationJail</c>, which answers null
    /// when neither the session store nor the Docker label listing has a running container.</param>
    private MergeQueueProvisioner NewDaemon(ISandboxEngine sandboxes, bool jailAlive)
    {
        var artifacts = NewDir("mainguard-resume-artifacts-");
        _artifactDirs.Add(artifacts);

        return new MergeQueueProvisioner(
            registry: new MergeQueueRegistry(),
            repos: new RepoProvisioner(_vmRoot),
            leases: new InMemoryMergeLeaseStore(),
            resolveContainerId: (_, agentId) => jailAlive && agentId == AgentId ? ContainerId : null,
            queueStore: _ => _rows,
            verificationStore: _ => _verifications,
            sandboxes: sandboxes,
            artifactDirectory: artifacts,
            mergeDiff: new MergeBranchDiffService(
                new RepoProvisioner(_vmRoot),
                (repoHash, agentId) => new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, agentId)),
            audit: _audit,
            publishAgentRef: (repoHash, agentId) => new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, agentId),
            checkAgentBranch: (repoHash, agentId) => new WorktreeManager(_vmRoot).CheckAgentBranch(repoHash, agentId));
    }

    /// <summary>Awaits a signal with a named failure, so a regression reads as the fact that went missing
    /// rather than as an unexplained TimeoutException.</summary>
    private static async Task Reached(Task signal, string because)
    {
        var finished = await Task.WhenAny(signal, Task.Delay(Timeout)).ConfigureAwait(false);
        Assert.True(ReferenceEquals(finished, signal), because);
        await signal.ConfigureAwait(false);
    }

    private Mainguard.Git.Models.MergeQueueRow Row(string repoHash)
        => _rows.LoadAll(repoHash).Single(r => r.AgentId == AgentId);

    private string SeedAndProvision()
    {
        Repository.Init(_source);
        using (var repo = new Repository(_source))
        {
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        }

        WriteAndCommit(_source, MergeQueueProvisioner.VerificationConfigPath, "npm test\n", "seed verify config");
        return new RepoProvisioner(_vmRoot).Provision(_source).RepoHash;
    }

    private void CommitOnAgentBranch(string repoHash)
    {
        var worktree = new WorktreeManager(_vmRoot).CreateAgentWorktree(repoHash, AgentId);
        WriteAndCommit(worktree, "feature.cs", "public class Feature { }\n", "the agent's actual work");
    }

    private static void WriteAndCommit(string repoPath, string relPath, string content, string message)
    {
        var full = Path.Combine(repoPath, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, relPath);
        var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
        repo.Commit(message, sig, sig);
    }

    private static string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var dir in _artifactDirs.Concat(new[] { _vmRoot, _source }))
        {
            TryDelete(dir);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Never fail a test from cleanup.
        }
    }

    /// <summary>
    /// A container runtime whose exec can be held open — which is how a daemon gets killed "during" a
    /// verification rather than between two of them. <see cref="Entered"/> completes the first time a
    /// command actually reaches the jail, so a test can assert the run was real before interrupting it.
    /// </summary>
    private sealed class GatedSandboxEngine : ISandboxEngine
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IReadOnlyList<string>> _commands = new();
        private readonly int _exitCode;

        public GatedSandboxEngine(bool open, int exitCode = 0)
        {
            _exitCode = exitCode;
            if (open)
            {
                _gate.TrySetResult();
            }
        }

        /// <summary>Completes when a command first reaches this jail.</summary>
        public Task Entered => _entered.Task;

        public string? LastContainerId { get; private set; }

        public IReadOnlyList<IReadOnlyList<string>> Commands
        {
            get { lock (_commands) return _commands.ToArray(); }
        }

        public void Release() => _gate.TrySetResult();

        public async Task<SandboxExecResult> ExecAsync(
            string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
        {
            LastContainerId = containerId;
            lock (_commands)
            {
                _commands.Add(command);
            }

            _entered.TrySetResult();
            await _gate.Task.ConfigureAwait(false);
            return new SandboxExecResult(_exitCode, "output", "");
        }

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnpauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

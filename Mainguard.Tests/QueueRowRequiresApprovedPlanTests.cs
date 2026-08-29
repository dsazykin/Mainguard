using System;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Defect G1 — <b>an agent that never presented a plan still got a merge-queue row.</b>
///
/// <para><b>What was observed.</b> Three <c>scripted</c> probes made ZERO plan-shim calls and received ZERO
/// approvals. Each one published <c>refs/heads/agent/&lt;id&gt;</c> AND appeared in the merge queue, at the
/// same time as <c>get_worker_status</c> correctly answered "no work is authorised" about them. The daemon
/// database still holds the evidence: three <c>MergeQueueRows</c> whose agent ids appear nowhere in the plan
/// store at all.</para>
///
/// <para><b>What was and was not already gated.</b> <see cref="WorkerPlanGate"/> is an
/// <see cref="IMergeGate"/> and IS ANDed into every repo's queue, so unapproved work genuinely could not
/// merge — that boundary was real and is untouched here. What was ungated was (a) publication and (b) row
/// creation: <see cref="MergeQueueProvisioner.EnsureEntry"/> consulted nothing at all.</para>
///
/// <para><b>The boundary chosen, and why it is the row and not the publish.</b> A branch existing is not the
/// harm — F1 established that an agent's branch must survive its own teardown, so refusing to publish would
/// destroy work in order to fix a display problem. A queue row is something else: it is a claim on human
/// attention, a line in the list a person is asked to work through, and it arrives carrying Verify — the
/// daemon offering to spend a test-suite run on work nobody authorised. That is what should require an
/// approved plan.</para>
///
/// <para>The other half is just as load-bearing and has its own tests below: a worker held at the gate is
/// the NORMAL case (every coordinator-spawned worker is spawned before it has presented anything), so the
/// row has to appear the moment the plan is approved. A gate with no way back would not be a fix, it would
/// break the normal path.</para>
/// </summary>
public sealed class QueueRowRequiresApprovedPlanTests : IDisposable
{
    private readonly string _vmRoot = NewDir("mainguard-g1-vm-");
    private readonly string _source = NewDir("mainguard-g1-src-");
    private readonly InMemoryAuditLog _audit = new();
    private readonly MergeQueueRegistry _registry = new();
    private readonly PlanApprovalService _plans;
    private readonly WorkerPlanGate _gate;

    public QueueRowRequiresApprovedPlanTests()
    {
        _plans = new PlanApprovalService(audit: _audit);
        _gate = new WorkerPlanGate(_plans, _audit);
    }

    // ---- The defect ------------------------------------------------------

    /// <summary>
    /// The scripted probe, reproduced: a worker the plan gate is holding, that has presented nothing and
    /// been approved for nothing, gets NO merge-queue row.
    ///
    /// <para>The mutation this pins is the whole of the fix — delete the gate consultation from
    /// <c>EnsureEntry</c> and this fails with the row present, which is the state the live daemon was in.</para>
    /// </summary>
    [Fact]
    public void AWorkerThatNeverPresentedAPlan_GetsNoQueueRow()
    {
        var repoHash = SeedAndProvision();
        var provisioner = NewProvisioner();
        Hold("probe-1");

        provisioner.EnsureEntry(repoHash, "probe-1");

        var ctx = provisioner.EnsureQueue(repoHash)!;
        Assert.DoesNotContain("probe-1", ctx.Queue.Agents);

        // …and the withholding is OBSERVABLE rather than only inferable from an absent row: the row is
        // owed, and the daemon said so at the time.
        Assert.Contains(provisioner.DeferredEntries(),
            k => k.RepoHandle == repoHash && k.AgentId == "probe-1");
        Assert.Contains(_audit.Read(), e =>
            e.Type == MergeQueueProvisioner.QueueEntryDeferredEvent && e.Fields["agent"] == "probe-1");
    }

    /// <summary>
    /// A worker whose plan is PENDING is still at the gate — presenting is not approval, and a row that
    /// appeared on presentation would put the claim on the human's attention before they had made the
    /// decision that authorises it.
    /// </summary>
    [Fact]
    public void APendingPlan_IsNotAnApprovedOne_AndStillGetsNoRow()
    {
        var repoHash = SeedAndProvision();
        var provisioner = NewProvisioner();
        Hold("w-pending");
        Present("w-pending");

        provisioner.EnsureEntry(repoHash, "w-pending");

        Assert.DoesNotContain("w-pending", provisioner.EnsureQueue(repoHash)!.Queue.Agents);
    }

    // ---- The normal path, which must keep working ------------------------

    /// <summary>
    /// The row appears the moment the plan is approved — the half without which this change would be a
    /// regression rather than a fix.
    /// </summary>
    [Fact]
    public void ApprovingThePlan_AdmitsTheRow_AtTheRightMoment()
    {
        var repoHash = SeedAndProvision();
        var provisioner = NewProvisioner();
        Hold("w-1");
        provisioner.EnsureEntry(repoHash, "w-1");
        Assert.DoesNotContain("w-1", provisioner.EnsureQueue(repoHash)!.Queue.Agents);

        var planId = Present("w-1");
        _plans.Approve(planId, "tester");

        var admitted = provisioner.AdmitDeferredEntries();

        Assert.Equal(new[] { "w-1" }, admitted);
        var ctx = provisioner.EnsureQueue(repoHash)!;
        Assert.Contains("w-1", ctx.Queue.Agents);
        Assert.Equal(WorkerMergeState.Working, ctx.Queue.GetState("w-1"));
        Assert.Contains(_audit.Read(), e =>
            e.Type == MergeQueueProvisioner.QueueEntryAdmittedEvent && e.Fields["agent"] == "w-1");

        // Nothing is owed any more, so a later approval elsewhere cannot re-create this row.
        Assert.Empty(provisioner.DeferredEntries());
    }

    /// <summary>
    /// The admission RE-ASKS the gate; it does not trust having been called. A pass triggered while a
    /// different worker's plan was approved must not admit a worker that is still at the gate — which is
    /// exactly what an implementation that treated the event as permission would do.
    /// </summary>
    [Fact]
    public void AdmitDeferredEntries_ReAsksTheGate_AndLeavesUnapprovedWorkersDeferred()
    {
        var repoHash = SeedAndProvision();
        var provisioner = NewProvisioner();
        Hold("w-approved");
        Hold("w-still-waiting");
        provisioner.EnsureEntry(repoHash, "w-approved");
        provisioner.EnsureEntry(repoHash, "w-still-waiting");

        _plans.Approve(Present("w-approved"), "tester");
        var admitted = provisioner.AdmitDeferredEntries();

        Assert.Equal(new[] { "w-approved" }, admitted);
        var agents = provisioner.EnsureQueue(repoHash)!.Queue.Agents;
        Assert.Contains("w-approved", agents);
        Assert.DoesNotContain("w-still-waiting", agents);
        Assert.Contains(provisioner.DeferredEntries(), k => k.AgentId == "w-still-waiting");
    }

    /// <summary>
    /// A worker whose plan is already approved before the entry is ever ensured takes the direct path —
    /// no deferral, no admission pass. This is the shape a resume or a re-provision produces.
    /// </summary>
    [Fact]
    public void AnAlreadyApprovedWorker_GetsItsRowImmediately()
    {
        var repoHash = SeedAndProvision();
        var provisioner = NewProvisioner();
        Hold("w-1");
        _plans.Approve(Present("w-1"), "tester");

        provisioner.EnsureEntry(repoHash, "w-1");

        Assert.Contains("w-1", provisioner.EnsureQueue(repoHash)!.Queue.Agents);
        Assert.Empty(provisioner.DeferredEntries());
    }

    // ---- The gate answers only for what it holds -------------------------

    /// <summary>
    /// <b>The paired negative, and the one that would break the product if it were wrong.</b> An agent the
    /// plan gate never held — a manual-mode agent the human drives by hand, an external-PR head, a seeded
    /// entry — is not governed by the plan gate and must get its row exactly as before. Making the gate
    /// default-deny for unknown ids would silently empty the merge queue of every non-coordinated branch,
    /// which is a far larger failure than the one being fixed.
    ///
    /// <para>This is the same default <see cref="WorkerPlanGate.Allows"/> already applies as a merge gate,
    /// asked through the same method, deliberately: a second opinion about what "approved" means is how one
    /// of the two copies goes decorative (MG-12).</para>
    /// </summary>
    [Fact]
    public void AnAgentTheGateNeverHeld_GetsItsRowUnchanged()
    {
        var repoHash = SeedAndProvision();
        var provisioner = NewProvisioner();

        provisioner.EnsureEntry(repoHash, "manual-agent");
        provisioner.EnsureEntry(repoHash, "pr-7", MergeEntryOrigin.External);

        var ctx = provisioner.EnsureQueue(repoHash)!;
        Assert.Contains("manual-agent", ctx.Queue.Agents);
        Assert.Contains("pr-7", ctx.Queue.Agents);
        Assert.Equal(MergeEntryOrigin.External, ctx.Queue.GetOrigin("pr-7"));
        Assert.Empty(provisioner.DeferredEntries());
    }

    /// <summary>
    /// A provisioner given NO plan gate behaves exactly as it always did. The daemon always supplies one,
    /// but the parameter is optional and a great many fixtures leave it out; a null gate that started
    /// withholding rows would fail them for a reason none of them is about.
    /// </summary>
    [Fact]
    public void WithNoPlanGateAtAll_EveryEntryIsCreated()
    {
        var repoHash = SeedAndProvision();
        var provisioner = NewProvisioner(withPlanGate: false);
        Hold("w-1"); // held by a gate this provisioner cannot see

        provisioner.EnsureEntry(repoHash, "w-1");

        Assert.Contains("w-1", provisioner.EnsureQueue(repoHash)!.Queue.Agents);
    }

    /// <summary>The owed rows go with the queue they were owed against: a torn-down repo has nothing to
    /// admit them into, so they must not outlive it.</summary>
    [Fact]
    public void RemovingARepo_DropsTheRowsItWasOwed()
    {
        var repoHash = SeedAndProvision();
        var provisioner = NewProvisioner();
        Hold("w-1");
        provisioner.EnsureEntry(repoHash, "w-1");
        Assert.NotEmpty(provisioner.DeferredEntries());

        provisioner.Remove(repoHash);

        Assert.Empty(provisioner.DeferredEntries());
    }

    // ---- helpers ---------------------------------------------------------

    private void Hold(string workerId) =>
        _gate.Hold(workerId, "coord", "Fix the clock", "the actual work to do", 1m);

    private string Present(string workerId) =>
        _plans.Present(
            workerId, "coord", "Fix the clock",
            new TaskPlanFields(new[] { "src/a.cs" }, "how", "tests"), "", 1m).PlanId!;

    private MergeQueueProvisioner NewProvisioner(bool withPlanGate = true) => new(
        registry: _registry,
        repos: new RepoProvisioner(_vmRoot),
        leases: new InMemoryMergeLeaseStore(),
        resolveContainerId: (_, _) => "container-g1",
        queueStore: _ => new InMemoryMergeQueueStore(),
        verificationStore: _ => new InMemoryVerificationStore(),
        sandboxes: new NoRunSandboxEngine(),
        artifactDirectory: NewDir("mainguard-g1-artifacts-"),
        mergeDiff: new MergeBranchDiffService(
            new RepoProvisioner(_vmRoot),
            (repoHash, agentId) => new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, agentId)),
        audit: _audit,
        planGate: withPlanGate ? _gate : null);

    private string SeedAndProvision()
    {
        Repository.Init(_source);
        using (var repo = new Repository(_source))
        {
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        }

        var path = Path.Combine(_source, MergeQueueProvisioner.VerificationConfigPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "npm test\n");
        using (var repo = new Repository(_source))
        {
            Commands.Stage(repo, "*");
            var who = new Signature("test-user", "test@mainguard.local", DateTimeOffset.UtcNow);
            repo.Commit("seed verify config", who, who);
        }

        return new RepoProvisioner(_vmRoot).Provision(_source).RepoHash;
    }

    /// <summary>A sandbox engine no test here ever reaches: every one of them stops at row CREATION, and a
    /// verification run would mean the fixture had wandered into a different subject. Throwing is the
    /// assertion.</summary>
    private sealed class NoRunSandboxEngine : ISandboxEngine
    {
        public System.Threading.Tasks.Task<SandboxExecResult> ExecAsync(
            string containerId, System.Collections.Generic.IReadOnlyList<string> command,
            System.Threading.CancellationToken ct = default) => throw new NotSupportedException();

        public System.Threading.Tasks.Task<SandboxHandle> SpawnAsync(
            SandboxSpawnRequest request, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();

        public System.Threading.Tasks.Task PauseAsync(string containerId, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public System.Threading.Tasks.Task UnpauseAsync(string containerId, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public System.Threading.Tasks.Task StopAsync(string containerId, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public System.Threading.Tasks.Task RemoveAsync(string containerId, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _vmRoot, _source })
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // A temp directory a test could not remove is not a test failure.
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Git.Review;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests;

/// <summary>
/// L2 — <b>a merge leaves a tamper-evident record.</b>
///
/// <para>Found in live testing: a real merge through the UI moved main <c>ffbc3bc → d8a987f</c> in the
/// mirror and in the user's checkout, the queue row went <c>Merged</c>, the lease recorded
/// <c>Confirmed=1, PostMergeSha=d8a987f</c> — and the audit chain got nothing. There was no merge event
/// type in the product at all: 33 types existed and they included <c>queue_entry_discarded</c>, so the
/// chain recorded the act of DROPPING an entry and not the act of merging one. The single most
/// consequential thing the product does, the one that rewrites the user's main branch, was the one action
/// with no artifact (G-17's whole premise).</para>
///
/// <para>The invariant these tests pin is deliberately stronger than "the RPC audits": <b>no transition
/// to <see cref="WorkerMergeState.Merged"/>, by any path, without exactly one
/// <see cref="MergeQueue.MergedEvent"/></b>. Four paths reach it — the <c>ConfirmMerge</c> RPC, the RT-D1
/// boot reconcile, the external-PR dispatch, and dev seeding — and an event wired to only the first would
/// leave a crash-recovered merge exactly as unrecorded as every merge was before.</para>
/// </summary>
public class MergeAuditEventTests
{
    private const string MainSha = "main-sha-0";
    private const string AgentId = "loom-1";

    [Fact]
    public async Task GatedConfirm_AppendsExactlyOneMergedEvent()
    {
        var h = new Harness();
        var queue = h.Build();
        await queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.True(queue.TryConfirmHumanMerge(
            AgentId, "main-sha-1", MainSha, out _, MergeAuthorization.ConfirmRpc("owner@example", "lease-9")));

        var merged = Assert.Single(h.Audit.Read(), e => e.Type == MergeQueue.MergedEvent);
        Assert.Equal("owner@example", merged.Fields["by"]);
        Assert.Equal(MergeAuthorization.ConfirmRpcSource, merged.Fields["source"]);
        Assert.Equal("lease-9", merged.Fields["lease"]);
    }

    /// <summary>
    /// The pre/post pair is the point: an event carrying only the new sha cannot answer "what did main
    /// used to be", which is the first question anyone asks when a merge turns out to have been wrong.
    /// The pre-merge sha is read under the same lock that decided the merge, so it is the main the gates
    /// actually passed against and not whatever main became afterwards.
    /// </summary>
    [Fact]
    public async Task MergedEvent_CarriesBothShas_AndTheVerificationItRodeOn()
    {
        var h = new Harness();
        var queue = h.Build();
        await queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.True(queue.TryConfirmHumanMerge(AgentId, "main-sha-1", MainSha, out _));

        var merged = Assert.Single(h.Audit.Read(), e => e.Type == MergeQueue.MergedEvent);
        Assert.Equal(MainSha, merged.Fields["pre_main_sha"]);
        Assert.Equal("main-sha-1", merged.Fields["post_main_sha"]);
        Assert.Equal("repo", merged.Fields["repo"]);
        Assert.Equal(AgentId, merged.Fields["agent"]);

        // The verification record the merge relied on — the branch could not have merged without it, and
        // "which run said this was green" is unanswerable from the state machine alone once the entry is
        // terminal.
        Assert.Equal(MainSha, merged.Fields["verification_main_sha"]);
        Assert.Equal("true", merged.Fields["verification_passed"]);
        Assert.Equal("npm test", merged.Fields["verification_command"]);
        Assert.Equal("confighash", merged.Fields["verification_config_hash"]);

        // The state the entry was in when the merge was AUTHORIZED, captured before the Verified →
        // AwaitingReview → Merged walk runs. Read afterwards it would always be "Merged" — an audit
        // record of its own effect.
        Assert.Equal(WorkerMergeState.Verified.ToString(), merged.Fields["from_state"]);
    }

    /// <summary>
    /// The RT-D1 reconcile entry point is unconditional (it records a merge that already landed on a ref),
    /// which is exactly why it must audit too — a crash-recovered merge is the one nobody watched happen.
    /// It is attributed to the reconciler, never a person: <c>RestartResumeEvent</c> exists as a separate
    /// type for the same reason, and putting an actor's name on a boot pass attributes a decision nobody
    /// made.
    /// </summary>
    [Fact]
    public async Task BootReconcileConfirm_AlsoAudits_AttributedToTheReconciler()
    {
        var h = new Harness();
        var queue = h.Build();
        await queue.RunVerificationAsync(AgentId, CancellationToken.None);

        queue.ConfirmHumanMerge(AgentId, "main-sha-1", MergeAuthorization.BootReconcile("lease-7"));

        var merged = Assert.Single(h.Audit.Read(), e => e.Type == MergeQueue.MergedEvent);
        Assert.Equal(MergeQueue.ReconcilerActor, merged.Fields["by"]);
        Assert.Equal(MergeAuthorization.BootReconcileSource, merged.Fields["source"]);
        Assert.Equal("lease-7", merged.Fields["lease"]);
    }

    /// <summary>
    /// The reconcile path can record a merge for an entry whose verification this queue does not hold —
    /// the exact shape a daemon restart produces, and the one the RT-D1 boot replay walks: the persisted
    /// state rehydrates as <c>Verified</c> while the verification row is gone.
    ///
    /// <para>The absence of a record is a REAL state, so it is stated as such rather than rendered as a
    /// row of empty verification fields that reads like a run which printed nothing — the same
    /// distinction <c>GetVerificationLog</c>'s <c>unavailable_reason</c> exists to preserve.</para>
    /// </summary>
    [Fact]
    public async Task MergedEvent_WithNoVerificationRecord_SaysSo_RatherThanEmptyFields()
    {
        var h = new Harness();
        var first = h.Build();
        await first.RunVerificationAsync(AgentId, CancellationToken.None);

        // Restart: same persisted queue state, a verification store that no longer has the row.
        var rebuilt = h.Build(freshVerificationStore: true);
        Assert.Equal(WorkerMergeState.Verified, rebuilt.GetState(AgentId));
        Assert.Null(rebuilt.LastVerification(AgentId));

        rebuilt.ConfirmHumanMerge(AgentId, "main-sha-1", MergeAuthorization.BootReconcile());

        var merged = Assert.Single(h.Audit.Read(), e => e.Type == MergeQueue.MergedEvent);
        Assert.Equal("none recorded", merged.Fields["verification"]);
        Assert.False(merged.Fields.ContainsKey("verification_command"));
    }

    /// <summary>
    /// A refused confirm must record NO merge. This is the mutation that matters most: an implementation
    /// that audits before the gates decide would manufacture a merge record for a branch that never
    /// merged — worse than the missing event it replaces, because a false record is believed.
    /// </summary>
    [Fact]
    public async Task RefusedConfirm_AuditsNothing()
    {
        var h = new Harness();
        var queue = h.Build(withChangedGate: true);
        await queue.RunVerificationAsync(AgentId, CancellationToken.None);
        h.ChangedGate.SetFlagged(AgentId, changed: true); // unacknowledged → the gate refuses.

        Assert.False(queue.TryConfirmHumanMerge(AgentId, "main-sha-1", MainSha, out var reason));
        Assert.Contains("acknowledge to merge", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Audit.Read(), e => e.Type == MergeQueue.MergedEvent);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
    }

    /// <summary>
    /// A lost CAS race is the other refusal shape, and it is refused before the gates are even consulted.
    /// Same requirement: nothing recorded.
    /// </summary>
    [Fact]
    public async Task ConfirmWithALostCasRace_AuditsNothing()
    {
        var h = new Harness();
        var queue = h.Build();
        await queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(queue.TryConfirmHumanMerge(AgentId, "main-sha-2", "some-other-main", out var reason));
        Assert.Contains("main moved", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Audit.Read(), e => e.Type == MergeQueue.MergedEvent);
    }

    /// <summary>
    /// The evidence half — what the gates had established, which is the only way the record answers "what
    /// was waived to get here". A bare "the gates allowed it" is true of every merge that ever happened,
    /// including the one under investigation.
    /// </summary>
    [Fact]
    public async Task MergedEvent_RecordsWhatEachGateHadEstablished()
    {
        var h = new Harness();
        var queue = h.Build(withChangedGate: true, withFlaggedGate: true);

        // A branch that rewrote its own verification command, and a human who waived it.
        h.ChangedGate.SetFlagged(
            AgentId, ChangedTestCommandGate.TestCommandItem, changed: true,
            new ChangedTestCommandGate.CommandDrift(".mainguard/verify", "npm test", "exit 0"));
        h.FlaggedGate.StoreFor(AgentId).SetFlagged(Array.Empty<FlaggedChange>());
        await queue.RunVerificationAsync(AgentId, CancellationToken.None);
        h.ChangedGate.Acknowledge(AgentId, "owner@example");

        Assert.True(queue.TryConfirmHumanMerge(AgentId, "main-sha-1", MainSha, out _));

        var merged = Assert.Single(h.Audit.Read(), e => e.Type == MergeQueue.MergedEvent);
        var gates = merged.Fields["gates"];
        Assert.Contains("changed-test-command: test command changed vs main — acknowledged", gates, StringComparison.Ordinal);
        Assert.Contains("flagged-change review: no flagged items", gates, StringComparison.Ordinal);
    }

    /// <summary>
    /// A queue with no gates says so, rather than reporting the empty string as though every gate had
    /// been consulted and had nothing to object to.
    /// </summary>
    [Fact]
    public async Task MergedEvent_WithNoGatesWired_SaysSo()
    {
        var h = new Harness();
        var queue = h.Build();
        await queue.RunVerificationAsync(AgentId, CancellationToken.None);
        Assert.True(queue.TryConfirmHumanMerge(AgentId, "main-sha-1", MainSha, out _));

        var merged = Assert.Single(h.Audit.Read(), e => e.Type == MergeQueue.MergedEvent);
        Assert.Equal("no gates wired", merged.Fields["gates"]);
    }

    /// <summary>
    /// A merge that was never attributed says "unknown"/"unattributed" instead of borrowing a name. The
    /// legacy overloads and every test double land here, and an audit chain that quietly assigned them an
    /// actor would be lying in precisely the places nobody looks.
    /// </summary>
    [Fact]
    public async Task UnattributedConfirm_IsRecordedAsUnattributed()
    {
        var h = new Harness();
        var queue = h.Build();
        await queue.RunVerificationAsync(AgentId, CancellationToken.None);
        queue.ConfirmHumanMerge(AgentId, "main-sha-1");

        var merged = Assert.Single(h.Audit.Read(), e => e.Type == MergeQueue.MergedEvent);
        Assert.Equal("unknown", merged.Fields["by"]);
        Assert.Equal(MergeAuthorization.UnattributedSource, merged.Fields["source"]);
    }

    /// <summary>
    /// Two merges in a repo produce two records, one per merge. The chain is the history of an operation
    /// and not a "last merge" cell.
    /// </summary>
    [Fact]
    public async Task EachMerge_AppendsItsOwnRecord()
    {
        var h = new Harness();
        var queue = h.Build();
        await queue.RunVerificationAsync(AgentId, CancellationToken.None);
        Assert.True(queue.TryConfirmHumanMerge(AgentId, "main-sha-1", MainSha, out _));

        await queue.RunVerificationAsync("loom-2", CancellationToken.None);
        Assert.True(queue.TryConfirmHumanMerge("loom-2", "main-sha-2", "main-sha-1", out _));

        var merges = h.Audit.Read().Where(e => e.Type == MergeQueue.MergedEvent).ToList();
        Assert.Equal(2, merges.Count);
        Assert.Equal(new[] { AgentId, "loom-2" }, merges.Select(m => m.Fields["agent"]));
        Assert.Equal(new[] { MainSha, "main-sha-1" }, merges.Select(m => m.Fields["pre_main_sha"]));
    }

    // ---- harness ---------------------------------------------------------

    private sealed class Harness
    {
        public InMemoryAuditLog Audit = new();
        public ChangedTestCommandGate ChangedGate = new();
        public FlaggedChangeGate FlaggedGate = new();

        // Persisted across Build() calls, so a second queue over them is a genuine daemon restart and not
        // a differently-configured fresh queue.
        private readonly InMemoryMergeQueueStore _states = new();
        private InMemoryVerificationStore _verifications = new();
        private long _tick;

        public MergeQueue Build(
            bool withChangedGate = false, bool withFlaggedGate = false, bool freshVerificationStore = false)
        {
            if (freshVerificationStore)
            {
                _verifications = new InMemoryVerificationStore();
            }

            MergeQueue queue = null!;
            Task<VerificationRecord> Run(string id, CancellationToken ct) =>
                Task.FromResult(new VerificationRecord(
                    id, queue.CurrentMainSha, Passed: true, "log.txt", "npm test", "confighash",
                    DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref _tick))));

            var gates = new List<IMergeGate>();
            if (withChangedGate) { gates.Add(ChangedGate); }
            if (withFlaggedGate) { gates.Add(FlaggedGate); }

            queue = new MergeQueue(
                "repo", MainSha, _states, _verifications,
                Run, requeue: (_, _) => Task.CompletedTask, gates: gates, audit: Audit);
            return queue;
        }
    }
}

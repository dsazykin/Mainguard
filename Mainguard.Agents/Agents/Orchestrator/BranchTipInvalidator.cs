using System;
using System.Threading;
using Mainguard.Agents.Agents;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The daemon's missing caller for <see cref="MergeQueue.NotifyBranchAdvanced"/>: it subscribes to the ref
/// watcher's own sweep and tells the repo's queue that an agent's branch moved.
///
/// <h3>The defect it exists to end</h3>
///
/// <para><c>Verified</c> was a trap door for a locally-spawned agent. <c>MergeQueue.NotifyNewCommits</c>
/// had exactly two callers — <c>ExternalPrIntake</c>, when an upstream PR head moves, and the dev queue
/// seeder — and neither of them fires for a worker in a jail. So a green entry stayed green through every
/// commit the agent pushed afterwards. Nothing re-verified (<see cref="WorkerReadinessTrigger"/> starts runs
/// only from <c>Working</c>/<c>StaleVerified</c>/<c>VerificationFailed</c>), the human Verify button was
/// still offered and threw <c>Verified → Verifying</c> on every press, and the flagged-change gate — armed
/// only inside a verification — kept holding the classification and the acknowledgments of a diff two
/// commits old. Observed live on 2026-08-30: agent <c>4c43d17a</c> verified at 01:35, committed again at
/// 01:41, 01:59 and 02:13, and the review cockpit still read "verified", footer "ready to merge", Merge
/// enabled, for a tip carrying an out-of-scope change and arithmetic that fails the repo's own tests.</para>
///
/// <h3>Why it is its own type and not a branch of the readiness trigger</h3>
///
/// <list type="bullet">
///   <item><b>Different clock.</b> The trigger DEBOUNCES — it waits out
///   <see cref="CoordinatorLimits.AutoVerifyQuietPeriod"/> so five commits cost one test run. Invalidation
///   must be immediate: the whole window in which a human can click Merge on evidence that has just gone
///   void is the window the debounce would deliberately hold open.</item>
///   <item><b>Different job.</b> The trigger's contract is that it is a caller and never a decider — "two
///   paths that can disagree about what verified means is the defect this codebase keeps producing". It
///   answers <i>when to verify</i>. Making it also move merge state would make it answer <i>what a state
///   means</i>, which is the queue's job; this type does not decide either, it forwards an observation the
///   queue turns into a transition.</item>
///   <item><b>Different failure if absent.</b> A missing trigger costs automation. A missing invalidator
///   costs correctness — which is why <see cref="MergeQueue.CanMerge"/> ALSO compares the record's
///   <see cref="VerificationRecord.BranchSha"/> against the tip it knows, and refuses on drift without
///   needing this type to have fired at all.</item>
/// </list>
///
/// <h3>What it does not do</h3>
///
/// <para>It resolves queues, never creates them (<see cref="IMergeQueueRegistry.Resolve"/>): a branch
/// moving must not provision a repo. It never verifies, never merges, and holds no state of its own — the
/// tip it forwards is remembered by the queue, where the merge gate can read it. A repo with no live queue
/// is a no-op, not an error.</para>
///
/// <para>Subscribing in the constructor is what makes "this object exists" and "this object is wired" the
/// same fact — the same posture, for the same reason, as <see cref="WorkerReadinessTrigger"/>. There is no
/// Start() to forget.</para>
/// </summary>
public sealed class BranchTipInvalidator : IDisposable
{
    private readonly AgentRefWatcher _source;
    private readonly IMergeQueueRegistry _queues;
    private readonly Action<string>? _log;
    private int _disposed;

    /// <param name="source">The ref watcher whose sweep discovers that an agent's branch moved. Held (and
    /// exposed as <see cref="Source"/>) so the composition root can assert it is the SAME watcher the
    /// daemon runs — a subscription to a second, unswept instance is the "implemented, tested, wired
    /// nowhere" defect this repository keeps producing.</param>
    /// <param name="queues">Resolves the repo's live queue. Read-only on purpose.</param>
    /// <param name="log">Optional milestone sink; one line per verification actually invalidated.</param>
    public BranchTipInvalidator(
        AgentRefWatcher source, IMergeQueueRegistry queues, Action<string>? log = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
        _log = log;
        _source.Advanced += OnAdvanced;
    }

    /// <summary>The watcher this invalidator observes (see the constructor note on why it is exposed).</summary>
    public AgentRefWatcher Source => _source;

    private void OnAdvanced(AgentRefPublishResult result) =>
        NotifyAdvanced(result.RepoHash, result.AgentId, result.NewSha ?? string.Empty);

    /// <summary>
    /// Forwards one observed advance to the repo's queue. Public so the signal can be driven in a test
    /// without a real git mirror; production reaches it through <see cref="AgentRefWatcher.Advanced"/>.
    /// </summary>
    /// <returns>True when the queue invalidated a verification because of this advance.</returns>
    public bool NotifyAdvanced(string repoHash, string agentId, string newSha)
    {
        if (string.IsNullOrWhiteSpace(repoHash) || string.IsNullOrWhiteSpace(agentId)
            || string.IsNullOrWhiteSpace(newSha))
        {
            return false;
        }

        var context = _queues.Resolve(repoHash);
        if (context is null)
        {
            return false;
        }

        bool invalidated;
        try
        {
            invalidated = context.Queue.NotifyBranchAdvanced(agentId, newSha);
        }
        catch (Exception ex)
        {
            // Same posture as every other subscriber on this event: an invalidator must never be the thing
            // that stops the sweep. The watcher swallows a throwing subscriber anyway; catching here is
            // what lets the reason be SAID, because a silently-dropped invalidation is a Verified row that
            // freezes exactly the way this type exists to prevent.
            _log?.Invoke(
                $"branch-tip invalidation FAILED repo={repoHash} agent={agentId} tip={Short(newSha)}: "
                + $"{ex.Message} — the entry may still be holding a verification of an older tip");
            return false;
        }

        if (invalidated)
        {
            _log?.Invoke(
                $"branch-tip invalidation repo={repoHash} agent={agentId} tip={Short(newSha)} — the branch "
                + "moved past its own verification; the entry is back on Working and is no longer mergeable");
        }

        return invalidated;
    }

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];

    /// <summary>Unsubscribes. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _source.Advanced -= OnAdvanced;
    }
}

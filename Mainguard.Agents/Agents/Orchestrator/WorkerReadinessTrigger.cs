using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>What one sweep decided about one armed worker.</summary>
public enum ReadinessOutcome
{
    /// <summary>The worker is still pushing — the quiet period has not elapsed. Stays armed.</summary>
    Quiescing,

    /// <summary>Ready, but not now: the cooldown has not expired, or a verification is already running for
    /// this entry. Stays armed and is re-examined on the next sweep.</summary>
    Deferred,

    /// <summary>Dropped for this tip: no approved plan, no queue, or a merge state from which verification
    /// is not a legal transition. Re-arms if the worker pushes again.</summary>
    Ineligible,

    /// <summary>
    /// This exact tip has already had its automatic ATTEMPT — whether that attempt produced a verdict or
    /// was refused before anything ran. Deliberately not named "AlreadyVerified": a refusal (no jail, a
    /// drifted branch, a missing toolchain, a malformed verify command) is not a verification, and calling
    /// it one here would be the same conflation the queue is careful to avoid downstream.
    /// </summary>
    AlreadyAttempted,

    /// <summary><see cref="MergeQueue.RunVerificationAsync"/> was called for this worker.</summary>
    Fired,
}

/// <summary>One sweep's decision about one worker, with the reason spelled out for a log or a test.</summary>
public sealed record ReadinessDecision(
    string RepoHash, string AgentId, string Sha, ReadinessOutcome Outcome, string Reason);

/// <summary>
/// Phase 2's <b>automatic</b> verification trigger: it notices that a delegated worker's branch has stopped
/// advancing and calls <see cref="MergeQueue.RunVerificationAsync"/> — the same method the Verify button,
/// the restart resume and the stale cascade call.
///
/// <para><b>It is a trigger, not a verification path.</b> It contains no gate, runs nothing in a jail, and
/// moves no state. Every gate, every jail execution and every transition stays exactly where it already
/// is, inside the queue and its provisioner. Two paths that can disagree about what "verified" means is the
/// defect this codebase keeps producing (<c>docs/design/verification-trigger.md</c>), so the whole of this
/// type is the answer to <i>when</i>, and none of it is an answer to <i>what</i>.</para>
///
/// <h3>What counts as "ready", and why it is ref quiescence</h3>
///
/// <para><b>Ref movement is the only signal the daemon OBSERVES.</b> The other two candidates are claims.
/// The worker declaring itself done over IPC is an agent-authored assertion, and phase 3 already occupies
/// that slot from the other end — its <c>verify</c> shim op is the coordinator <i>proposing</i> readiness
/// (contract §3), landing on this same queue method. A worker-side twin built here would be a second
/// declaration path competing with it, and it would still leave the case this exists for: nobody, human or
/// agent, remembering to ask.</para>
///
/// <para><b>CLI process exit is not merely weaker — it structurally cannot work.</b> Verification runs in the
/// worker's own jail and host execution is a rejection trigger (§3.2), so a verification triggered by the
/// agent's exit races the teardown that stops that jail and deletes <c>agent/&lt;id&gt;</c> from the mirror
/// (<see cref="AgentContext"/>). Exit also conflates completion with a crash, a budget kill and a human
/// Stop, and would verify the half-finished branch of every one of them.</para>
///
/// <para><b>Movement means "something changed", not "this is ready" — which is exactly why the trigger is
/// quiescence rather than movement.</b> Phase 1 rejected ref movement for two reasons and both were right:
/// it would verify every intermediate commit, and it would hand an agent an unbounded verification loop.
/// Neither survives the debounce and the two bounds below, and phase 2 supplies the third thing phase 1 did
/// not have — an authorization predicate (<see cref="WorkerPlanGate.MayAutoVerify"/>) that says which agents
/// are eligible at all.</para>
///
/// <list type="number">
///   <item><b>Quiescence, not movement.</b> Each observed advance restarts the window. Five commits inside
///   the window are one verification, not five.</item>
///   <item><b>Once per tip.</b> A sha this trigger has already attempted is never attempted again, so
///   producing N automatic runs costs the agent N distinct commits of real work.</item>
///   <item><b>A cooldown per worker.</b> Bursts are bounded by (1) and (2); a steady grinder committing just
///   under the window is bounded by <see cref="CoordinatorLimits.AutoVerifyCooldown"/>.</item>
/// </list>
///
/// <para><b>The residual cost is one wasted test run, and it is worth stating plainly rather than hiding.</b>
/// A worker that pauses longer than the quiet period mid-task gets verified early. The resulting record is
/// not a lie — a <see cref="VerificationRecord"/> says the repo's test command passed against that tip on
/// that main, and it never claimed the worker was finished — and human gate #2 still reads the actual diff
/// before anything merges. What it costs is one suite execution, which is why the window is a limit in
/// <see cref="CoordinatorLimits"/> rather than a constant here.</para>
///
/// <h3>What it refuses to do</h3>
///
/// <list type="bullet">
///   <item><b>It never fights the in-flight guard.</b> <see cref="MergeQueue.RunVerificationAsync"/> throws
///   when a run is already up for an entry. This asks <see cref="MergeQueue.IsVerificationInFlight"/> first
///   and DEFERS (staying armed) rather than calling — and, because that is a check-then-act across a lock it
///   does not hold, it also treats the throw itself as a deferral rather than as an error.</item>
///   <item><b>It never fires for an unapproved plan</b>, and never for an agent the plan gate does not hold
///   at all — a manual-mode agent or an external-PR head keeps the human Verify button and costs no suite
///   runs. See <see cref="WorkerPlanGate.MayAutoVerify"/> for why that is stricter than the merge gate's
///   own default.</item>
///   <item><b>It never turns a refusal into a result.</b> A throw out of the run means the verification was
///   refused before it produced a verdict; the queue writes no record and settles the entry back to
///   <c>Working</c>, and this type logs and stops. See <see cref="RunAsync"/>.</item>
///   <item><b>It only fires from a state where <c>Verifying</c> is a legal transition</b> —
///   <c>Working</c> or <c>StaleVerified</c>. In particular a worker that pushes while its entry sits at
///   <c>Verified</c> is NOT auto-verified: the state machine has no <c>Verified → Verifying</c> edge, and
///   inventing one (or calling <c>NotifyNewCommits</c> from here to walk it back to <c>Working</c>) would be
///   this trigger changing the merge spine's behaviour instead of triggering it. The human button has the
///   same limitation today; it is recorded in <c>docs/design/verification-trigger.md</c> rather than
///   quietly worked around.</item>
/// </list>
///
/// <para><see cref="PollOnce"/> is public and does one complete sweep against an injectable clock, so every
/// timing rule above is testable without sleeping — the same posture, for the same reason, as
/// <see cref="AgentRefWatcher.PollOnce"/>. Build with <see cref="DriveManually"/> to suppress the background
/// loop entirely so a hand-cranked sweep is the only mover.</para>
/// </summary>
public sealed class WorkerReadinessTrigger : IDisposable
{
    /// <summary>How often the loop re-examines armed workers. This is a dictionary scan against a clock —
    /// no git, no I/O — so it is cheap enough to be finer-grained than the quiet period it measures.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(5);

    /// <summary>Sweep sentinel: run NO background loop; the caller drives <see cref="PollOnce"/> itself.
    /// Same contract, and same warning, as <see cref="AgentRefWatcher.DriveManually"/> — a hand-cranked
    /// sweep racing the loop consumes the other's work and turns an assertion into a coin flip.</summary>
    public static readonly TimeSpan DriveManually = Timeout.InfiniteTimeSpan;

    private sealed record Pending(string Sha, DateTimeOffset At);

    private readonly object _gate = new();
    private readonly Dictionary<(string RepoHash, string AgentId), Pending> _armed = new();
    private readonly Dictionary<(string RepoHash, string AgentId), string> _lastFiredSha = new();
    private readonly Dictionary<(string RepoHash, string AgentId), DateTimeOffset> _lastFiredAt = new();
    private readonly HashSet<(string RepoHash, string AgentId)> _running = new();

    private readonly AgentRefWatcher _source;
    private readonly IMergeQueueRegistry _queues;
    private readonly WorkerPlanGate _planGate;
    private readonly CoordinatorLimits _limits;
    private readonly TimeSpan _sweepInterval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _loopGate = new();
    private Task? _loop;
    private int _disposed;

    /// <param name="source">The ref watcher whose sweep discovers that an agent's branch moved. Held (and
    /// exposed as <see cref="Source"/>) so the composition root can be asserted against the SAME watcher the
    /// daemon's worktree manager runs — a trigger subscribed to a second, unwatched instance would be
    /// exactly the "implemented, tested, wired nowhere" defect this repository keeps producing.</param>
    /// <param name="queues">Resolves the repo's live <see cref="MergeQueue"/>. Read-only on purpose: a
    /// trigger that could CREATE a queue would be provisioning repos as a side effect of an agent pushing.</param>
    /// <param name="planGate">The one authority on whether a worker's plan was approved.</param>
    /// <param name="limits">The daemon-side caps; the quiet period and cooldown live there, not here.</param>
    /// <param name="sweepInterval">Loop period, or <see cref="DriveManually"/> for no background loop.</param>
    /// <param name="clock">Injectable clock — every timing rule here is asserted against it rather than slept on.</param>
    /// <param name="log">Optional milestone sink; one line per fire and per refusal worth a human's attention.</param>
    public WorkerReadinessTrigger(
        AgentRefWatcher source,
        IMergeQueueRegistry queues,
        WorkerPlanGate planGate,
        CoordinatorLimits? limits = null,
        TimeSpan? sweepInterval = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
        _planGate = planGate ?? throw new ArgumentNullException(nameof(planGate));
        _limits = limits ?? new CoordinatorLimits();
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;

        // Subscribing in the constructor is what makes "this object exists" and "this object is wired"
        // the same fact. There is no Start() to forget.
        _source.Advanced += OnAdvanced;
        EnsureLoopRunning();
    }

    /// <summary>The watcher this trigger observes (see the constructor note on why it is exposed).</summary>
    public AgentRefWatcher Source => _source;

    /// <summary>
    /// The most recent verification this trigger started — same posture as
    /// <see cref="MergeQueue.LastCascade"/>: tests await it, production fires and forgets. A completed
    /// no-op until something fires, so a caller can always await it.
    /// </summary>
    public Task LastRun { get; private set; } = Task.CompletedTask;

    /// <summary>The workers currently armed (their branch moved and the quiet period is still running).</summary>
    public IReadOnlyCollection<(string RepoHash, string AgentId)> Armed
    {
        get
        {
            lock (_gate)
            {
                return new List<(string, string)>(_armed.Keys);
            }
        }
    }

    /// <summary>
    /// Arms (or re-arms) a worker because its branch advanced to <paramref name="newSha"/>. Public so the
    /// signal can be driven in a test without a real git mirror; production reaches it through
    /// <see cref="AgentRefWatcher.Advanced"/>.
    /// </summary>
    public void NotifyAdvanced(string repoHash, string agentId, string newSha)
    {
        if (string.IsNullOrWhiteSpace(repoHash) || string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        lock (_gate)
        {
            // The timestamp is REPLACED, not kept: the window measures silence since the last commit, so a
            // worker that keeps pushing keeps pushing the deadline out. Keeping the first timestamp would
            // make this fire mid-burst, which is the per-commit behaviour the debounce exists to avoid.
            _armed[(repoHash, agentId)] = new Pending(newSha ?? string.Empty, _clock());
        }
    }

    private void OnAdvanced(AgentRefPublishResult result) =>
        NotifyAdvanced(result.RepoHash, result.AgentId, result.NewSha ?? string.Empty);

    /// <summary>
    /// One complete sweep: decide what to do with every armed worker, and start the verifications that are
    /// due. Returns one <see cref="ReadinessDecision"/> per armed worker examined — including the ones that
    /// were left alone, because "why did this NOT fire" is the question a stuck queue actually asks.
    /// </summary>
    public IReadOnlyList<ReadinessDecision> PollOnce()
    {
        var now = _clock();
        List<((string RepoHash, string AgentId) Key, Pending State)> due = new();
        var decisions = new List<ReadinessDecision>();

        lock (_gate)
        {
            foreach (var (key, state) in _armed)
            {
                if (now - state.At < _limits.AutoVerifyQuietPeriod)
                {
                    decisions.Add(new ReadinessDecision(
                        key.RepoHash, key.AgentId, state.Sha, ReadinessOutcome.Quiescing,
                        "the branch is still advancing — the quiet period has not elapsed"));
                    continue;
                }

                due.Add((key, state));
            }
        }

        foreach (var (key, state) in due)
        {
            decisions.Add(Evaluate(key, state, now));
        }

        return decisions;
    }

    private ReadinessDecision Evaluate((string RepoHash, string AgentId) key, Pending state, DateTimeOffset now)
    {
        ReadinessDecision Drop(ReadinessOutcome outcome, string reason)
        {
            lock (_gate)
            {
                // Only disarm if nothing re-armed us in the meantime (the worker pushed again while this
                // sweep was deciding). Dropping a fresher arm would silently lose the newer tip.
                if (_armed.TryGetValue(key, out var current) && ReferenceEquals(current, state))
                {
                    _armed.Remove(key);
                }
            }

            return new ReadinessDecision(key.RepoHash, key.AgentId, state.Sha, outcome, reason);
        }

        ReadinessDecision Keep(string reason) =>
            new(key.RepoHash, key.AgentId, state.Sha, ReadinessOutcome.Deferred, reason);

        // 1. Already answered for this exact tip. Checked first so a re-armed-then-unchanged entry costs
        //    nothing else, and so the "once per tip" bound cannot be reached around by any later branch.
        lock (_gate)
        {
            if (_lastFiredSha.TryGetValue(key, out var fired)
                && string.Equals(fired, state.Sha, StringComparison.Ordinal)
                && state.Sha.Length > 0)
            {
                return Drop(ReadinessOutcome.AlreadyAttempted,
                    $"tip {Short(state.Sha)} has already had its automatic verification attempt");
            }

            if (_running.Contains(key))
            {
                return Keep("a verification this trigger started is still running");
            }

            if (_lastFiredAt.TryGetValue(key, out var last) && now - last < _limits.AutoVerifyCooldown)
            {
                return Keep(
                    $"the automatic-verification cooldown has {Math.Round((_limits.AutoVerifyCooldown - (now - last)).TotalSeconds)}s left");
            }
        }

        // 2. Authorization. Never for a worker whose plan was not approved, and never for an agent the plan
        //    gate does not hold at all.
        if (!_planGate.MayAutoVerify(key.AgentId, out var planReason))
        {
            return Drop(ReadinessOutcome.Ineligible, planReason);
        }

        // 3. The repo has to have a live queue. Resolve only — never create one.
        var context = _queues.Resolve(key.RepoHash);
        if (context is null)
        {
            return Drop(ReadinessOutcome.Ineligible, $"repo '{key.RepoHash}' has no active merge queue");
        }

        // 4. Do not fight the in-flight guard. This is a check-then-act (the queue takes its own lock), which
        //    is why the throw is ALSO handled as a deferral below rather than as a failure.
        //
        //    It is asked BEFORE the state check below and that order is load-bearing, not stylistic. A live
        //    run means the entry IS Verifying, so asking about the state first answered "Ineligible" for
        //    every in-flight entry and this branch was unreachable — dead code standing in for the one
        //    interaction that matters here. The test that caught it is
        //    AVerificationAlreadyInFlight_IsDeferred_NotFoughtOver, and it fails with "Ineligible" if the
        //    two are ever swapped back.
        var queue = context.Queue;
        if (queue.IsVerificationInFlight(key.AgentId))
        {
            return Keep("a verification is already running for this entry");
        }

        // 5. …and the entry has to be in a state verification can legally start from. Reaching here with a
        //    Verifying state means a row that SAYS verifying with no run behind it — the shape a daemon
        //    restart leaves. That belongs to ResumeAfterRestartAsync and to the human's Clear control, not
        //    to a trigger that would be guessing.
        //
        //    VerificationFailed is in the set, and it has to be. Nothing in the daemon calls
        //    MergeQueue.NotifyNewCommits for a locally-spawned agent, so a push does not walk an entry back
        //    to Working on its own — which means that without this arm, H2's new state would be a state a
        //    worker's own fix could never get it out of: the entry would sit red forever while the agent
        //    committed the repair, and only a human clicking Verify would ever find out. The trigger's
        //    existing bounds do the containment: once-per-tip means this fires only for a tip that has
        //    never been attempted, i.e. only for work the agent has actually pushed SINCE the failure, and
        //    the cooldown bounds a grinder. (A Verified entry is still not re-fired on a push — the state
        //    machine has no Verified → Verifying edge, and inventing one here would be this trigger
        //    changing the merge spine rather than triggering it. That limitation is unchanged and is
        //    recorded in docs/design/verification-trigger.md.)
        var merge = queue.GetState(key.AgentId);
        if (merge is not (WorkerMergeState.Working or WorkerMergeState.StaleVerified
            or WorkerMergeState.VerificationFailed))
        {
            return Drop(ReadinessOutcome.Ineligible,
                $"the queue entry is {merge} — automatic verification starts only from Working, "
                + "StaleVerified or VerificationFailed");
        }

        lock (_gate)
        {
            // Mark attempted BEFORE the run starts. A second sweep overlapping the async handoff would
            // otherwise pass every check above and fire the same tip twice.
            _lastFiredSha[key] = state.Sha;
            _lastFiredAt[key] = now;
            _running.Add(key);
            if (_armed.TryGetValue(key, out var current) && ReferenceEquals(current, state))
            {
                _armed.Remove(key);
            }
        }

        _log?.Invoke(
            $"auto-verify repo={key.RepoHash} agent={key.AgentId} tip={Short(state.Sha)} — the worker's branch "
            + $"has been quiet for {_limits.AutoVerifyQuietSeconds}s and its plan is approved; verifying");

        LastRun = RunAsync(key, queue);
        return new ReadinessDecision(
            key.RepoHash, key.AgentId, state.Sha, ReadinessOutcome.Fired, "verification started");
    }

    private async Task RunAsync((string RepoHash, string AgentId) key, MergeQueue queue)
    {
        try
        {
            // THE one call. Everything about what a verification is — the Verifying transition, the jail
            // execution, the gates armed from the committed trees, the immutable record, the settle to
            // Verified or to VerificationFailed — belongs to this method and to nothing here.
            var record = await queue.RunVerificationAsync(key.AgentId, _stop.Token).ConfigureAwait(false);

            // H3 — the OUTCOME, logged. This method announced "verifying" and then said nothing at all,
            // whichever way the run went: a red verification existed only as a row in SQLite and an
            // artifact file nothing linked to, so the daemon log — the first place anyone looks when a
            // swarm has gone quiet — recorded that six test runs were STARTED and not one of them
            // finished. An automatic actor that spends a test suite on a human's behalf and does not say
            // what it found is indistinguishable from one that hung.
            //
            // The verdict word comes from record.Passed, which is the daemon-observed container-runtime
            // exit (OPS SA-1) and the same field the queue decided the state from — never a second
            // opinion — and the line carries the artifact path so the output is one `cat` away rather
            // than a directory to guess at. The resulting state is included because "PASSED" and
            // "Verified" are not the same claim: a pass whose entry was discarded mid-run settles
            // nowhere, and a log that asserted the transition would be wrong exactly then.
            _log?.Invoke(
                $"auto-verify repo={key.RepoHash} agent={key.AgentId} — the verification "
                + $"{(record.Passed ? "PASSED" : "FAILED")} ({record.ResolvedCommand}) against "
                + $"main@{Short(record.MainSha)}; the entry is now "
                + $"{queue.GetState(key.AgentId)} — output: {record.LogArtifactPath}");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already in flight", StringComparison.Ordinal))
        {
            // Lost the check-then-act race against another caller (the Verify button, the stale cascade,
            // the restart resume). Their run is the run; ours was never needed.
            _log?.Invoke(
                $"auto-verify repo={key.RepoHash} agent={key.AgentId} — stood down, another verification "
                + "was already in flight");
        }
        catch (Exception ex)
        {
            // The throw is REPORTED and translated into nothing. A throw out of RunVerificationAsync means
            // the run was REFUSED before it produced a verdict — no live jail, a drifted branch, a missing
            // toolchain, or a verify command whose shell operators survived tokenisation
            // (MalformedVerificationCommandException, PR #322) — and the queue has already done the only
            // correct thing with it: written NO VerificationRecord and settled the entry back to Working.
            //
            // The one thing a trigger must never do here is finish the job the refusal declined to do. An
            // automatic caller that caught this and marked the entry failed would turn "we could not run
            // your tests" into "your tests failed", which is precisely the distinction the merge decision
            // rests on — and it would do it silently, to a human who never asked for the run. So this
            // branch logs and stops. It cannot do otherwise structurally either: this type holds no
            // verification store and touches no queue state, and
            // ARefusedVerification_RecordsNothing_AndIsNotRetriedOnTheSameTip is what keeps that a claim
            // which can fail rather than an assurance.
            _log?.Invoke(
                $"auto-verify repo={key.RepoHash} agent={key.AgentId} — the verification was REFUSED and did "
                + $"not run; no result was recorded and the entry is back on Working: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(key);
            }
        }
    }

    /// <summary>
    /// Forgets everything about a worker (teardown). Idempotent.
    ///
    /// <para><b>Nothing in the daemon calls this yet, and that is a bounded leak rather than a hidden one.</b>
    /// The armed set is self-clearing — every decision either fires or drops — so what persists is the
    /// once-per-tip sha and the cooldown stamp, two small entries per agent this trigger has ever attempted
    /// in one daemon lifetime. That is the same posture, and the same bound, as <c>AgentRefMediator</c>'s
    /// per-agent publish gates. Losing them would be worse than keeping them: a re-used agent id would get
    /// its cooldown and its dedupe reset.</para>
    /// </summary>
    public void Forget(string repoHash, string agentId)
    {
        var key = (repoHash, agentId);
        lock (_gate)
        {
            _armed.Remove(key);
            _lastFiredSha.Remove(key);
            _lastFiredAt.Remove(key);
        }
    }

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];

    private void EnsureLoopRunning()
    {
        if (_sweepInterval == DriveManually || _stop.IsCancellationRequested)
        {
            return;
        }

        lock (_loopGate)
        {
            if (_loop is not null || _stop.IsCancellationRequested)
            {
                return;
            }

            var token = _stop.Token;
            _loop = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        PollOnce();
                    }
                    catch
                    {
                        // A trigger must never be the thing that takes the daemon down.
                    }

                    try
                    {
                        await Task.Delay(_sweepInterval, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                    {
                        return;
                    }
                }
            });
        }
    }

    /// <summary>Unsubscribes, stops the loop, and waits for the sweep in flight — same reasoning as
    /// <see cref="AgentRefWatcher.Dispose"/>: disposing the token source underneath a parked loop faults it
    /// with the one exception the loop is not written to expect.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _source.Advanced -= OnAdvanced;
        _stop.Cancel();

        Task? loop;
        lock (_loopGate)
        {
            loop = _loop;
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // A cancelled or faulted sweep is not a disposal failure.
        }

        _stop.Dispose();
    }
}

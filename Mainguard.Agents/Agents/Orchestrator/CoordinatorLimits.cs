namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The hard, <b>daemon-side</b> caps on the orchestration loop (coordinator contract §2 / §5).
///
/// <para>Every field here is enforced where the daemon serves the call — never by telling an agent about
/// it. A limit an agent is merely told about is a suggestion: MG-12 found role authorization that
/// <i>looked</i> present and was dead code that failed open, so "the prompt says so" is not a control.
/// Each field below has a test that fails when its check is removed.</para>
///
/// <para>Lifted out of <c>CoordinatorTools.cs</c> in phase 2 because four separate call sites now consume
/// it — the coordinator tool surface, <c>PlanApprovalService</c> (the revision budget), the daemon's
/// wired shim-spawn gate, and the daemon host's composition root.</para>
/// </summary>
/// <param name="MaxActiveWorkers">
/// The box-wide ceiling on live workers. <b>A worker blocked on plan approval counts against this.</b>
/// The cap is a <i>resource</i> cap and a blocked worker still holds its jail, tmpfs, network segment
/// and worktree; exempting blocked workers would let a coordinator spawn unboundedly many
/// resource-consuming workers at exactly the moment a human is too busy to approve. The intended
/// behaviour is therefore <b>backpressure, not deadlock</b>: the coordinator stops spawning until the
/// human clears plans, and the stall says so out loud (<see cref="WorkerPlanGate.BackpressureSignal"/>).
/// </param>
/// <param name="MaxPlanRevisions">
/// How many times a worker may revise and re-present a rejected plan before it stops and escalates to
/// the human. Rejection is feedback, not death — but an unbounded reject→revise loop burns budget and
/// wall-clock forever, so the loop is bounded here rather than in a prompt.
///
/// <para><b>Counting, stated exactly</b> (the contract's prose admits two readings, so the arithmetic is
/// pinned here and unit-pinned by <c>WorkerPlanRevisionLoopTests</c>): the original presentation is
/// revision 0. Rejection <i>n</i> permits revision <i>n</i> while <i>n</i> ≤ this value. With the
/// decided value of 3 that is: reject → revise ×3, and the <b>4th rejection escalates</b> instead of
/// looping. The maximal reading was taken deliberately — "rejection is feedback, not death" argues for
/// giving the plan every permitted round before the worker gives up on it.</para>
/// </param>
/// <param name="AutoVerifyQuietSeconds">
/// How long a worker's <c>refs/heads/agent/&lt;id&gt;</c> must stop advancing before
/// <see cref="WorkerReadinessTrigger"/> reads the branch as ready and verifies it. Every observed advance
/// restarts the window, so a worker that lands five commits over three minutes costs ONE verification
/// rather than five — which is the whole reason the trigger debounces instead of firing per commit.
///
/// <para><b>Tuned toward the cheaper failure.</b> Too short and a mid-task pause is read as completion: the
/// cost is one wasted suite run in the worker's own jail, and a <c>VerificationRecord</c> that is still
/// true (the tests DID pass on that tip — the record never claims the worker was finished). Too long and a
/// finished worker's entry simply sits at <c>Working</c> until someone presses Verify, which is the state
/// this trigger exists to end. The second failure is the one that wastes a human, so the number leans
/// short.</para>
/// </param>
/// <param name="AutoVerifyCooldownSeconds">
/// The floor on the gap between two AUTOMATIC verifications of the same worker. The quiet period bounds a
/// burst; this bounds a grinder — an agent committing steadily just under the quiet period would otherwise
/// run the repo's whole test suite as fast as the suite completes. Nothing here throttles the human Verify
/// button or the stale cascade: a cooldown on a human's explicit request would be a control refusing the
/// person it exists to serve.
/// </param>
/// <param name="MirrorRefreshSeconds">
/// How often the daemon pulls each live queue's mirror main forward from the user's checkout
/// (<c>MergeQueueProvisioner.RefreshMainFromCheckout</c>). Owner decision 2026-09-04: before this the
/// mirror was refreshed only at repo-open, merge-confirm, cascade and reconcile moments, so a pull or a
/// commit made on main outside Mainguard left every queue entry measured against a main that no longer
/// existed until one of those moments happened, with nothing on any surface saying so. One local fetch
/// per repo per minute is cheap; the value is what makes the rail's "refreshed N min ago" a bound.
/// </param>
/// <param name="JailReapSweepSeconds">
/// How often <c>JailReaperHostedService</c> walks every live session and asks <c>JailReapPolicy</c>
/// whether its jail should still exist (owner decision 2026-09-04). A jail whose merge-queue entry is
/// terminal, or that has had no CLI bound to it for <see cref="IdleJailReapMinutes"/>, is stopped through
/// the ordinary Stop path — harvest, publish, teardown — so nothing it committed is lost.
/// </param>
/// <param name="IdleJailReapMinutes">
/// How long a jail may sit with no CLI bound to it (an orphan adopted after a restart whose PTY was not
/// re-attached, a CLI that exited, a spawn whose bind never came) before the reaper stops it. Thirty
/// minutes is long past any startup, and short enough that a laptop does not carry a day's worth of
/// idle 2 GiB jails.
/// </param>
/// <param name="MaxLiveCoordinators">
/// How many coordinators may be live on one daemon at once. <b>One</b> (owner decision, 2026-09-03): the
/// plan gate, the operator's approval cards and the coordinator surface are all built around a single
/// orchestrating agent, and a second one produced cards a human could approve from the wrong repository's
/// window. Enforced by <c>AgentSpawnService.SpawnAsync</c>; adoption after a restart is not a spawn and
/// re-admits whatever was already running. Test rigs that prove cross-coordinator ownership scoping raise
/// it explicitly — that scoping stays as defence in depth.
/// </param>
public sealed record CoordinatorLimits(
    int MaxActiveWorkers = 6,
    int MaxPlanRevisions = 3,
    int AutoVerifyQuietSeconds = 90,
    int AutoVerifyCooldownSeconds = 600,
    int MaxLiveCoordinators = 1,
    int MirrorRefreshSeconds = 60,
    int JailReapSweepSeconds = 60,
    int IdleJailReapMinutes = 30)
{
    /// <summary><see cref="AutoVerifyQuietSeconds"/> as a <see cref="System.TimeSpan"/>.</summary>
    public System.TimeSpan AutoVerifyQuietPeriod => System.TimeSpan.FromSeconds(AutoVerifyQuietSeconds);

    /// <summary><see cref="AutoVerifyCooldownSeconds"/> as a <see cref="System.TimeSpan"/>.</summary>
    public System.TimeSpan AutoVerifyCooldown => System.TimeSpan.FromSeconds(AutoVerifyCooldownSeconds);
}

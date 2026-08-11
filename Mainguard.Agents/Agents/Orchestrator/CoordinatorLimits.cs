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
public sealed record CoordinatorLimits(
    int MaxActiveWorkers = 6,
    int MaxPlanRevisions = 3,
    int AutoVerifyQuietSeconds = 90,
    int AutoVerifyCooldownSeconds = 600)
{
    /// <summary><see cref="AutoVerifyQuietSeconds"/> as a <see cref="System.TimeSpan"/>.</summary>
    public System.TimeSpan AutoVerifyQuietPeriod => System.TimeSpan.FromSeconds(AutoVerifyQuietSeconds);

    /// <summary><see cref="AutoVerifyCooldownSeconds"/> as a <see cref="System.TimeSpan"/>.</summary>
    public System.TimeSpan AutoVerifyCooldown => System.TimeSpan.FromSeconds(AutoVerifyCooldownSeconds);
}

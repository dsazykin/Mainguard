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
public sealed record CoordinatorLimits(int MaxActiveWorkers = 6, int MaxPlanRevisions = 3);

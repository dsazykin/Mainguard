using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The P2-09 orchestrator-side run state for one agent. Deliberately narrower than the design-layer
/// <see cref="AgentLifecycleState"/> (which drives the activity-bar iconography): this is the set the
/// yield/keep-alive/teardown machinery actually transitions through, and it adds the
/// <see cref="Conflict"/> state the keep-alive rebase parks an agent in until a human clears it.
/// </summary>
public enum AgentRunState
{
    /// <summary>The agent's CLI is running normally.</summary>
    Working,

    /// <summary>A cooperative yield has been requested; awaiting the ready ack.</summary>
    Yielding,

    /// <summary>The jail is <c>docker pause</c>d (yield-timeout path) or otherwise held.</summary>
    Paused,

    /// <summary>A keep-alive rebase is committing/replaying against fresh main.</summary>
    Rebasing,

    /// <summary>
    /// A keep-alive rebase hit a conflict: the worktree is parked mid-rebase and the jail is frozen, both
    /// deliberately — there is no automatic <c>rebase --abort</c> (a rejection trigger).
    ///
    /// <para>It used to say "parked for the T-04 resolver", which named a surface that does not exist and
    /// left the state reading as a queue for something that was never coming. T-04 — where a human resolves
    /// hunks against a diff — is still unbuilt. What an entry in this state now HAS is two operations built
    /// from machinery that already ships (<c>MergeQueueService.ResolveConflictWithAgent</c> and
    /// <c>AbortRebase</c>): hand the conflict back to the agent that wrote half of it, or undo the rebase.
    /// Neither is T-04 and neither is a step toward replacing it.</para>
    ///
    /// <para>The jail staying frozen is the point of the state; the machine does NOT keep a claim on that
    /// pause — the cycle settles its yield token on this path (see <c>IYieldToken.ReleaseWithoutResuming</c>),
    /// so the human's own Pause/Unpause control still works on a conflicted agent.</para>
    /// </summary>
    Conflict,

    /// <summary>Teardown is in progress.</summary>
    TearingDown,

    /// <summary>Teardown finished; the agent is gone.</summary>
    TornDown,
}

/// <summary>A teardown/lifecycle notification the client reacts to (e.g. close floating dock windows).</summary>
public sealed record AgentLifecycleEvent(string AgentId, string Kind, string Detail)
{
    /// <summary>The terminal event: the agent is fully torn down; the client closes its dock windows.</summary>
    public const string Terminated = "terminated";

    /// <summary>A warning event: teardown left residue (a worktree or container the verify step still sees).</summary>
    public const string Residue = "residue";
}

/// <summary>
/// The idempotent, failure-tolerant teardown steps for one agent, each injected so the teardown is
/// unit-testable without Docker (the real daemon supplies leader/sandbox/worktree-backed delegates).
/// Every delegate is optional (a null step is skipped) and any thrown error is aggregated — teardown
/// always continues to the next step.
/// </summary>
public sealed record TeardownPlan(
    string AgentId,
    Func<CancellationToken, Task>? KillPty = null,
    Func<CancellationToken, Task>? StopContainer = null,
    Action? RemoveWorktree = null,
    Func<IReadOnlyList<string>>? ResidualWorktrees = null,
    Func<IReadOnlyList<string>>? ResidualContainers = null,
    Action<AgentLifecycleEvent>? Emit = null);

/// <summary>The outcome of one teardown — surfaced to the daemon (and asserted by tests, which fail on residue).</summary>
public sealed record TeardownReport(
    IReadOnlyList<Exception> Errors,
    IReadOnlyList<string> ResidualWorktrees,
    IReadOnlyList<string> ResidualContainers,
    bool EmittedTerminal)
{
    /// <summary>True iff every step succeeded and the verify pass found no worktree/container residue.</summary>
    public bool Clean => Errors.Count == 0 && ResidualWorktrees.Count == 0 && ResidualContainers.Count == 0;
}

/// <summary>
/// P2-09 agent teardown as an <see cref="IDisposable"/> context (contract §2.4). Dispose runs the
/// ordered steps — kill PTY (via the leader) → stop container (the agent, per policy — not the jail
/// image) → <c>RemoveAgentWorktree(force:true)</c> (which also deletes <c>agent/&lt;id&gt;</c> in the
/// mirror) → emit the terminal event → <b>verify clean</b> (no residual worktree/container), surfacing
/// any residue as a warning event. Each step is idempotent and failure-tolerant; errors are aggregated
/// and continue. The last <see cref="TeardownReport"/> is exposed for the daemon/tests.
/// </summary>
public sealed class AgentContext : IDisposable, IAsyncDisposable
{
    private readonly TeardownPlan _plan;
    private int _disposed;

    public AgentContext(TeardownPlan plan)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (string.IsNullOrWhiteSpace(plan.AgentId))
        {
            throw new ArgumentException("A teardown plan needs an agent id.", nameof(plan));
        }
    }

    /// <summary>The agent this context owns.</summary>
    public string AgentId => _plan.AgentId;

    /// <summary>The report from the last (single) teardown, or null before Dispose.</summary>
    public TeardownReport? LastReport { get; private set; }

    /// <summary>Runs teardown once; safe to call repeatedly (subsequent calls return the same report).</summary>
    public async Task<TeardownReport> TeardownAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return LastReport ?? new TeardownReport(Array.Empty<Exception>(), Array.Empty<string>(), Array.Empty<string>(), false);
        }

        var errors = new List<Exception>();

        // 1. Kill the PTY (owned by the session leader).
        await RunStepAsync(errors, () => _plan.KillPty?.Invoke(ct) ?? Task.CompletedTask).ConfigureAwait(false);

        // 2. Stop the container (the agent, per policy — the jail image itself is not removed here).
        await RunStepAsync(errors, () => _plan.StopContainer?.Invoke(ct) ?? Task.CompletedTask).ConfigureAwait(false);

        // 3. Remove the worktree with force and delete agent/<id> in the mirror (WorktreeManager does both).
        RunStep(errors, () => _plan.RemoveWorktree?.Invoke());

        // 4. Emit the terminal event so the client closes any floating dock windows for this agent.
        var emittedTerminal = false;
        try
        {
            _plan.Emit?.Invoke(new AgentLifecycleEvent(AgentId, AgentLifecycleEvent.Terminated, "Agent torn down."));
            emittedTerminal = true;
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }

        // 5. Verify clean — the verification IS part of teardown; residue is surfaced as a warning event.
        var residualWorktrees = Safe(errors, _plan.ResidualWorktrees);
        var residualContainers = Safe(errors, _plan.ResidualContainers);

        if (residualWorktrees.Count > 0 || residualContainers.Count > 0)
        {
            try
            {
                _plan.Emit?.Invoke(new AgentLifecycleEvent(AgentId, AgentLifecycleEvent.Residue,
                    $"Teardown left residue: {residualWorktrees.Count} worktree(s), {residualContainers.Count} container(s)."));
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        LastReport = new TeardownReport(errors, residualWorktrees, residualContainers, emittedTerminal);
        return LastReport;
    }

    public async ValueTask DisposeAsync() => await TeardownAsync().ConfigureAwait(false);

    public void Dispose() => TeardownAsync().GetAwaiter().GetResult();

    private static async Task RunStepAsync(List<Exception> errors, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }

    private static void RunStep(List<Exception> errors, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }

    private static IReadOnlyList<string> Safe(List<Exception> errors, Func<IReadOnlyList<string>>? probe)
    {
        if (probe is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            return probe() ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
            return Array.Empty<string>();
        }
    }
}

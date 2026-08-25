using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>How a yield was achieved: the agent cooperatively acknowledged, or it was force-paused.</summary>
public enum YieldOutcome
{
    /// <summary>The agent answered <c>[IPC_UPDATE_READY]</c> in time — no <c>docker pause</c> needed.</summary>
    ByReady,

    /// <summary>The agent went silent past the timeout — the jail was <c>docker pause</c>d (timeout path).</summary>
    ByPause,
}

/// <summary>
/// The proof-of-quiescence handle returned by <see cref="IYieldProtocol.RequestYieldAsync"/>. While
/// <see cref="IsActive"/> is true the agent is quiesced (cooperatively yielded or paused) and the
/// daemon may mutate the worktree through <see cref="GitMutationGuard.RunGuarded{T}"/> — this token is
/// the <b>only</b> API that gates worktree mutation (invariant 2). <see cref="Resume"/> (and
/// <see cref="IDisposable.Dispose"/>) unpauses the jail / signals the agent to continue; both are
/// idempotent.
/// </summary>
public interface IYieldToken : IDisposable
{
    /// <summary>The agent this token quiesced.</summary>
    string AgentId { get; }

    /// <summary>True until the token is resumed/disposed — the guard requires this.</summary>
    bool IsActive { get; }

    /// <summary>Whether the yield was cooperative (<see cref="YieldOutcome.ByReady"/>) or a forced pause.</summary>
    YieldOutcome Outcome { get; }

    /// <summary>Unpause the jail (pause path) / signal resume; idempotent.</summary>
    void Resume();
}

/// <summary>
/// The dedicated control channel into one agent's container — a named pipe / second channel the
/// adapter wrapper watches, deliberately <b>not</b> the interactive PTY, so the marker strings never
/// race user-visible terminal output (plan §3.1).
/// </summary>
public interface IAgentControlChannel
{
    /// <summary>Writes a control marker (e.g. <c>[IPC_UPDATE_REQUESTED]</c>) toward the agent wrapper.</summary>
    Task SendAsync(string marker, CancellationToken ct = default);

    /// <summary>Awaits <paramref name="marker"/> (e.g. <c>[IPC_UPDATE_READY]</c>) up to <paramref name="timeout"/>; false on timeout.</summary>
    Task<bool> WaitForAsync(string marker, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// The control channel for a daemon that has <b>no cooperative transport bound</b> — which is every
/// daemon shipped so far. It answers "the agent did not acknowledge" <i>immediately</i> rather than after
/// the cooperative window, and that difference is the whole reason it is a named type instead of a
/// missing one.
///
/// <para><b>Why this is honest rather than a stub.</b> The cooperative half of the yield needs a wrapper
/// INSIDE the jail to write <see cref="YieldProtocol.UpdateReady"/> back; nothing in the shipped adapter
/// wrapper does. So the true answer to "did the agent reach a safe point on request" is not "yes" and not
/// "we waited ten seconds and heard nothing" — it is "there is no channel to ask on". Saying so at once
/// takes the <see cref="YieldOutcome.ByPause"/> path immediately: the jail is frozen through the freezer
/// cgroup, which needs no cooperation from the agent, and the worktree is quiescent by force instead of by
/// agreement. The mutation-safety property (invariant 2) is identical on both paths; only the courtesy
/// differs.</para>
///
/// <para><b>What the alternative would have cost.</b> Waiting the full ten seconds per agent for a marker
/// nobody will ever send turns one human merge into a ten-second stall per co-tenant branch before any of
/// them is even rebased — a cascade over five agents would idle for the better part of a minute waiting on
/// a transport that does not exist. This matches
/// <c>SandboxKillTarget.RequestYieldAsync</c>, which already answers false without a round trip for the
/// same reason.</para>
///
/// <para>Replace this with the real named-pipe channel when the wrapper grows one; nothing else in the
/// protocol changes, because the pause fallback stays the backstop for an agent that does not answer.</para>
/// </summary>
public sealed class UnboundAgentControlChannel : IAgentControlChannel
{
    /// <summary>The single instance — it holds no state and names a fact about the daemon, not an agent.</summary>
    public static UnboundAgentControlChannel Instance { get; } = new();

    /// <summary>Nothing is listening; the marker is dropped rather than queued for a reader that will never exist.</summary>
    public Task SendAsync(string marker, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Always false, and always at once — see the type remarks for why the timeout is not waited out.</summary>
    public Task<bool> WaitForAsync(string marker, TimeSpan timeout, CancellationToken ct = default) =>
        Task.FromResult(false);
}

/// <summary>
/// Arbitrates between a HUMAN's sticky per-agent pause and the MACHINE's short pause-for-rebase
/// (the yield's timeout path). Two rules, one ledger:
/// <list type="bullet">
/// <item>a human pause is sticky — the machine may run its critical section through a frozen jail
/// (already quiescent) but must never wake it on resume;</item>
/// <item>a human unpause defers to an in-flight machine hold (an honest, self-clearing refusal) so a
/// click can never break the rebase's pause open mid-write.</item>
/// </list>
/// </summary>
public interface IPauseArbiter
{
    /// <summary>True while a human holds this agent paused (checked by the yield at pause AND resume time).</summary>
    bool IsHumanPaused(string agentId);

    /// <summary>Marks the machine's critical section (pause-rebase-unpause); dispose to release.
    /// While any hold is outstanding for the agent, a human unpause is refused.</summary>
    IDisposable HoldForMachine(string agentId);
}

/// <summary>The cooperative-yield request seam. The single gateway to a mutable worktree.</summary>
public interface IYieldProtocol
{
    /// <summary>
    /// Requests a cooperative yield: sends <c>[IPC_UPDATE_REQUESTED]</c>, awaits
    /// <c>[IPC_UPDATE_READY]</c> ≤ <paramref name="timeout"/> (default 10 s), else <c>docker pause</c>s
    /// the jail. Always returns an active <see cref="IYieldToken"/>.
    /// </summary>
    Task<IYieldToken> RequestYieldAsync(string agentId, TimeSpan? timeout = null, CancellationToken ct = default);
}

/// <summary>
/// P2-09 cooperative yield protocol (contract §2.1). Drives the request/ready handshake on the
/// dedicated control channel and falls back to <see cref="ISandboxEngine.PauseAsync"/> on timeout, then
/// hands the caller a token that is the sole worktree-mutation gateway. The container id for the pause
/// path is resolved lazily (Docker is truth) via the injected resolver.
/// </summary>
public sealed class YieldProtocol : IYieldProtocol
{
    /// <summary>The marker the daemon writes to ask the agent to reach a safe point.</summary>
    public const string UpdateRequested = "[IPC_UPDATE_REQUESTED]";

    /// <summary>The marker the agent wrapper writes back when it is between tool calls.</summary>
    public const string UpdateReady = "[IPC_UPDATE_READY]";

    /// <summary>The default cooperative window before the pause fallback.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string, IAgentControlChannel> _channelFor;
    private readonly ISandboxEngine _sandbox;
    private readonly Func<string, string?> _containerIdFor;
    private readonly IAgentSupervisor _supervisor;
    private readonly TimeSpan _defaultTimeout;
    private readonly IPauseArbiter? _arbiter;

    /// <param name="channelFor">Resolves the dedicated control channel for an agent.</param>
    /// <param name="sandbox">The sandbox engine (pause/unpause on the timeout path).</param>
    /// <param name="containerIdFor">Resolves an agent's live container id (Docker-as-truth); null → no pause possible.</param>
    /// <param name="supervisor">Reflects the yield/pause state in agent metadata (optional).</param>
    /// <param name="defaultTimeout">Overrides the 10 s cooperative window (tests use a short one).</param>
    /// <param name="arbiter">The human-pause ledger (optional; null = no human pause feature, today's
    /// exact behavior). With one bound, the timeout path takes a machine hold for its critical
    /// section, skips the docker-pause when the human already froze the jail, and — decisively — the
    /// resume re-checks the human flag AT RESUME TIME and leaves a human-paused jail frozen.</param>
    public YieldProtocol(
        Func<string, IAgentControlChannel> channelFor,
        ISandboxEngine sandbox,
        Func<string, string?> containerIdFor,
        IAgentSupervisor? supervisor = null,
        TimeSpan? defaultTimeout = null,
        IPauseArbiter? arbiter = null)
    {
        _channelFor = channelFor ?? throw new ArgumentNullException(nameof(channelFor));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _containerIdFor = containerIdFor ?? throw new ArgumentNullException(nameof(containerIdFor));
        _supervisor = supervisor ?? NullAgentSupervisor.Instance;
        _defaultTimeout = defaultTimeout ?? DefaultTimeout;
        _arbiter = arbiter;
    }

    public async Task<IYieldToken> RequestYieldAsync(string agentId, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("agentId is required.", nameof(agentId));
        }

        var window = timeout ?? _defaultTimeout;
        var channel = _channelFor(agentId);

        _supervisor.MarkState(agentId, "Yielding", "Cooperative update requested.");
        await channel.SendAsync(UpdateRequested, ct).ConfigureAwait(false);

        var ready = await channel.WaitForAsync(UpdateReady, window, ct).ConfigureAwait(false);
        if (ready)
        {
            // Cooperative path: the agent is quiescent of its own accord; no pause. Resume just clears state.
            return new YieldToken(agentId, YieldOutcome.ByReady, () =>
            {
                _supervisor.MarkState(agentId, "Working", null);
                return Task.CompletedTask;
            });
        }

        // Timeout path: force-pause the jail BEFORE returning the token (no mutation may precede the pause).
        var containerId = _containerIdFor(agentId)
            ?? throw new InvalidOperationException(
                $"Agent '{agentId}' did not yield and has no live container to pause (Docker reports none).");

        // The machine's critical section is marked in the arbiter FIRST, so a human unpause arriving
        // between here and the token's resume is refused rather than breaking the pause open mid-write.
        var hold = _arbiter?.HoldForMachine(agentId);
        try
        {
            // A human-paused jail is already frozen — pausing again is a no-op at best and an engine
            // error at worst; the rebase's quiescence requirement is already met.
            if (_arbiter?.IsHumanPaused(agentId) != true)
            {
                await _sandbox.PauseAsync(containerId, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            hold?.Dispose();
            throw;
        }

        _supervisor.MarkState(agentId, "Paused", "Yield timed out; jail paused for the update.");

        return new YieldToken(agentId, YieldOutcome.ByPause, async () =>
        {
            try
            {
                // Checked at RESUME time, not capture time: a human who paused the agent DURING the
                // machine's hold has said "stay frozen", and the machine must not override that on its
                // way out. The hold is released either way — the human's own unpause takes over.
                if (_arbiter?.IsHumanPaused(agentId) == true)
                {
                    _supervisor.MarkState(agentId, "Paused", "Paused by you.");
                    return;
                }

                await _sandbox.UnpauseAsync(containerId, CancellationToken.None).ConfigureAwait(false);
                _supervisor.ResumeInput(agentId);
                _supervisor.MarkState(agentId, "Working", null);
            }
            finally
            {
                hold?.Dispose();
            }
        });
    }

    /// <summary>The active-until-resumed token. <see cref="Resume"/> runs the resume action exactly once.</summary>
    private sealed class YieldToken : IYieldToken
    {
        private readonly Func<Task> _resumeAsync;
        private int _resumed;

        public YieldToken(string agentId, YieldOutcome outcome, Func<Task> resumeAsync)
        {
            AgentId = agentId;
            Outcome = outcome;
            _resumeAsync = resumeAsync;
        }

        public string AgentId { get; }

        public YieldOutcome Outcome { get; }

        public bool IsActive => Volatile.Read(ref _resumed) == 0;

        public void Resume()
        {
            if (Interlocked.Exchange(ref _resumed, 1) != 0)
            {
                return;
            }

            _resumeAsync().GetAwaiter().GetResult();
        }

        public void Dispose() => Resume();
    }
}

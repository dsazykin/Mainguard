using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.Terminal;
using Mainguard.Git.Audit;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mainguard.Server.Runtime;

/// <summary>What the binder needs to start one CLI under a TTY inside a live jail.</summary>
public sealed record AgentCliLaunchSpec(
    string AgentId, string RepoHash, string ContainerId, IReadOnlyList<string> Launch);

/// <summary>
/// The complete, PTY-ready launch plan for one in-jail CLI — the exact command/argv/environment/size
/// the daemon-side PTY spawn consumes. Pure data, computed by <see cref="AgentCliBinder.BuildPtyLaunch"/>
/// so tests can assert the TTY-relevant bits (interactive <c>-i -t</c> exec, sane <c>TERM</c>,
/// positive dimensions) without spawning anything.
/// </summary>
public sealed record CliPtyLaunch(
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Environment,
    int Cols,
    int Rows);

/// <summary>
/// The outcome of one <c>send_worker_prompt</c> delivery, split into the two facts the daemon can
/// actually distinguish — because the defect this type replaces was a <c>bool</c> that conflated them.
/// </summary>
/// <param name="Submitted">
/// The text was written to the worker's PTY without error, and the CR that submits it was written after
/// it as a separate keystroke.
/// </param>
/// <param name="Echoed">
/// The CLI produced output after the body and before the CR, within
/// <see cref="AgentCliBinder.PromptEchoWindow"/>. It had therefore already read the body, which is what
/// makes the CR a keystroke of its own instead of the tail of a paste — so this is the daemon's runtime
/// detector for a J2 regression, and it is strictly more than "the write returned".
/// </param>
/// <param name="Reacted">
/// The CLI produced output within <see cref="AgentCliBinder.PromptReactionWindow"/> of the CR.
/// </param>
/// <param name="Refusal">Set only when <paramref name="Submitted"/> is false and the reason is known
/// more precisely than "no live CLI" — the sentence the coordinator is shown.</param>
/// <remarks>
/// <b>Neither observation is proof, and the status the coordinator is shown must not read as one.</b>
/// Each says the child read its PTY and re-rendered — which a CLI that never saw the keystroke cannot
/// do — but a CLI already mid-turn emits output continuously and would satisfy both without reading
/// anything. Their evidential weight is in the negative direction: an idle CLI that produces nothing at
/// all after a keystroke did not see one. The ground truth that a prompt became a TURN lives in the
/// CLI's own transcript inside the jail, which the daemon deliberately does not read. Report; never
/// assert. (That rule was already written here and then broken one layer up: the status sentence said
/// "Enter was pressed and its CLI redrew in response", which a coordinator reasonably read as
/// confirmation and which the redraw cannot support — defect J3.)
/// </remarks>
internal readonly record struct PromptDelivery(
    bool Submitted, bool Echoed, bool Reacted, string? Refusal)
{
    /// <summary>Nothing was written: no bound CLI, or the PTY refused the write.</summary>
    public static PromptDelivery NotDelivered => new(false, false, false, null);

    /// <summary>Nothing was written, and the daemon can say exactly why.</summary>
    public static PromptDelivery Refused(string reason) => new(false, false, false, reason);
}

/// <summary>
/// Binds a freshly launched jail's CLI to a real terminal: spawns the CLI inside the container
/// attached to a TTY (the default factory runs <c>docker exec -it</c> under a daemon-side forkpty
/// PTY — see <see cref="SandboxCliLaunch"/>), registers the long-lived session with
/// <see cref="TerminalSessionManager"/> (so <c>TerminalService.Attach</c> streams the REAL CLI) and
/// with the P2-09 <see cref="SessionLeader"/> (which owns per-agent PTY fds and their input pause),
/// and reflects the CLI's exit in the session store as a state delta.
///
/// <para>Binding is best-effort by design: on a box without the docker CLI (dev loop) the spawn
/// failure is audited and the agent degrades to the session-only shape (attach echoes) rather than
/// failing the whole spawn — the jail itself is real and correct either way.</para>
/// </summary>
public sealed class AgentCliBinder
{
    private const int AgentUid = 1000;
    private const int DefaultCols = 120;
    private const int DefaultRows = 32;

    private readonly TerminalSessionManager _terminals;
    private readonly SessionLeader _leader;
    private readonly AgentSessionStore _store;
    private readonly IAuditLog _audit;
    private readonly Func<AgentCliLaunchSpec, ITerminalSession> _sessionFactory;
    private readonly Mainguard.Server.Terminal.TerminalEngineConfig _engine;
    private readonly Mainguard.Server.Auth.TerminalLockRegistry? _locks;
    private readonly ILogger _log;

    public AgentCliBinder(
        TerminalSessionManager terminals,
        SessionLeader leader,
        AgentSessionStore store,
        IAuditLog audit,
        Func<AgentCliLaunchSpec, ITerminalSession>? sessionFactory = null,
        ILoggerFactory? loggerFactory = null,
        Mainguard.Server.Terminal.TerminalEngineConfig? engine = null,
        Mainguard.Server.Auth.TerminalLockRegistry? locks = null)
    {
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _leader = leader ?? throw new ArgumentNullException(nameof(leader));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _sessionFactory = sessionFactory ?? SpawnDockerExecPty;
        _locks = locks;
        // Optional so the AgentCliWiringTests direct construction keeps working; DI supplies the real
        // ones (the P2-18 engine flag included — absent means interim, today's behavior).
        _engine = engine ?? Mainguard.Server.Terminal.TerminalEngineConfig.Interim;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(DaemonLogCategories.Terminal);
    }

    /// <summary>
    /// The pure launch plan behind the production factory: <c>docker exec -i -t</c> (interactive
    /// TTY — <c>isatty()</c> true for the docker CLI daemon-side and for the agent CLI in-jail, so
    /// an unauthenticated CLI opens its interactive login instead of printing a non-interactive
    /// refusal and exiting), an explicit sane <c>TERM</c> on both sides of the exec, and a positive
    /// terminal size. The environment is minimal and secret-free (G-13) — the CLI's credentials come
    /// from the in-container <c>/run/secrets/agent/agent.env</c> the launch wrapper sources.
    /// </summary>
    internal static CliPtyLaunch BuildPtyLaunch(AgentCliLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var (command, args) = SandboxCliLaunch.BuildDockerExecArgv(spec.ContainerId, spec.Launch, AgentUid);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TERM"] = SandboxCliLaunch.InJailTerm,
        };
        foreach (var name in new[] { "PATH", "HOME", "DOCKER_HOST" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                env[name] = value;
            }
        }

        return new CliPtyLaunch(command, args, env, DefaultCols, DefaultRows);
    }

    /// <summary>The production factory: the <see cref="BuildPtyLaunch"/> plan under a real
    /// daemon-side PTY (<see cref="PtyProcessShim"/> forkpty/ConPTY).</summary>
    internal static ITerminalSession SpawnDockerExecPty(AgentCliLaunchSpec spec)
    {
        var launch = BuildPtyLaunch(spec);
        return PtyProcessShim.Spawn(
            launch.Command, launch.Args, Environment.CurrentDirectory, launch.Environment,
            launch.Cols, launch.Rows);
    }

    /// <summary>
    /// Starts the CLI and registers the bound session. Returns true when the terminal is live;
    /// false when the CLI could not be started (audited; the agent stays session-only + echo).
    /// </summary>
    public bool TryBind(AgentCliLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        ITerminalSession session;
        try
        {
            session = _sessionFactory(spec);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cli bind failed agent={Agent} — degrading to session-only echo", spec.AgentId);
            _audit.Append(new AuditEvent("cli_bind_failed", new Dictionary<string, string>
            {
                ["agent_id"] = spec.AgentId,
                ["agent_kind_launch"] = spec.Launch.Count > 0 ? spec.Launch[0] : string.Empty,
                ["reason"] = ex.Message,
            }));
            return false;
        }

        // P2-18: the engine flag decides whether this session also runs the daemon-side vterm grid.
        // Cols/rows match the PTY spawn defaults — the one-authoritative-size rule from birth.
        // MG-5: a managed worker's terminal is input-locked (view-only). Evaluate the lock live at
        // OSC 52 fan-out time (the lock is applied by AgentSpawnService AFTER this bind), so a copy-out
        // from a locked session's output is dropped rather than written to the operator's host clipboard.
        var locks = _locks;
        var agentId = spec.AgentId;
        Func<bool>? isInputLocked = locks is null ? null : () => locks.IsLocked(agentId);
        var bound = new BoundTerminalSession(spec.AgentId, session, _engine, DefaultCols, DefaultRows, isInputLocked);
        // (repo, agent id), never the id alone: an intake-named `pr-<n>` is unique only inside a repo, and
        // Bind DISPOSES whatever it replaces — id-only keying let this bind kill another repository's
        // still-running worker CLI.
        var key = new AgentSessionKey(spec.RepoHash, spec.AgentId);
        _terminals.Bind(key, bound);
        _log.LogInformation(
            "cli bound agent={Agent} container={Container} engine={Engine}",
            spec.AgentId, spec.ContainerId, _engine.Engine);

        // P2-09: the leader owns the per-agent PTY (registry entry + kill + input pause seam).
        _leader.Register(
            new LeaderSession(spec.AgentId, spec.RepoHash, spec.ContainerId, DefaultCols, DefaultRows,
                SocketPath: string.Empty),
            kill: bound.Kill);

        _audit.Append(new AuditEvent("cli_bound", new Dictionary<string, string>
        {
            ["agent_id"] = spec.AgentId,
            ["container_id"] = spec.ContainerId,
        }));

        _ = WatchExitAsync(key, bound);
        return true;
    }

    /// <summary>Kills + unregisters this session's CLI (StopAgent / teardown). Idempotent. Repo-scoped, so
    /// stopping repo A's <c>pr-7</c> leaves repo B's <c>pr-7</c> terminal alone — and, unlike the previous
    /// id-keyed shape, actually releases repo A's rather than skipping the teardown while a second holder
    /// of the id exists.
    ///
    /// <para><see cref="SessionLeader"/> is still keyed by agent id alone, so its kill stays id-only; it is
    /// guarded by the caller's last-holder-of-the-id check in <see cref="AgentSpawnService"/>.</para></summary>
    public void Release(AgentSessionKey key)
    {
        _terminals.Release(key);
        _terminals.ClearBindPending(key); // torn down before it bound → no attach should keep waiting
    }

    /// <summary>The id-keyed half of a release: the <see cref="SessionLeader"/> PTY registry entry. Split
    /// out because the leader has no repo in its key, so the caller must only reach it once NO session
    /// anywhere on the daemon still answers to the id.</summary>
    public void ReleaseLeader(string agentId) => _leader.Kill(agentId);

    /// <summary>Marks that a CLI bind is expected for this session (spawn in-flight) so an
    /// early attach waits for it instead of falling into echo — the attach-before-bind race. Cleared by a
    /// successful <see cref="TryBind"/> or by <see cref="ClearBindPending"/> on a session-only/failed spawn.</summary>
    public void MarkBindPending(AgentSessionKey key) => _terminals.MarkBindPending(key);

    /// <summary>Clears the pending-bind flag (no CLI will bind for this agent — an attach should echo).</summary>
    public void ClearBindPending(AgentSessionKey key) => _terminals.ClearBindPending(key);

    /// <summary>
    /// How long the daemon waits, after pressing Enter for the coordinator, for the worker's CLI to
    /// produce output. Long enough that an idle CLI's redraw always lands inside it; short enough that a
    /// coordinator's tool call does not stall on a worker that is wedged.
    /// </summary>
    internal static readonly TimeSpan PromptReactionWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the daemon waits for the CLI to repaint the message body <i>before</i> pressing Enter.
    /// That echo is what separates the two writes causally, so the CR cannot be read as the tail of a
    /// paste (defect J2). Measured at 0–1 ms against the real CLI even for an 872-byte message, so this
    /// is generous by three orders of magnitude; when it lapses the caller falls back to a plain delay,
    /// so a slow CLI costs latency, never correctness.
    /// </summary>
    internal static readonly TimeSpan PromptEchoWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Delivers a coordinator's steering prompt to a managed worker's live CLI (coordinator contract §3,
    /// <c>send_worker_prompt</c>). Reports <see cref="PromptDelivery.NotDelivered"/> when no CLI is bound
    /// for that session — the caller reports that rather than pretending the prompt landed.
    ///
    /// <para><b>The line is terminated with CR, not LF, and that is the whole defect this once had.</b>
    /// It wrote <c>prompt + "\n"</c>, which a PTY-attached TUI reads as "insert a newline in my input
    /// box" — never as Enter. Three prompts to two workers in a live run therefore ACCUMULATED unsubmitted
    /// in their input lines while every layer above logged success. See <see cref="TerminalSubmit"/> for
    /// the measured rule and why it is one shared fact rather than per-adapter vendor knowledge.</para>
    ///
    /// <para><b>Why this deliberately does not consult <see cref="Mainguard.Server.Auth.TerminalLockRegistry"/>.</b>
    /// That lock exists to sever <i>human</i> keyboard input to a managed worker (P2-14): a worker's terminal
    /// is read-only in the UI so steering goes through the sanctioned channel instead of a human typing into
    /// an agent's session. This IS that sanctioned channel. Honouring the lock here would make
    /// <c>send_worker_prompt</c> permanently impossible, since every worker a coordinator owns is Managed
    /// and therefore locked — the tool would be a contract entry that could never once succeed.</para>
    ///
    /// <para>The ownership and plan-gate checks that make this safe are applied by the caller
    /// (<c>AgentSpawnService.PromptAsync</c>): the target must be a live child of the calling coordinator in
    /// the same repo, and must already hold an approved plan. This method is the delivery mechanism, not the
    /// gate — it is <c>internal</c> so no other transport can reach it without going through those checks.</para>
    /// </summary>
    internal async Task<PromptDelivery> TrySendPromptAsync(
        AgentSessionKey key, string prompt, CancellationToken ct)
    {
        var bound = _terminals.TryGetBound(key);
        if (bound is null)
        {
            return PromptDelivery.NotDelivered;
        }

        if (!TerminalSubmit.TryEncodeSubmission(prompt, out var body, out var terminator))
        {
            // A bare terminator is Enter, which would confirm whatever the CLI has focused (a permission
            // dialog's highlighted option, an autocomplete row). Refuse rather than press it blindly.
            return PromptDelivery.Refused("there is nothing to submit — the prompt is empty.");
        }

        SubmitObservation seen;
        try
        {
            // Two writes, not one. Appending the CR to the body submitted a 3-byte poke and silently
            // failed a 139-byte steer, because a TUI reads a CR arriving inside one burst as pasted
            // content rather than as Enter (defect J2, measured — §17.8).
            seen = await bound
                .SubmitLineAndAwaitOutputAsync(body, terminator, PromptEchoWindow, PromptReactionWindow, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // `TryGetBound` succeeding only means a session EXISTS, not that its pty is still writable —
            // a jail can die between the lookup and the write, and the write then throws EIO. Letting
            // that escape made a Try method throw, and the coordinator saw the raw errno ("Input/output
            // error") instead of the actionable refusal its caller already had ready ("<id> has no live
            // CLI to steer."). Undelivered is exactly what NotDelivered means.
            _log.LogWarning(
                ex, "coordinator prompt to worker={Agent} could not be written to its pty", key.AgentId);
            return PromptDelivery.NotDelivered;
        }

        // The old line here said "delivered", off nothing but a write that returned. It now says what was
        // actually written (terminator included and separate, so a regression to LF — or to one coalesced
        // write — is visible in the log itself) and what the CLI did about each half.
        _log.LogInformation(
            "coordinator prompt submitted to worker={Agent} ({Bytes} bytes, terminator=CR sent separately) "
            + "echoed={Echoed} reacted={Reacted}",
            key.AgentId, body.Length, seen.Echoed, seen.Reacted);
        return new PromptDelivery(true, seen.Echoed, seen.Reacted, null);
    }

    /// <summary>Cap on the last-output tail carried into the death reason/audit — enough to name
    /// the cause ("the input device is not a TTY", "Not logged in …", a stack-trace head).</summary>
    internal const int ExitTailChars = 400;

    private async Task WatchExitAsync(AgentSessionKey key, BoundTerminalSession bound)
    {
        var agentId = key.AgentId;
        int exitCode;
        try
        {
            exitCode = await bound.ExitCode.ConfigureAwait(false);
        }
        catch (Exception)
        {
            exitCode = -1;
        }

        // Only reflect a natural CLI exit; a Release/Stop already removed the session record. Checked
        // against THIS session's key: id-only, another repo's live `pr-7` answered here for a session that
        // had already been released, so a stopped agent got a spurious Dead-with-tail broadcast.
        if (_terminals.TryGetBound(key) is not null)
        {
            // The CLI's dying words (from the replay ring) are the diagnosis — a bare exit code
            // told the field NOTHING when the coordinator died at launch. They go to the audit
            // log durably; the bound session stays registered, so attaching to the dead agent's
            // terminal still replays the same output in full.
            var tail = bound.TailText(ExitTailChars);
            _log.LogInformation("cli exited agent={Agent} exitCode={ExitCode}", agentId, exitCode);
            _audit.Append(new AuditEvent("cli_exited", new Dictionary<string, string>
            {
                ["agent_id"] = agentId,
                ["exit_code"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["output_tail"] = tail,
            }));
            // Keyed, not id-only: the id-only MarkState is a documented no-op when two repos hold the id,
            // so a `pr-7` CLI dying in either repo used to change no state at all — the card stayed
            // "Working" over a dead CLI. With the key in hand the right session is marked every time.
            _store.MarkState(key, "Dead",
                tail.Length > 0 ? $"CLI exited ({exitCode}): {tail}" : $"CLI exited ({exitCode}).");
        }
    }
}

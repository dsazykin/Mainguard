using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Mainguard.Agents.Services;
using Mainguard.Git;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
namespace Mainguard.Agents.Agents;

/// <summary>
/// P2-06 daemon service (no UI dependency). Manages per-agent worktrees: create
/// <c>agent/&lt;id&gt;</c> off the mirror's default branch, remove, and prune.
/// Every worktree is <b>quarantined</b> — its sole configured remote is a daemon-owned repository
/// (§3.4), so an agent's <c>git push</c> can only land there, never in the user's real remote and
/// never with credentials it does not have.
///
/// <para><b>MG-3.</b> That repository is no longer the shared mirror: each agent gets its OWN bare
/// repo (<see cref="AgentRepoManager"/>) borrowing the mirror's objects through
/// <c>objects/info/alternates</c>, and the worktree is linked off <i>that</i>. The mirror stops being
/// a surface the jail writes at all; the daemon carries the agent's branch across
/// (<see cref="PublishAgentBranch"/>), naming the source and destination refs itself.</para>
/// </summary>
public interface IAgentWorktreeManager
{
    /// <summary>Create the worktree for an agent (branch <c>agent/&lt;id&gt;</c> off the mirror's default branch). Returns its path.</summary>
    string CreateAgentWorktree(string repoHash, string agentId);

    /// <summary>
    /// <b>Resume</b> — give an agent id that ALREADY has an <c>agent/&lt;id&gt;</c> branch a fresh worktree
    /// standing on that branch, with its commits intact. Returns the worktree path.
    ///
    /// <para>This is the mirror image of <see cref="CreateAgentWorktree"/>, which refuses outright when the
    /// branch exists (a duplicate id) and creates the branch with <c>worktree add -b</c>. Adoption requires
    /// the branch to exist and checks it out <i>without</i> <c>-b</c>, so the work committed by the previous
    /// jail is what the new one starts from. An absent branch is
    /// <see cref="Mainguard.Git.Exceptions.AgentBranchMissingException"/> — never a fresh branch off the
    /// default, which would report success for an operation that recovered nothing.</para>
    ///
    /// <para><b>The default throws, deliberately.</b> A worktree manager with no substrate cannot adopt
    /// anything, and the one alternative — quietly delegating to <see cref="CreateAgentWorktree"/> — would
    /// answer "resumed" for a jail started on an empty branch. This repository's recurring defect is a
    /// control that looks applied and is not; a default that cannot do the job must say so.</para>
    /// </summary>
    string AdoptAgentWorktree(string repoHash, string agentId)
        => throw new Mainguard.Git.Exceptions.RepoProvisioningException(
            $"This worktree manager cannot adopt an existing branch, so agent '{agentId}' in repo "
            + $"'{repoHash}' cannot be resumed here.");

    /// <summary>Remove an agent's worktree; <paramref name="force"/> discards a dirty tree, otherwise a dirty tree is refused (typed).</summary>
    void RemoveAgentWorktree(string repoHash, string agentId, bool force);

    /// <summary>
    /// Clears an agent's worktree + per-agent repository while <b>leaving
    /// <c>refs/heads/agent/&lt;id&gt;</c> in the mirror exactly where it is</b> — the rollback path for a
    /// resume whose jail failed to start.
    ///
    /// <para><see cref="RemoveAgentWorktree"/> ends with <c>branch -D</c>, which is correct for a teardown
    /// (the agent is finished; no residue) and catastrophic here: the branch is the only surviving copy of
    /// the work the resume was invoked to recover, so a failed resume that ran the ordinary cleanup would
    /// destroy exactly what it was asked to save.</para>
    ///
    /// <para><b>The default throws</b> rather than falling back to <see cref="RemoveAgentWorktree"/>: a
    /// manager that cannot preserve the branch must not silently delete it, and every caller of this method
    /// is on a best-effort cleanup path where leaving residue is strictly better than losing commits.</para>
    /// </summary>
    void RemoveAgentWorktreeKeepingBranch(string repoHash, string agentId)
        => throw new System.NotSupportedException(
            $"This worktree manager cannot remove agent '{agentId}''s worktree while preserving its "
            + "branch, and a resume's cleanup must never delete the branch it was resuming.");

    /// <summary>Prune stale worktree metadata.</summary>
    void Prune(string repoHash);

    /// <summary>List the repo's agent worktrees via the porcelain parser (drives the ListWorktrees RPC).</summary>
    IReadOnlyList<WorktreeItem> List(string repoHash);

    /// <summary>
    /// MG-3 — the per-agent repository whose git dir backs this agent's worktree, bind-mounted
    /// READ-WRITE into exactly one jail at its identical VM path. Empty when this implementation has
    /// no per-agent repo (the test doubles), which simply means the jail carries no such mount.
    /// </summary>
    string AgentRepoPathFor(string repoHash, string agentId) => string.Empty;

    /// <summary>
    /// MG-3 — carry <c>refs/heads/agent/&lt;id&gt;</c> from the agent's own repository into the shared
    /// mirror, which is where the merge queue reads it from. The daemon names both the source ref and
    /// the destination; the agent never proposes a ref update at all. Returns true when the mirror's
    /// ref is at the agent's tip afterwards.
    /// </summary>
    bool PublishAgentBranch(string repoHash, string agentId) => false;

    /// <summary>
    /// MG-3 — start watching this agent's own <c>refs/heads/agent/&lt;id&gt;</c> and publish it into the
    /// mirror whenever it moves (design §7: the daemon watches AND re-fetches before verification).
    /// Called at spawn. Default no-op for the substrate-less test doubles.
    /// </summary>
    void WatchAgentRef(string repoHash, string agentId) { }

    /// <summary>MG-3 — stop watching (teardown). Default no-op.</summary>
    void UnwatchAgentRef(string repoHash, string agentId) { }

    /// <summary>
    /// Establishes which branch this agent's worktree is actually committing on, so that work committed
    /// somewhere other than <c>agent/&lt;id&gt;</c> is REPORTED rather than silently ignored.
    ///
    /// <para>The default is <see cref="AgentBranchAlignmentState.Unknown"/>, not "aligned": the test
    /// doubles that take this default have no worktree to read, and an implementation that cannot measure
    /// alignment must never be able to assert it. A caller that treats Unknown as a pass is choosing to;
    /// it cannot do so by accident.</para>
    /// </summary>
    AgentBranchAlignment CheckAgentBranch(string repoHash, string agentId)
        => new(AgentBranchAlignmentState.Unknown, AgentRepoLayout.BranchPrefix + agentId,
            Detail: "this worktree manager has no worktree to inspect");
}

/// <summary>
/// MG-3 — the one best-effort wrapper around <see cref="IAgentWorktreeManager.WatchAgentRef"/>, for the
/// paths that <b>take responsibility for a jail they did not spawn</b>.
///
/// <para><b>Why a shared helper rather than a call per site.</b> The spawn path
/// (<c>SandboxAgentLauncher.LaunchAsync</c>) is the only place a watch starts as part of creating the
/// agent; every OTHER way the daemon comes to own a live jail is an <i>adoption</i> — the boot
/// <c>SwarmReconciler</c> keeping a survivor, the external-PR intake finding its <c>pr-&lt;n&gt;</c>
/// container still up — and an adoption that skips this leaves the agent unwatched for the rest of its
/// life. Nothing errors when it does: the pre-verification publish (design §7's other half) still carries
/// the tip into the mirror whenever a verification happens to run, so the symptom is a review cockpit,
/// merge-queue projection and stale cascade sitting on a tip the agent already moved past. Each site
/// rolling its own <c>try</c> is how the second one got forgotten.</para>
///
/// <para><b>Best-effort by contract.</b> Both callers sit on paths where a throw is worse than a missed
/// watch — the boot sequence is fail-fast (an exception here is a daemon that will not start) and the
/// intake poll must survive one bad pull request. <c>Watch</c> is idempotent and keyed on
/// <c>(repoHash, agentId)</c>, so calling it again on a later pass costs nothing and one repository's
/// <c>pr-7</c> never stands in for another's.</para>
/// </summary>
public static class AgentRefWatchRegistration
{
    /// <summary>Registers <paramref name="agentId"/> in <paramref name="repoHash"/> with the MG-3 ref
    /// sweep, swallowing any failure. Returns true when the watch was accepted — for callers that want to
    /// log or assert it, never as something to branch the caller's own outcome on.</summary>
    public static bool TryWatch(IAgentWorktreeManager worktrees, string repoHash, string agentId)
    {
        try
        {
            worktrees.WatchAgentRef(repoHash, agentId);
            return true;
        }
        catch (Exception)
        {
            // The mirror still catches up at verification time; no caller may fail over the sweep.
            return false;
        }
    }
}

/// <inheritdoc cref="IAgentWorktreeManager"/>
public sealed class WorktreeManager : IAgentWorktreeManager
{
    private readonly string _vmRoot;
    private readonly Func<string, (int ExitCode, string Output)> _pnpmRunner;
    private readonly Action<string>? _warningSink;
    private readonly IAuditLog? _audit;
    private readonly Sandbox.PackageCacheManager? _packageCaches;
    private readonly AgentRepoManager _agentRepos;
    private readonly AgentRefMediator _refs;
    private readonly Lazy<AgentRefWatcher> _watcher;

    /// <param name="vmRoot">The VM base directory (shared with the provisioner). Injected for tests.</param>
    /// <param name="pnpmRunner">
    /// Runs <c>pnpm install</c> in a worktree. Injected so tests can assert the command was
    /// <i>issued</i> (or simulate a failure) without running real pnpm; defaults to a real spawn.
    /// This is the ONE process launch in <c>Mainguard.Agents/Agents</c> — all git goes through the
    /// shared <see cref="GitServices.RunGit"/> primitive.
    /// </param>
    /// <param name="warningSink">Receives non-fatal warnings (e.g. a failed pnpm install).</param>
    /// <param name="audit">
    /// MG-3 — G-17 sink for the one security-relevant event this type produces: a REFUSED publish
    /// (<see cref="AgentRefRefusedEvent"/>). An agent that rewrote history the mirror had already
    /// published, or an id that failed the layout gate, must leave a durable record rather than only a
    /// log line — the whole finding was a control that looked applied and was not.
    /// </param>
    /// <param name="packageCaches">
    /// MG-43 — the daemon-owned package cache, so a retired agent's cache is reclaimed on the ONE
    /// teardown path every caller already goes through (<see cref="RemoveAgentWorktree"/> — the swarm
    /// reconciler, the external-PR intake, the sync service and the launcher's own cleanup all land
    /// here). Without it a cache would only ever be reclaimed by budget eviction, which is the residue
    /// path, not the ordinary one. Null (every existing test double) simply keeps no cache.
    /// </param>
    public WorktreeManager(
        string? vmRoot = null,
        Func<string, (int ExitCode, string Output)>? pnpmRunner = null,
        Action<string>? warningSink = null,
        IAuditLog? audit = null,
        Sandbox.PackageCacheManager? packageCaches = null)
    {
        _vmRoot = vmRoot ?? DefaultVmRoot();
        _pnpmRunner = pnpmRunner ?? RealPnpmInstall;
        _warningSink = warningSink;
        _audit = audit;
        _packageCaches = packageCaches;
        _agentRepos = new AgentRepoManager(_vmRoot);
        _refs = new AgentRefMediator(_agentRepos, BareRepoPathFor, OnPublishOutcome);
        // The warning sink is what keeps an eviction from the sweep visible: an agent that silently stops
        // being watched still reaches the mirror at verification time, so nothing fails — the watcher
        // half of design §7 just quietly stops, which is exactly the shape of failure MG-3 exists to
        // avoid producing more of.
        _watcher = new Lazy<AgentRefWatcher>(
            () => new AgentRefWatcher(_refs, _agentRepos, interval: null, warningSink: _warningSink));
    }

    /// <summary>The G-17 audit type for a refused publish (MG-3).</summary>
    public const string AgentRefRefusedEvent = "agent_ref_refused";

    // A refusal is the interesting half: it means an agent rewrote history the mirror had already
    // published (or aimed at something that is not its own branch), and it must not pass silently just
    // because the caller wanted a bool.
    private void OnPublishOutcome(AgentRefPublishResult result)
    {
        if (!result.Refused)
        {
            return;
        }

        // The agent id is echoed RAW, never through AgentRepoLayout.RefFor: a RefusedTarget outcome is
        // precisely the case where the id failed that gate, and re-running it here would throw out of a
        // mediator whose entire contract is that it never does.
        _warningSink?.Invoke(
            $"MG-3: refused to publish agent '{result.AgentId}' into repo "
            + $"'{result.RepoHash}' — {result.Outcome}: {result.Reason}");

        _audit?.Append(new AuditEvent(AgentRefRefusedEvent, new Dictionary<string, string>
        {
            ["repo"] = result.RepoHash,
            ["agent"] = result.AgentId,
            ["outcome"] = result.Outcome.ToString(),
            ["old"] = result.OldSha ?? string.Empty,
            ["new"] = result.NewSha ?? string.Empty,
            ["reason"] = result.Reason ?? string.Empty,
            ["when"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }));
    }

    /// <summary>MG-3 — the mediator this manager publishes through (the watcher shares it).</summary>
    public AgentRefMediator RefMediator => _refs;

    /// <summary>MG-3 — the per-agent repository store (the watcher needs it to snapshot refs).</summary>
    public AgentRepoManager AgentRepos => _agentRepos;

    /// <summary>
    /// MG-3 — the ref watcher, created on first use so a manager that never spawns an agent (every unit
    /// test, the merge-diff bridge) never starts a background loop at all. Its lifetime is the daemon's:
    /// <see cref="UnwatchAgentRef"/> removes an agent from the sweep, and there is nothing per-agent to
    /// dispose.
    /// </summary>
    public AgentRefWatcher RefWatcher => _watcher.Value;

    /// <inheritdoc />
    public void WatchAgentRef(string repoHash, string agentId) => RefWatcher.Watch(repoHash, agentId);

    /// <inheritdoc />
    public void UnwatchAgentRef(string repoHash, string agentId) => RefWatcher.Unwatch(repoHash, agentId);

    public string CreateAgentWorktree(string repoHash, string agentId)
    {
        var barePath = BareRepoPathFor(repoHash);
        if (!Directory.Exists(barePath))
        {
            throw new RepoProvisioningException($"No provisioned mirror for repo '{repoHash}'; provision it first.");
        }

        var branch = BranchFor(agentId);
        var worktreePath = WorktreePathFor(repoHash, agentId);
        var agentRepoPath = _agentRepos.PathFor(repoHash, agentId);

        // Refuse (typed) BEFORE any mutation if the branch or either path already exists (edge row 3):
        // leave no residue. The mirror still carries agent/<id> — publishing it there is what the merge
        // queue consumes — so it remains the authoritative "is this id taken?" question.
        if (BranchExists(barePath, branch))
        {
            throw new AgentWorktreeConflictException($"Branch '{branch}' already exists for repo '{repoHash}'.");
        }

        if (Directory.Exists(worktreePath))
        {
            throw new AgentWorktreeConflictException($"Worktree path already exists for agent '{agentId}'.");
        }

        if (_agentRepos.Exists(repoHash, agentId))
        {
            throw new AgentWorktreeConflictException($"A per-agent repository already exists for agent '{agentId}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        // MG-3: the agent's own repository, borrowing the mirror's objects through alternates. The
        // worktree is linked off THIS, not off the mirror — which is what lets the mirror's mount become
        // read-only without taking `git commit` away from the agent.
        _agentRepos.Create(repoHash, agentId, barePath);
        try
        {
            var baseBranch = DefaultBranch(agentRepoPath);
            AgentGitCommand.Run(agentRepoPath, "worktree", "add", "-b", branch, worktreePath, baseBranch);
            FinishWorktreeLocked(repoHash, agentId, worktreePath, agentRepoPath);
        }
        catch
        {
            // Leave no residue: a half-made agent repo would make the next spawn of this id conflict.
            _agentRepos.Remove(repoHash, agentId);
            throw;
        }

        return worktreePath;
    }

    /// <inheritdoc />
    public string AdoptAgentWorktree(string repoHash, string agentId)
    {
        var barePath = BareRepoPathFor(repoHash);
        if (!Directory.Exists(barePath))
        {
            throw new RepoProvisioningException($"No provisioned mirror for repo '{repoHash}'; provision it first.");
        }

        var branch = BranchFor(agentId);
        var worktreePath = WorktreePathFor(repoHash, agentId);

        // (1) Rescue first. The previous jail's per-agent repository may still hold commits the mirror
        //     never saw — the ref watcher publishes on its own clock and the last-publish-on-teardown only
        //     runs for a clean stop, so a daemon crash or a VM halt leaves exactly this gap. That repo is
        //     deleted a few lines below and the mirror is what the adopted branch is cloned FROM, so this
        //     is the last moment those commits can be saved. Mediated (fast-forward only, never a delete),
        //     and best effort: an unreadable residue must not stop the resume, it must not silently lose
        //     work either — which is why it is attempted rather than skipped.
        if (_agentRepos.Exists(repoHash, agentId))
        {
            PublishAgentBranch(repoHash, agentId);
        }

        // (2) The branch IS the thing being resumed. Checked AFTER the rescue publish, so an agent whose
        //     branch reached the mirror only just now is still resumable — and refused outright when it is
        //     genuinely absent. Never a fresh branch off the default: see AgentBranchMissingException.
        if (!BranchExists(barePath, branch))
        {
            throw new AgentBranchMissingException(repoHash, agentId, branch);
        }

        // (3) Clear the dead jail's residue — the stale worktree and the per-agent repo — WITHOUT touching
        //     the mirror's branch. The package cache is deliberately kept: it belongs to this same agent,
        //     and re-downloading it is the cost RemoveAgentWorktree pays because there its agent is retired.
        ClearWorktreeResidue(repoHash, agentId, barePath);

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        // (4) A fresh per-agent repository, cloned from the mirror — which carries refs/heads/agent/<id>
        //     at the tip we just confirmed, so the adopted branch arrives with its history.
        var agentRepoPath = _agentRepos.Create(repoHash, agentId, barePath);
        try
        {
            // No `-b`: check out the EXISTING branch. `-b` is what makes CreateAgentWorktree a creation,
            // and it is the one character between resuming the work and starting an empty branch under
            // the same name.
            AgentGitCommand.Run(agentRepoPath, "worktree", "add", worktreePath, branch);
            FinishWorktreeLocked(repoHash, agentId, worktreePath, agentRepoPath);
        }
        catch
        {
            // Same "no residue" rule as a create — and pointedly NOT RemoveAgentWorktree, whose last act
            // is `branch -D` on the mirror. The branch outlives every failure on this path.
            _agentRepos.Remove(repoHash, agentId);
            throw;
        }

        return worktreePath;
    }

    // The tail both entry points share: quarantine remote, dependency hook, jail-writable modes, the
    // in-jail branch guard, and the mirror publish. Factored out so a create and an adoption cannot drift
    // into producing differently-configured worktrees — the difference between them is meant to be
    // exactly which branch `worktree add` lands on.
    private void FinishWorktreeLocked(
        string repoHash, string agentId, string worktreePath, string agentRepoPath)
    {
        // Quarantine remote (§3.4 + MG-3): the worktree's remotes MUST be exactly
        // {origin -> the agent's OWN repo}. A linked worktree shares its main repository's config,
        // so this is also the agent repo's only remote. `git push origin` therefore succeeds
        // entirely inside the agent's writable space — LLM CLIs push reflexively and that has to
        // keep working — while the mirror is not a remote it can name at all.
        AgentGitCommand.TryRun(worktreePath, out _, "remote", "remove", "origin");
        AgentGitCommand.Run(worktreePath, "remote", "add", "origin", agentRepoPath);

        // pnpm hook (§3.3): only when a lockfile is present, and non-fatal — a failure surfaces
        // a warning but the worktree is still returned.
        MaybeRunPnpm(worktreePath, agentId);

        // MG-17: the jail that mounts this worktree read-write is host uid/gid 101000 (the userns
        // remap), not this process's uid 1000 — so a checkout laid down under the daemon's 022 umask
        // (0644 files, 0755 dirs) is one the agent can READ and never EDIT. Group-share it. This runs
        // LAST so it also covers whatever `pnpm install` just wrote.
        GroupShareRecursive(worktreePath);
        // …and again over the agent repo, whose `worktrees/<id>` metadata `worktree add` just created.
        GroupShareRecursive(agentRepoPath);

        // The in-jail guard rail (layer 2), installed AFTER both GroupShareRecursive passes so the
        // hook keeps its own narrower mode instead of being widened back to group-writable. It is
        // ergonomics, not a boundary — see AgentBranchGuard — and it is best effort: a spawn must
        // never fail because a guard rail could not be written.
        AgentBranchGuard.InstallHook(agentRepoPath, agentId, _warningSink);

        // Seed the merge queue's input contract: refs/heads/agent/<id> exists in the MIRROR from the
        // moment the agent does, pointing at the base commit. Everything downstream (the queue, the
        // diff bridge, the host repo's sync fetch) reads it there and is unaffected by where the
        // agent actually commits. On an adoption the mirror is already at the tip, so this is an
        // `Unchanged` no-op — asserted rather than assumed by the resume tests.
        PublishAgentBranch(repoHash, agentId);
    }

    /// <inheritdoc />
    public string AgentRepoPathFor(string repoHash, string agentId) => _agentRepos.PathFor(repoHash, agentId);

    /// <inheritdoc />
    public AgentBranchAlignment CheckAgentBranch(string repoHash, string agentId)
        => AgentBranchGuard.Probe(
            WorktreePathFor(repoHash, agentId), _agentRepos.PathFor(repoHash, agentId), agentId);

    /// <inheritdoc />
    public bool PublishAgentBranch(string repoHash, string agentId) => Publish(repoHash, agentId).Current;

    /// <summary>
    /// MG-3 stage 2 — the mediated publish, with the outcome rather than a bool. The four rules
    /// (destination is this agent's branch, fast-forward only, no deletes, never the integration
    /// branch) live in <see cref="AgentRefMediator"/>; this is the manager's entry point to them.
    /// </summary>
    public AgentRefPublishResult Publish(string repoHash, string agentId)
        => _refs.Publish(repoHash, agentId);

    /// <summary>
    /// MG-17 — grants the group <c>rwX</c> throughout a tree the daemon owns and a remapped jail must be
    /// able to write, and sets the setgid bit on its directories so anything created underneath inherits
    /// that group.
    ///
    /// <para>The GROUP itself is not set here and does not need to be: <c>~/mainguard/worktrees</c> is
    /// provisioned <c>2775 mainguard:mainguard-jail</c> at boot, so every directory created inside it
    /// already carries gid 101000 by inheritance. What inheritance cannot supply is the mode — umask is
    /// a property of the writing process, not of the parent directory — which is exactly what this fixes.
    /// <b>Anything MG-3 adds that a jail must write needs this same call (or
    /// <c>core.sharedRepository=group</c> for a git dir); anything it mounts read-only needs neither,
    /// because read+traverse already come from the group.</b></para>
    ///
    /// <para>Best effort and Unix-only: on Windows (the unit-test and dev-box path) there is no jail and
    /// no remap, and a failure to relax a mode must never fail a spawn — the failure it would cause is
    /// strictly worse than the one it is preventing.</para>
    /// </summary>
    internal static void GroupShareRecursive(string root)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            ShareOne(root, isDirectory: true);
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                ShareOne(dir, isDirectory: true);
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ShareOne(file, isDirectory: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A symlink loop, a racing removal, a filesystem with no Unix modes — never fail the spawn.
        }
    }

    private static void ShareOne(string path, bool isDirectory)
    {
        // Repeated (the caller already returned on Windows) so the platform analyzer can see the guard
        // on the call site itself rather than one frame up.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            // `g+rwX`: read/write always; execute only where execute already exists (or it is a
            // directory), so a data file never becomes executable.
            mode |= UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
            if (isDirectory
                || (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                mode |= UnixFileMode.GroupExecute;
            }

            if (isDirectory)
            {
                // setgid: children inherit the shared group instead of the creator's primary group.
                mode |= UnixFileMode.SetGroup;
            }

            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Per-entry best effort (a dangling symlink is the common one).
        }
    }

    public void RemoveAgentWorktree(string repoHash, string agentId, bool force)
    {
        var barePath = BareRepoPathFor(repoHash);
        var worktreePath = WorktreePathFor(repoHash, agentId);
        var branch = BranchFor(agentId);
        // The owner of the worktree metadata. A worktree made before MG-3 is still linked off the
        // mirror, so an upgraded daemon must be able to tear that one down too.
        var owner = _agentRepos.Exists(repoHash, agentId) ? _agentRepos.PathFor(repoHash, agentId) : barePath;

        if (Directory.Exists(worktreePath))
        {
            if (!force && IsDirty(worktreePath))
            {
                throw new AgentWorktreeConflictException(
                    $"Worktree for agent '{agentId}' has uncommitted changes; pass force to discard them.");
            }

            if (force)
            {
                AgentGitCommand.Run(owner, "worktree", "remove", "--force", worktreePath);
            }
            else
            {
                AgentGitCommand.Run(owner, "worktree", "remove", worktreePath);
            }
        }

        // Prune any dangling metadata and delete the agent branch so no residue survives either way.
        // The mirror's copy of agent/<id> is what the merge queue consumed, so it is deleted too.
        AgentGitCommand.TryRun(owner, out _, "worktree", "prune");
        AgentGitCommand.TryRun(barePath, out _, "worktree", "prune");
        AgentGitCommand.TryRun(barePath, out _, "branch", "-D", branch);

        // MG-3: the agent's own repository goes with it. Its objects were already COPIED into the mirror
        // by every publish (a fetch across a local transport transfers objects; the mirror borrows from
        // nobody), so deleting it can never strand a commit the mirror's refs still name.
        _agentRepos.Remove(repoHash, agentId);

        // MG-43: and so does its package cache. The cache is derived, disposable content that only this
        // agent's jail ever mounted, so a retired agent has no claim on the gigabytes it holds — and
        // reclaiming here is what keeps budget eviction a residue path (a crashed daemon, an unclean
        // removal) rather than the routine one.
        _packageCaches?.Release(repoHash, agentId);

        // §4 gc policy: this is the natural idle point — if that was the last borrower, unreachable
        // objects in the mirror may finally be pruned.
        MirrorMaintenance.AfterAgentDetached(barePath, _agentRepos, repoHash, _warningSink);
    }

    /// <inheritdoc />
    public void RemoveAgentWorktreeKeepingBranch(string repoHash, string agentId)
    {
        var barePath = BareRepoPathFor(repoHash);
        ClearWorktreeResidue(repoHash, agentId, barePath);

        // The package cache is deliberately NOT released. It belongs to this same agent, which is either
        // about to be resumed again or has just failed to be — and the cache is the one part of a jail
        // that is expensive to rebuild.
        MirrorMaintenance.AfterAgentDetached(barePath, _agentRepos, repoHash, _warningSink);
    }

    /// <summary>
    /// Removes the dead jail's worktree and per-agent repository and <b>leaves the mirror's
    /// <c>agent/&lt;id&gt;</c> alone</b>. The single distinction from <see cref="RemoveAgentWorktree"/>'s
    /// tail, and the reason it is a separate method rather than a flag: the <c>branch -D</c> there is not
    /// an incidental line, it is what makes a teardown final.
    /// </summary>
    private void ClearWorktreeResidue(string repoHash, string agentId, string barePath)
    {
        var worktreePath = WorktreePathFor(repoHash, agentId);
        var owner = _agentRepos.Exists(repoHash, agentId) ? _agentRepos.PathFor(repoHash, agentId) : barePath;

        if (Directory.Exists(worktreePath))
        {
            // Always forced: the tree belongs to a jail that no longer exists, and refusing on a dirty
            // checkout would make a resume impossible for exactly the agents that were interrupted
            // mid-edit. Uncommitted bytes are not recoverable through the branch and were never the
            // thing being resumed — the commits are (see AdoptAgentWorktree step 1).
            AgentGitCommand.TryRun(owner, out _, "worktree", "remove", "--force", worktreePath);
            if (Directory.Exists(worktreePath))
            {
                // git declined (metadata already pruned, a stale lock, an unmounted path). The DIRECTORY
                // is what `worktree add` collides with, so remove it directly. Best effort: one that will
                // not go away surfaces as git's own error on the next add, which names the real cause
                // better than anything this line could.
                try { Directory.Delete(worktreePath, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        AgentGitCommand.TryRun(owner, out _, "worktree", "prune");
        AgentGitCommand.TryRun(barePath, out _, "worktree", "prune");

        // No `branch -D`. This is the whole point of the method.
        _agentRepos.Remove(repoHash, agentId);
    }

    public void Prune(string repoHash)
    {
        var barePath = BareRepoPathFor(repoHash);
        AgentGitCommand.Run(barePath, "worktree", "prune");
        foreach (var agentId in _agentRepos.ListAgentIds(repoHash))
        {
            AgentGitCommand.TryRun(_agentRepos.PathFor(repoHash, agentId), out _, "worktree", "prune");
        }
    }

    public IReadOnlyList<WorktreeItem> List(string repoHash)
    {
        // MG-3: worktrees now hang off the per-agent repositories, so the listing is their union. The
        // mirror is still asked because a pre-MG-3 daemon left its worktrees there, and an upgrade must
        // not make them vanish from the RPC while they are still on disk.
        var items = new List<WorktreeItem>();
        foreach (var gitDir in EnumerateWorktreeOwners(repoHash))
        {
            if (AgentGitCommand.TryRun(gitDir, out var porcelain, "worktree", "list", "--porcelain") != 0)
            {
                continue;
            }

            foreach (var item in WorktreePorcelainParser.Parse(porcelain))
            {
                // Skip each repository's own bare stanza: it is a git dir, not an agent worktree, and
                // before MG-3 the mirror's stanza was the (single) "main" entry that got filtered the
                // same way by everything downstream.
                if (item.Branch is { Length: > 0 } branch
                    && branch.StartsWith(AgentRepoLayout.BranchPrefix, StringComparison.Ordinal))
                {
                    items.Add(item);
                }
            }
        }

        return items;
    }

    private IEnumerable<string> EnumerateWorktreeOwners(string repoHash)
    {
        yield return BareRepoPathFor(repoHash);
        foreach (var agentId in _agentRepos.ListAgentIds(repoHash))
        {
            yield return _agentRepos.PathFor(repoHash, agentId);
        }
    }

    /// <summary>The bare-mirror path for a hash (shared layout with the provisioner).</summary>
    public string BareRepoPathFor(string repoHash) => Path.Combine(_vmRoot, "repos", repoHash + ".git");

    /// <summary>The worktree path for an agent: <c>&lt;vmRoot&gt;/worktrees/&lt;hash&gt;/&lt;agentId&gt;</c>.</summary>
    public string WorktreePathFor(string repoHash, string agentId)
        => Path.Combine(_vmRoot, "worktrees", repoHash, agentId);

    private static string BranchFor(string agentId) => "agent/" + agentId;

    private void MaybeRunPnpm(string worktreePath, string agentId)
    {
        if (!File.Exists(Path.Combine(worktreePath, "pnpm-lock.yaml")))
        {
            return;
        }

        try
        {
            var (exitCode, output) = _pnpmRunner(worktreePath);
            if (exitCode != 0)
            {
                _warningSink?.Invoke(
                    $"pnpm install failed for agent '{agentId}' (exit {exitCode}); the worktree was still created. {output.Trim()}");
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: agents can still run without dependencies installed.
            _warningSink?.Invoke($"pnpm install could not run for agent '{agentId}': {ex.Message}");
        }
    }

    private static bool BranchExists(string barePath, string branch)
        => AgentGitCommand.TryRun(barePath, out _, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch) == 0;

    private static bool IsDirty(string worktreePath)
        => AgentGitCommand.Run(worktreePath, "status", "--porcelain").Trim().Length > 0;

    private static string DefaultBranch(string barePath)
    {
        // The mirror's HEAD points at the source's default branch (main/master); base the worktree
        // off whatever that is rather than assuming a literal name.
        if (AgentGitCommand.TryRun(barePath, out var output, "symbolic-ref", "--short", "HEAD") == 0)
        {
            var name = output.Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "main";
    }

    private static (int ExitCode, string Output) RealPnpmInstall(string worktreePath)
    {
        // The single, injectable, real process launch in Mainguard.Agents/Agents. Git never spawns here.
        var psi = new ProcessStartInfo
        {
            FileName = "pnpm",
            WorkingDirectory = worktreePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("install");

        using var process = Process.Start(psi)
            ?? throw new RepoProvisioningException("Failed to launch pnpm.");
        // Drain BOTH pipes concurrently: pnpm writes progress to stderr, and reading stdout to end
        // first deadlocks once stderr fills its ~64KB pipe buffer (the audit-flagged wsl-runner bug
        // class) — which would hang worktree creation inside the daemon.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return (process.ExitCode, combined);
    }

    // MainguardPaths.HomeDirectory(), not the old `?? "."` fallback: a relative VM root silently
    // resolving against the daemon's CWD is exactly the class of bug that crash-looped mainguardd.
    // An unresolvable home now fails loudly with the systemd remedy named.
    private static string DefaultVmRoot()
        => Path.Combine(MainguardPaths.HomeDirectory(), "mainguard");
}

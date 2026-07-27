using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Mainguard.Agents.Services;
using Mainguard.Git;
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

    /// <summary>Remove an agent's worktree; <paramref name="force"/> discards a dirty tree, otherwise a dirty tree is refused (typed).</summary>
    void RemoveAgentWorktree(string repoHash, string agentId, bool force);

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
}

/// <inheritdoc cref="IAgentWorktreeManager"/>
public sealed class WorktreeManager : IAgentWorktreeManager
{
    private readonly string _vmRoot;
    private readonly Func<string, (int ExitCode, string Output)> _pnpmRunner;
    private readonly Action<string>? _warningSink;
    private readonly AgentRepoManager _agentRepos;

    /// <param name="vmRoot">The VM base directory (shared with the provisioner). Injected for tests.</param>
    /// <param name="pnpmRunner">
    /// Runs <c>pnpm install</c> in a worktree. Injected so tests can assert the command was
    /// <i>issued</i> (or simulate a failure) without running real pnpm; defaults to a real spawn.
    /// This is the ONE process launch in <c>Mainguard.Agents/Agents</c> — all git goes through the
    /// shared <see cref="GitServices.RunGit"/> primitive.
    /// </param>
    /// <param name="warningSink">Receives non-fatal warnings (e.g. a failed pnpm install).</param>
    public WorktreeManager(
        string? vmRoot = null,
        Func<string, (int ExitCode, string Output)>? pnpmRunner = null,
        Action<string>? warningSink = null)
    {
        _vmRoot = vmRoot ?? DefaultVmRoot();
        _pnpmRunner = pnpmRunner ?? RealPnpmInstall;
        _warningSink = warningSink;
        _agentRepos = new AgentRepoManager(_vmRoot);
    }

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

            // Seed the merge queue's input contract: refs/heads/agent/<id> exists in the MIRROR from the
            // moment the agent does, pointing at the base commit. Everything downstream (the queue, the
            // diff bridge, the host repo's sync fetch) reads it there and is unaffected by where the
            // agent actually commits.
            PublishAgentBranch(repoHash, agentId);
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
    public string AgentRepoPathFor(string repoHash, string agentId) => _agentRepos.PathFor(repoHash, agentId);

    /// <inheritdoc />
    public bool PublishAgentBranch(string repoHash, string agentId)
    {
        var barePath = BareRepoPathFor(repoHash);
        var agentRepoPath = _agentRepos.PathFor(repoHash, agentId);
        if (!Directory.Exists(agentRepoPath) || !Directory.Exists(barePath))
        {
            return false;
        }

        var refName = AgentRepoLayout.RefFor(agentId);
        // The daemon names BOTH sides. The refspec has no leading '+', so git's own fetch machinery
        // refuses a non-fast-forward; stage 2 replaces this with the explicit four-rule mediation.
        return AgentGitCommand.TryRun(
            barePath, out _, "fetch", "--no-tags", agentRepoPath, $"{refName}:{refName}") == 0;
    }

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

        // §4 gc policy: this is the natural idle point — if that was the last borrower, unreachable
        // objects in the mirror may finally be pruned.
        MirrorMaintenance.AfterAgentDetached(barePath, _agentRepos, repoHash, _warningSink);
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

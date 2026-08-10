using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Git;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents;

/// <summary>
/// MG-3 — the pure, IO-free naming rules for the per-agent repository layout, and the ONE place that
/// decides what an agent id is allowed to look like.
///
/// <para>The layout is
/// <c>&lt;vmRoot&gt;/agents/&lt;repoHash&gt;/&lt;agentId&gt;.git</c>, deliberately a sibling of
/// <c>&lt;vmRoot&gt;/repos/&lt;repoHash&gt;.git</c> (the shared mirror) rather than a child of it: the
/// mirror becomes a tree the jail may only READ, so nothing the jail writes may live inside it.
/// <c>agents/</c> is provisioned <c>2775 mainguard:mainguard-jail</c> at boot by
/// <see cref="Sandbox.UsernsRemapPolicy.MountOwnershipScript"/> precisely so these directories inherit
/// the remapped jail gid and the setgid bit <i>by construction</i> (MG-17).</para>
///
/// <para><b>Why the id is validated here.</b> The agent id is chosen daemon-side today (a GUID, or
/// <c>pr-&lt;n&gt;</c>), but it now names BOTH a filesystem path and a git ref. Either one is a
/// traversal primitive if it is ever sourced from the wire: <c>../../repos/&lt;hash&gt;</c> as an id
/// would put the "per-agent" repo on top of the mirror, and <c>a/../main</c> as a ref component would
/// make <c>refs/heads/agent/&lt;id&gt;</c> resolve somewhere else entirely. Refusing anything outside a
/// conservative charset is one check that closes both, and it is applied at construction of every path
/// and every ref name rather than at one call site that a later edit can route around.</para>
/// </summary>
public static class AgentRepoLayout
{
    /// <summary>The <c>&lt;vmRoot&gt;</c> child holding every per-agent repository.</summary>
    public const string AgentsDirectoryName = "agents";

    /// <summary>The branch an agent works on, and the merge queue's input contract.</summary>
    public const string BranchPrefix = "agent/";

    /// <summary>The fully-qualified form of <see cref="BranchPrefix"/>.</summary>
    public const string RefPrefix = "refs/heads/agent/";

    /// <summary>Longest accepted agent id. A GUID "N" is 32; <c>pr-&lt;n&gt;</c> is far shorter.</summary>
    public const int MaxAgentIdLength = 64;

    /// <summary><c>&lt;vmRoot&gt;/agents</c>.</summary>
    public static string AgentsRoot(string vmRoot)
        => Path.Combine(RequireVmRoot(vmRoot), AgentsDirectoryName);

    /// <summary><c>&lt;vmRoot&gt;/agents/&lt;repoHash&gt;</c> — every agent repo for one mirror.</summary>
    public static string RepoAgentsDirectory(string vmRoot, string repoHash)
        => Path.Combine(AgentsRoot(vmRoot), RequireHandle(repoHash));

    /// <summary><c>&lt;vmRoot&gt;/agents/&lt;repoHash&gt;/&lt;agentId&gt;.git</c> — the repository one
    /// agent owns outright and the ONLY git directory its jail may write.</summary>
    public static string AgentRepoPath(string vmRoot, string repoHash, string agentId)
        => Path.Combine(RepoAgentsDirectory(vmRoot, repoHash), RequireAgentId(agentId) + ".git");

    /// <summary><c>agent/&lt;id&gt;</c>.</summary>
    public static string BranchFor(string agentId) => BranchPrefix + RequireAgentId(agentId);

    /// <summary><c>refs/heads/agent/&lt;id&gt;</c> — the merge queue's input, in the mirror.</summary>
    public static string RefFor(string agentId) => RefPrefix + RequireAgentId(agentId);

    /// <summary>
    /// The agent id, or a typed refusal. Accepts ASCII letters/digits and <c>. _ -</c> only, refuses a
    /// leading <c>.</c>/<c>-</c>, any <c>..</c>, a trailing <c>.lock</c>, and anything over
    /// <see cref="MaxAgentIdLength"/>. That is simultaneously a path-traversal guard and a subset of
    /// git's own <c>check-ref-format</c> rules, so a valid id is always a valid single path component
    /// AND a valid ref component.
    /// </summary>
    public static string RequireAgentId(string agentId)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            throw new RepoProvisioningException("MG-3: an agent id is required to name its repository and branch.");
        }

        if (agentId.Length > MaxAgentIdLength)
        {
            throw new RepoProvisioningException(
                $"MG-3: agent id '{agentId}' is longer than {MaxAgentIdLength} characters.");
        }

        foreach (var c in agentId)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                     || c is '.' or '_' or '-';
            if (!ok)
            {
                throw new RepoProvisioningException(
                    $"MG-3: agent id '{agentId}' contains '{c}'. An agent id names both a directory under "
                    + "agents/ and a component of refs/heads/agent/<id>, so only [A-Za-z0-9._-] is accepted.");
            }
        }

        if (agentId[0] is '.' or '-'
            || agentId.Contains("..", StringComparison.Ordinal)
            || agentId.EndsWith(".lock", StringComparison.Ordinal))
        {
            throw new RepoProvisioningException(
                $"MG-3: agent id '{agentId}' is not a usable path/ref component (leading '.'/'-', '..' or a '.lock' suffix).");
        }

        return agentId;
    }

    /// <summary>The repo hash names a directory too; it is daemon-computed (SHA-256 hex) but validated
    /// on the same grounds — a caller-supplied handle must never be able to escape <c>agents/</c>.</summary>
    private static string RequireHandle(string repoHash)
    {
        if (string.IsNullOrEmpty(repoHash))
        {
            throw new RepoProvisioningException("MG-3: a repo hash is required to locate the agents directory.");
        }

        foreach (var c in repoHash)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                     || c is '.' or '_' or '-';
            if (!ok || repoHash.Contains("..", StringComparison.Ordinal))
            {
                throw new RepoProvisioningException(
                    $"MG-3: repo handle '{repoHash}' is not a usable single path component.");
            }
        }

        return repoHash;
    }

    private static string RequireVmRoot(string vmRoot)
    {
        if (string.IsNullOrWhiteSpace(vmRoot))
        {
            throw new RepoProvisioningException("MG-3: the VM root is required to locate the agents directory.");
        }

        return vmRoot;
    }
}

/// <summary>
/// MG-3 — creates, locates and destroys the repository each agent owns outright.
///
/// <para><b>This is not a clone of the history.</b> The per-agent repo is made with
/// <c>git clone --bare --shared</c>, which writes a one-line <c>objects/info/alternates</c> pointing at
/// the mirror's object store instead of copying (or hardlinking) a single object. The agent repo owns
/// its <c>refs/</c>, <c>HEAD</c>, <c>config</c>, <c>index</c> and the objects it creates; everything
/// else resolves straight through to the mirror. Measured on a three-commit seed repo the whole
/// directory is ~26 KB — the sample hooks and config, not history — and
/// <c>CoordinatorLimits.MaxActiveWorkers</c> is 6.</para>
///
/// <para><b>Why this reverses the trust direction.</b> A linked worktree off the mirror also shares the
/// object store, but it shares it the wrong way round: committing writes new objects AND the branch ref
/// <i>into the mirror</i>, so the jail must hold write access to the mirror — and anything that can
/// write the mirror can rewrite <c>refs/heads/main</c> without ever invoking <c>receive-pack</c>, which
/// is MG-3 in one sentence. Alternates give the identical storage saving with the direction reversed:
/// the jail reads the mirror and writes only here.</para>
/// </summary>
public sealed class AgentRepoManager
{
    private readonly string _vmRoot;

    /// <param name="vmRoot">The VM base directory (shared with the provisioner and worktree manager).</param>
    public AgentRepoManager(string? vmRoot = null)
        => _vmRoot = vmRoot ?? Path.Combine(MainguardPaths.HomeDirectory(), "mainguard");

    /// <summary>The per-agent repository path (whether or not it exists yet).</summary>
    public string PathFor(string repoHash, string agentId)
        => AgentRepoLayout.AgentRepoPath(_vmRoot, repoHash, agentId);

    /// <summary>The directory holding every agent repo for one mirror.</summary>
    public string RepoAgentsDirectory(string repoHash)
        => AgentRepoLayout.RepoAgentsDirectory(_vmRoot, repoHash);

    /// <summary>True when the per-agent repo has actually been created (git dir present).</summary>
    public bool Exists(string repoHash, string agentId)
        => Directory.Exists(Path.Combine(PathFor(repoHash, agentId), "objects"));

    /// <summary>Every agent id that currently has a repository against <paramref name="repoHash"/>.</summary>
    public IReadOnlyList<string> ListAgentIds(string repoHash)
    {
        var dir = RepoAgentsDirectory(repoHash);
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(dir, "*.git")
            .Select(d => Path.GetFileName(d)!)
            .Where(n => n.EndsWith(".git", StringComparison.Ordinal))
            .Select(n => n[..^4])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>True while ANY agent still borrows objects from this mirror through an alternate — the
    /// condition that makes a prune unsafe (§4: pruning breaks borrowers, repacking does not).</summary>
    public bool AnyAttached(string repoHash) => ListAgentIds(repoHash).Count > 0;

    /// <summary>
    /// Creates the per-agent repository against <paramref name="barePath"/> and returns its path.
    ///
    /// <para>Idempotent only in the "already fully created" sense: a half-made directory is cleared and
    /// remade, so a crashed create leaves no residue that a later spawn has to reason about.</para>
    /// </summary>
    public string Create(string repoHash, string agentId, string barePath)
    {
        if (!Directory.Exists(Path.Combine(barePath, "objects")))
        {
            throw new RepoProvisioningException(
                $"No provisioned mirror at '{barePath}'; the per-agent repository borrows its objects and cannot be made without it.");
        }

        var agentRepoPath = PathFor(repoHash, agentId);
        var parent = Path.GetDirectoryName(agentRepoPath)!;
        Directory.CreateDirectory(parent);

        if (Directory.Exists(agentRepoPath))
        {
            Directory.Delete(agentRepoPath, recursive: true);
        }

        // `--shared` is what makes this cheap: git writes <agentRepo>/objects/info/alternates naming the
        // mirror's object store and copies NO objects. `--bare` because the working tree is a linked
        // worktree created separately (and mounted at /workspace), not a child of this directory.
        AgentGitCommand.Run(parent, "clone", "--bare", "--shared", barePath, agentRepoPath);

        // `clone` records the source as `origin`. The agent must not carry a configured remote pointing at
        // the mirror: after stage 3 that mount is read-only, so a `git push origin` there would fail in a
        // way that reads like a product bug rather than a boundary. Its real `origin` is set on the
        // worktree (which shares this config) and points at THIS repository.
        AgentGitCommand.TryRun(agentRepoPath, out _, "remote", "remove", "origin");

        // MG-17: the daemon creates this tree under its 022 umask, and the jail that mounts it read-write
        // is a DIFFERENT host uid that meets us only through the setgid group `agents/` propagates. The
        // config setting covers every FUTURE git write in here (including the object fan-out directories
        // the agent's first commit creates); the recursive mode pass fixes what the clone just laid down.
        AgentGitCommand.Run(agentRepoPath, "config", "core.sharedRepository", "group");

        // §4 gc policy, agent side: never let an implicit `gc --auto` fire inside the jail. It would be
        // operating on a repository whose objects mostly live in someone else's store, and the one
        // operation that breaks an alternates borrower is a prune.
        AgentGitCommand.Run(agentRepoPath, "config", "gc.auto", "0");

        // Other agents' branches came along with the clone. They are not a new exposure (the mirror is
        // mounted read-only into every jail and holds the same refs) but they are noise that would make a
        // later `for-each-ref refs/heads/agent/` here ambiguous about whose branch is whose.
        DropForeignAgentRefs(agentRepoPath, agentId);

        WorktreeManager.GroupShareRecursive(agentRepoPath);
        return agentRepoPath;
    }

    /// <summary>Deletes the per-agent repository outright (teardown). Best effort by contract — a stop
    /// must never fail because a directory would not go away.</summary>
    public void Remove(string repoHash, string agentId)
    {
        var path = PathFor(repoHash, agentId);
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave it; the next create clears a residual directory before cloning into it.
        }
    }

    /// <summary>
    /// Removes every <c>refs/heads/agent/*</c> the clone brought along that is not this agent's own.
    ///
    /// <para><b>The delete is CHECKED and a failure is fatal to the create.</b> It used to test the
    /// <c>for-each-ref</c> exit code and discard <c>update-ref -d</c>'s, which is the wrong way round: the
    /// listing failing means "there is nothing to reason about", while the delete failing means the ref is
    /// STILL THERE. The class comment above calls these branches "noise", and they are noise for
    /// disambiguating <c>for-each-ref</c> — but a ref that survives is also a branch agent A can
    /// <c>git checkout</c> and read, in its own writable repository, with the whole of agent B's work in
    /// it. That is the MG-3 isolation boundary, and a boundary that reports success when it did not hold
    /// is worse than one that is absent.</para>
    ///
    /// <para>Throwing is safe here and deliberately chosen over a warning: the only caller is
    /// <see cref="Create"/>, whose callers already unwind a failed create by removing the half-made
    /// repository (see <c>WorktreeManager.CreateAgentWorktree</c>), so the agent gets a failed spawn with
    /// a named reason instead of a jail that can see another agent's branch.</para>
    /// </summary>
    private static void DropForeignAgentRefs(string agentRepoPath, string agentId)
    {
        var mine = AgentRepoLayout.RefFor(agentId);
        if (AgentGitCommand.TryRun(
                agentRepoPath, out var listing, "for-each-ref", "--format=%(refname)", AgentRepoLayout.RefPrefix) != 0)
        {
            return;
        }

        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var refName = line.Trim();
            if (refName.Length > 0 && !string.Equals(refName, mine, StringComparison.Ordinal))
            {
                var exit = AgentGitCommand.TryRun(agentRepoPath, out var output, "update-ref", "-d", refName);
                if (exit != 0)
                {
                    throw new RepoProvisioningException(
                        $"could not drop foreign agent branch '{refName}' from agent '{agentId}'s repository "
                        + $"(git update-ref -d exited {exit}): that branch stays visible and checkout-able to "
                        + "this agent, which is the MG-3 isolation boundary. The spawn is refused rather than "
                        + $"completed with the boundary unheld. {output.Trim()}");
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Mainguard.Git;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// The IO half of the per-agent conversation store: creates the daemon-owned directories a jail's CLI
/// writes its transcripts into, answers whether a previous session left anything there, and drops the
/// store when the agent it belongs to is finally torn down.
///
/// <para>Everything about <i>why</i> the store is shaped this way — a bind mount rather than a
/// harvest-on-stop round trip, per (repo, agent) rather than shared, ext4 rather than tmpfs, and never
/// able to hold a credential — is in <see cref="ConversationStorePolicy"/>. This type only does the
/// filesystem work.</para>
///
/// <para><b>There is no budget and no eviction, unlike <see cref="PackageCacheManager"/>, and that is a
/// decision rather than an omission.</b> A package cache is derived content measured in gigabytes that a
/// re-download can always rebuild; a transcript is a few hundred kilobytes of JSONL that nothing can
/// rebuild, ever. Evicting one to reclaim space would delete the only copy of the thing this feature
/// exists to keep. The store is reclaimed by exactly one event — the final teardown of the agent it
/// belongs to (<c>WorktreeManager.RemoveAgentWorktree</c>, which is also where the branch is deleted) —
/// and the residue left by a crashed daemon is the price. It is a stated gap: see
/// docs/design/agent-conversation-persistence.md §7.</para>
/// </summary>
public sealed class ConversationStoreManager
{
    private readonly string _vmRoot;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    /// <summary>Resolved once; see <see cref="Grant"/>.</summary>
    private PackageCacheGrant? _grant;

    /// <param name="vmRoot">The VM base directory, shared with the provisioner and worktree manager.</param>
    /// <param name="log">Optional milestone sink (the daemon's sandbox log category).</param>
    public ConversationStoreManager(string? vmRoot = null, Action<string>? log = null)
    {
        _vmRoot = vmRoot ?? Path.Combine(MainguardPaths.HomeDirectory(), "mainguard");
        _log = log;
    }

    /// <summary>The store root every per-agent store hangs off. Never mounted into any jail.</summary>
    public string RootPath => ConversationStorePolicy.StoreRoot(_vmRoot);

    /// <summary>This agent's whole store directory (whether or not it exists yet).</summary>
    public string PathFor(string repoHash, string agentId)
        => ConversationStorePolicy.AgentStorePath(_vmRoot, repoHash, agentId);

    /// <summary>
    /// Which grant this store root supports — the SAME question <see cref="PackageCacheManager"/> asks
    /// about the cache root, deliberately answered by the same implementation
    /// (<see cref="PackageCachePolicy.DecideGrant"/>) rather than by a second copy.
    ///
    /// <para>The grant is a property of the MACHINE, not of the tree: it asks whether the boot step
    /// provisioned a <c>mainguard-jail</c> group that both the unprivileged daemon and the remapped jail
    /// belong to. A dev box or CI runner that never ran that step falls to
    /// <see cref="PackageCacheGrant.ModeOnly"/> for both trees at once, and it would be a real bug for
    /// this tree to reach a different verdict about the same machine.</para>
    /// </summary>
    public PackageCacheGrant Grant => _grant ??= ResolveGrant();

    /// <summary>
    /// Makes this agent's declared conversation directories exist and be writable by the jail, and
    /// returns the bind mounts that carry them. Called on the spawn path <b>before</b> the container is
    /// created (mounts are fixed at create); every failure is typed and stops the spawn.
    ///
    /// <para>The credential-overlap invariant is re-asserted here, against the marker the daemon actually
    /// read, and not only in <c>AdapterManifest.Parse</c>. The manifest is reviewed product source, but
    /// the daemon does not spawn from the manifest — it spawns from an install marker written by the
    /// installer into a user-writable VM path, possibly by an older build. So the gate has to sit where
    /// the paths are USED, or it is a check on a file the decision does not depend on.</para>
    /// </summary>
    /// <param name="adapterId">Named only so a refusal can say which CLI's declaration was wrong.</param>
    /// <param name="conversationPaths">The adapter's declared <c>$HOME</c>-relative conversation paths.</param>
    /// <param name="credentialPaths">The adapter's declared credential paths — the list the conversation
    /// paths are checked against. Passing null here is not "no credentials", it is "we did not look",
    /// which is why every caller passes the marker's list verbatim.</param>
    public IReadOnlyList<ConversationMount> Prepare(
        string repoHash, string agentId, string adapterId,
        IEnumerable<string>? conversationPaths, IEnumerable<string>? credentialPaths)
    {
        // Throws ConversationStoreOverlapException before a single directory is created: the refusal has
        // to happen before the store exists, or a bad declaration leaves a credential-shaped directory
        // behind on disk even though the spawn failed.
        var declared = ConversationStorePolicy.UsablePaths(adapterId, conversationPaths, credentialPaths);
        if (declared.Length == 0)
        {
            return Array.Empty<ConversationMount>();
        }

        var mounts = new List<ConversationMount>(declared.Length);

        lock (_gate)
        {
            var grant = Grant;
            try
            {
                // The WHOLE chain, each level shared as it is created — the MG-17 invariant is a property
                // of the PARENT (a setgid parent hands its group to everything created underneath), so
                // creating only the leaf leaves the chain unformed on any VM root the boot step never
                // provisioned. Same failure PackageCacheManager already took once against a test suite
                // that builds a fresh VM root per run.
                ApplyGrantLocked(Directory.CreateDirectory(RootPath), PackageCachePolicy.ParentMode);
                ApplyGrantLocked(
                    Directory.CreateDirectory(ConversationStorePolicy.RepoStoreDirectory(_vmRoot, repoHash)),
                    PackageCachePolicy.ParentMode);
                ApplyGrantLocked(
                    Directory.CreateDirectory(PathFor(repoHash, agentId)),
                    PackageCachePolicy.LeafMode(grant));

                foreach (var relative in declared)
                {
                    var path = ConversationStorePolicy.DeclaredPathStore(_vmRoot, repoHash, agentId, relative);
                    // Every intermediate directory too (".claude" before ".claude/projects"), each with
                    // the leaf mode: the CLI writes through all of them, not just into the deepest.
                    var current = PathFor(repoHash, agentId);
                    foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        current = Path.Combine(current, segment);
                        ApplyGrantLocked(Directory.CreateDirectory(current), PackageCachePolicy.LeafMode(grant));
                    }

                    if (!IsWritableDirectory(path))
                    {
                        throw new ConversationStoreUnavailableException(RootPath, path,
                            "even the daemon, which owns it, cannot write to it — the filesystem is "
                            + "read-only, full, or the mode was changed underneath us");
                    }

                    mounts.Add(new ConversationMount(path, relative));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ConversationStoreUnavailableException(
                    RootPath, PathFor(repoHash, agentId), $"it could not be created: {ex.Message}");
            }
        }

        _log?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"conversation store ready for {repoHash}/{agentId}: "
            + $"{string.Join(", ", mounts.Select(m => m.HomeRelativePath))} under '{RootPath}'; grant {Grant}"));
        return mounts;
    }

    /// <summary>
    /// Does a PREVIOUS session's conversation actually survive in this agent's store?
    ///
    /// <para>This is the guard on the resume flag, and it is deliberately a question about FILES rather
    /// than about directories. <see cref="Prepare"/> creates the directories on every spawn — including
    /// the very first one — so "the store directory exists" is true from the first moment and would make
    /// the guard permanently true, i.e. no guard at all. Handing a CLI a resume flag with no prior
    /// session is a worse failure than not handing it one: depending on the vendor it either starts a
    /// fresh conversation anyway (measured for claude-code) or exits immediately, and an agent whose CLI
    /// dies at spawn is a dead terminal with no explanation.</para>
    ///
    /// <para>Any regular file anywhere under a declared path counts. Symlinks are not followed — this is
    /// an agent-writable tree, and the question is what THIS store holds, not what something in it points
    /// at.</para>
    /// </summary>
    public bool HasTranscripts(string repoHash, string agentId, IEnumerable<string>? conversationPaths)
    {
        foreach (var relative in conversationPaths ?? Array.Empty<string>())
        {
            if (!Adapters.AdapterManifest.IsHomeRelativeFilePath(relative))
            {
                continue;
            }

            string path;
            try
            {
                path = ConversationStorePolicy.DeclaredPathStore(_vmRoot, repoHash, agentId, relative);
            }
            catch (RepoProvisioningException)
            {
                continue;
            }

            if (ContainsAnyFile(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Drops this agent's whole conversation store.
    ///
    /// <para><b>Called from the FINAL teardown only</b> — <c>WorktreeManager.RemoveAgentWorktree</c>, the
    /// path that also runs <c>branch -D</c> — and deliberately NOT from
    /// <c>RemoveAgentWorktreeKeepingBranch</c>, which is the resume rollback: an agent whose branch is
    /// being preserved for another attempt must keep the conversation that goes with it.</para>
    ///
    /// <para><b>Why the store goes at all when the branch does.</b> A clean stop deletes the branch, the
    /// worktree and the agent's own repository, so the work the conversation is ABOUT no longer exists.
    /// Keeping the transcript would not preserve continuity, it would create a trap: agent ids are unique
    /// per repo and not globally, and the intake's <c>pr-&lt;n&gt;</c> ids RECUR — a later <c>pr-7</c> for
    /// a different pull request would mount, and resume into, the previous one's conversation. That is
    /// both a wrong answer and a disclosure of one PR author's session to the next. Scoped by the PAIR
    /// (repo, agent), the same key everything else about a jail is keyed on.</para>
    /// </summary>
    public void Release(string repoHash, string agentId)
    {
        string path;
        try
        {
            path = PathFor(repoHash, agentId);
        }
        catch (RepoProvisioningException)
        {
            // An id that cannot name a path never had a store. Teardown must not throw over it.
            return;
        }

        lock (_gate)
        {
            DeleteTree(path);
        }
    }

    // ---- Symlink-safe filesystem primitives -------------------------------------------------------

    /// <summary>True when any REGULAR FILE exists anywhere under <paramref name="root"/>. Descends only
    /// into real directories; a symlink is neither followed nor counted.</summary>
    private static bool ContainsAnyFile(string root)
    {
        if (!Directory.Exists(root) || IsLink(root))
        {
            return false;
        }

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (files.Any(f => !IsLink(f)))
            {
                return true;
            }

            foreach (var child in SafeChildDirectories(dir))
            {
                stack.Push(child);
            }
        }

        return false;
    }

    private static IEnumerable<string> SafeChildDirectories(string parent)
    {
        string[] children;
        try
        {
            children = Directory.GetDirectories(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            if (!IsLink(child))
            {
                yield return child;
            }
        }
    }

    /// <summary>True for a symlink/reparse point — unknown counts as one, so the walk never leaves the
    /// tree. Same conservative direction as <see cref="PackageCacheManager"/>, and for the same reason:
    /// this is an agent-WRITABLE tree the daemon walks.</summary>
    private static bool IsLink(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: false) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void DeleteTree(string path)
    {
        if (!Directory.Exists(path) || IsLink(path))
        {
            TryDeleteFile(path);
            return;
        }

        try
        {
            DeleteTreeRecursive(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"conversation store could not fully remove '{path}': {ex.Message}");
        }
    }

    /// <summary>Recursive delete that unlinks symlinks instead of descending through them.</summary>
    private static void DeleteTreeRecursive(string dir)
    {
        foreach (var file in Directory.GetFiles(dir))
        {
            TryDeleteFile(file);
        }

        foreach (var child in Directory.GetDirectories(dir))
        {
            if (IsLink(child))
            {
                TryDeleteFile(child);
            }
            else
            {
                DeleteTreeRecursive(child);
            }
        }

        try
        {
            Directory.Delete(dir, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsWritableDirectory(string path)
    {
        var probe = Path.Combine(path, ".mgconvwrite-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies <paramref name="mode"/> to one directory in the store chain. Deliberately NOT recursive
    /// and deliberately not a chown: the daemon runs unprivileged and cannot chown into the remapped
    /// range, and the CONTENT below is written by the jail — as its own uid, under the group the setgid
    /// bit hands down. Re-applied on every spawn, so a mode changed underneath the daemon heals.
    /// </summary>
    private void ApplyGrantLocked(DirectoryInfo directory, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(directory.FullName, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Invoke($"conversation store could not set mode on '{directory.FullName}': {ex.Message}");
        }
    }

    private PackageCacheGrant ResolveGrant()
    {
        var gid = PackageCacheManager.ReadOwningGroupId(RootPath);
        var grant = PackageCachePolicy.DecideGrant(gid);
        _log?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"conversation store grant for '{RootPath}': {grant} (root gid {gid}, jail gid "
            + $"{UsernsRemapPolicy.AgentHostGid})"));
        return grant;
    }
}

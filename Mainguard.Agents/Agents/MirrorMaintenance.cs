using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Mainguard.Agents.Agents;

/// <summary>
/// MG-3 §4 — the object-lifetime policy for a shared mirror that other repositories borrow from
/// through <c>objects/info/alternates</c>.
///
/// <para><b>The distinction the whole policy rests on: pruning breaks borrowers, repacking does
/// not.</b> Repacking consolidates loose objects into packs and re-deltas them, but every object still
/// exists and still resolves by SHA, so an alternates borrower is unaffected. <i>Deleting</i>
/// unreachable objects is the only operation that can pull the floor out from under one — and git does
/// not track borrowers, so it will happily do it. That splits gc into a safe half that may run at any
/// time and an unsafe half that may run only at a genuine idle point:</para>
///
/// <list type="number">
///   <item><b><c>gc.auto=0</c> on the mirror.</b> Git runs <c>gc --auto</c> implicitly after many
///   ordinary commands (the daemon's own fetches included); with agents borrowing objects an implicit
///   prune firing mid-session is exactly the failure mode. Nothing in this codebase runs
///   <c>gc</c>/<c>repack</c> explicitly, so suppressing the automatic path is the whole of it.</item>
///   <item><b>Repack-without-prune may run at any time</b>, including with agents attached. This is
///   what bounds growth without ever waiting for an idle window — the answer to "what if the agents
///   are never all stopped at once".</item>
///   <item><b>Full prune only when no agent is attached</b> to that mirror, which happens naturally as
///   the last agent tears down and so needs no new scheduler and no lease.</item>
///   <item><b>A size guard</b>, so unbounded growth is visible rather than discovered at 40 GB.</item>
/// </list>
///
/// <para>No locking primitive is required and there is no stale-lease failure mode: every decision is
/// a question about what is on disk right now, asked at a moment when the answer cannot change under
/// us (a teardown the daemon is itself performing).</para>
/// </summary>
public static class MirrorMaintenance
{
    /// <summary>Loose objects above which a repack is worth its cost. A repo that has just been cloned
    /// is already fully packed, so this only fires after real agent traffic.</summary>
    public const int RepackLooseObjectThreshold = 2_000;

    /// <summary>The mirror size (packs + loose objects) at which growth stops being normal and gets
    /// surfaced. Deliberately a warning, never a refusal: refusing to spawn because a mirror got large
    /// would convert a housekeeping problem into an outage.</summary>
    public const long SizeWarningBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Pins <c>gc.auto=0</c> (and the newer <c>maintenance.auto</c> equivalent) on a mirror. Idempotent
    /// and applied on EVERY provision, so a mirror created before this policy existed is repaired by a
    /// daemon update alone rather than needing a re-clone.
    /// </summary>
    public static void ApplyGcPolicy(string barePath)
    {
        AgentGitCommand.Run(barePath, "config", "gc.auto", "0");
        // `git maintenance` is the modern automatic path and reads its own switch; setting only gc.auto
        // would leave a second door open on a newer git.
        //
        // CHECKED, exactly like gc.auto above, and for the reason the comment already gives. This was a
        // TryRun whose exit code went into `_`: on a git new enough for background maintenance — i.e.
        // precisely the git this line exists for — a failure to pin the switch leaves automatic
        // maintenance free to prune objects that alternates borrowers still need. Nothing fails at that
        // moment; the agent repositories break later with missing objects, at a distance from the cause
        // large enough that nobody would connect the two. Both halves of a two-door policy have to be
        // checked or it is a one-door policy with a comment. `git config <key> <value>` writes an
        // arbitrary key and does not require git to know it, so an older git sets it just as happily.
        AgentGitCommand.Run(barePath, "config", "maintenance.auto", "false");
    }

    /// <summary>
    /// Consolidates loose objects into packs <b>without deleting a single object</b>, and is therefore
    /// safe while agents are attached.
    ///
    /// <para><c>-A</c> rather than <c>-a</c> is load-bearing: git documents <c>-a -d</c> as also
    /// cleaning up the unreachable objects "<c>git prune</c> leaves behind", which is precisely the
    /// deletion an alternates borrower cannot survive. <c>-A</c> is defined as the same consolidation
    /// except that unreachable objects are <i>loosened</i> rather than dropped — nothing is ever thrown
    /// away. <c>-d</c> then removes only redundant <i>packs</i> and the loose copies of objects that are
    /// now inside a pack, both of which leave every SHA resolvable.</para>
    /// </summary>
    public static bool RepackWithoutPrune(string barePath)
        => AgentGitCommand.TryRun(barePath, out _, "repack", "-A", "-d", "--quiet") == 0;

    /// <summary>
    /// Deletes unreachable objects — the unsafe half — and therefore runs ONLY when the mirror has no
    /// alternates borrower left. Returns false (having done nothing) when an agent is still attached.
    ///
    /// <para>The repack first is not redundant: <c>git prune</c> only ever removes <b>loose</b> objects,
    /// and <c>-A</c> is precisely the flag that loosens unreachable objects back out of the packs they
    /// are hiding in. Without it the tail this method exists to reclaim would stay on disk forever while
    /// the call reported success.</para>
    /// </summary>
    public static bool PruneWhenIdle(string barePath, AgentRepoManager agentRepos, string repoHash)
    {
        ArgumentNullException.ThrowIfNull(agentRepos);
        if (agentRepos.AnyAttached(repoHash))
        {
            return false;
        }

        RepackWithoutPrune(barePath);
        AgentGitCommand.TryRun(barePath, out _, "worktree", "prune");
        return AgentGitCommand.TryRun(barePath, out _, "prune") == 0;
    }

    /// <summary>
    /// The teardown hook: repack when the loose-object count says it is worth it (always allowed),
    /// prune when this was the last borrower, then measure and surface the size.
    ///
    /// <para>Best effort by contract — a stop must never fail because housekeeping did.</para>
    /// </summary>
    public static void AfterAgentDetached(
        string barePath, AgentRepoManager agentRepos, string repoHash, Action<string>? warningSink)
    {
        try
        {
            if (!Directory.Exists(Path.Combine(barePath, "objects")))
            {
                return;
            }

            // Fast path. Publishing a handful of commits leaves a handful of loose objects, and running a
            // full repack on every agent stop would make teardown scale with the size of the repository
            // rather than with what the agent actually did. Below the threshold there is nothing worth
            // reclaiming, so neither half of gc runs at all.
            if (CountLooseObjects(barePath) >= RepackLooseObjectThreshold)
            {
                RepackWithoutPrune(barePath);
                PruneWhenIdle(barePath, agentRepos, repoHash);
            }

            var bytes = MeasureObjectStoreBytes(barePath);
            if (bytes >= SizeWarningBytes)
            {
                var megabytes = (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
                var guard = (SizeWarningBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
                warningSink?.Invoke(
                    $"mirror '{barePath}' object store is {megabytes} MB — above the {guard} MB guard. "
                    + "Unreachable objects are only pruned while no agent is attached (MG-3 §4); stop every "
                    + "agent on this repo to let a full prune run.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping is never allowed to fail a teardown.
        }
    }

    /// <summary>Loose objects in the mirror: the files under the 256 two-hex fan-out directories.</summary>
    public static int CountLooseObjects(string barePath)
    {
        var objects = Path.Combine(barePath, "objects");
        if (!Directory.Exists(objects))
        {
            return 0;
        }

        var count = 0;
        foreach (var dir in EnumerateFanoutDirectories(objects))
        {
            try
            {
                count += Directory.EnumerateFiles(dir).Count();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A fan-out directory removed by a concurrent repack — nothing to count.
            }
        }

        return count;
    }

    /// <summary>Total bytes of the mirror's object store (packs + loose). The size-guard input.</summary>
    public static long MeasureObjectStoreBytes(string barePath)
    {
        var objects = Path.Combine(barePath, "objects");
        if (!Directory.Exists(objects))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Raced away by a repack; the measurement is a guard, not an audit.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return total;
        }

        return total;
    }

    private static IEnumerable<string> EnumerateFanoutDirectories(string objectsDir)
    {
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(objectsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var dir in candidates)
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 2 && name.All(Uri.IsHexDigit))
            {
                yield return dir;
            }
        }
    }
}

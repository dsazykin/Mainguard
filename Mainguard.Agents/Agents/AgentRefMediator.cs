using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents;

/// <summary>What a mediated publish did, or why it refused.</summary>
public enum AgentRefPublishOutcome
{
    /// <summary>The mirror's <c>refs/heads/agent/&lt;id&gt;</c> was fast-forwarded to the agent's tip.</summary>
    Published,

    /// <summary>The mirror was already at the agent's tip; nothing to do.</summary>
    Unchanged,

    /// <summary>The agent's repository has no such branch (yet). The mirror's ref is left alone —
    /// an absent source is never read as a request to delete.</summary>
    NothingToPublish,

    /// <summary>The agent's tip is not a descendant of what the mirror already holds. Refused.</summary>
    RefusedNonFastForward,

    /// <summary>The computed destination is not this agent's branch. Refused (a bug, not a policy hit).</summary>
    RefusedTarget,

    /// <summary>Git itself failed (unreadable repo, races, disk). Nothing was changed.</summary>
    Failed,
}

/// <summary>The outcome of one mediated publish, with the shas involved so a refusal is diagnosable.</summary>
public sealed record AgentRefPublishResult(
    string RepoHash, string AgentId, AgentRefPublishOutcome Outcome,
    string? OldSha = null, string? NewSha = null, string? Reason = null)
{
    /// <summary>True when the mirror ends up carrying the agent's tip (whether or not it moved).</summary>
    public bool Current => Outcome is AgentRefPublishOutcome.Published or AgentRefPublishOutcome.Unchanged;

    /// <summary>True when policy stopped the update — the interesting half for logs and audits.</summary>
    public bool Refused => Outcome is AgentRefPublishOutcome.RefusedNonFastForward or AgentRefPublishOutcome.RefusedTarget;
}

/// <summary>
/// MG-3 stage 2 — the ONE path by which anything an agent produced reaches the shared mirror.
///
/// <para><b>The shape is the control.</b> The agent pushes to its own repository; the daemon
/// <i>fetches</i> from it and names the source ref and the destination itself. With a push-to-daemon
/// model the agent proposes ref updates (<c>old new refname</c>) and the daemon has to validate every
/// one — right namespace, fast-forward, not a delete — forever, correctly. Here the agent cannot name
/// a ref at all: the authorization question is structurally absent rather than enforced by checks.
/// Given MG-3 exists precisely because a <i>config-enforced</i> quarantine turned out not to cover
/// direct writes, preferring structure over checks is the lesson applied.</para>
///
/// <para><b>The four rules are nevertheless enforced here, in code rather than config</b> (design §4),
/// because "the daemon computes the ref name" is only true while every line of this class keeps it
/// true:</para>
/// <list type="number">
///   <item>the destination is <c>refs/heads/agent/&lt;thatAgentId&gt;</c> — never another agent's
///   branch, never anything outside the <c>agent/</c> namespace;</item>
///   <item>fast-forward only — the mirror's existing tip must be an ancestor of the new one;</item>
///   <item>no deletes — an absent or unreadable source ref leaves the mirror's ref exactly where it
///   was, rather than being read as "remove it";</item>
///   <item><c>main</c> is never a valid target, asserted against the mirror's own HEAD rather than
///   against the literal name (a repo whose default is <c>master</c>, or anything else, is covered).</item>
/// </list>
///
/// <para><b>Why the fetch lands in a quarantine namespace first.</b> Fetching straight into
/// <c>refs/heads/agent/&lt;id&gt;</c> would make git's refspec semantics the policy — a leading <c>+</c>
/// silently turns the fast-forward rule off, and a future edit that adds one to fix an unrelated
/// annoyance would remove the control with nothing to notice. Landing in
/// <c>refs/mainguard/incoming/&lt;id&gt;</c> (a namespace no agent and no merge consumer reads) and then
/// deciding explicitly means the rules live in code that can be tested and cannot be turned off by a
/// character.</para>
///
/// <para>The final move is an <c>update-ref &lt;target&gt; &lt;new&gt; &lt;old&gt;</c> — a compare-and-swap
/// against the value we made the decision on, so a concurrent publish cannot be clobbered by a
/// decision taken against a stale read.</para>
/// </summary>
public sealed class AgentRefMediator
{
    /// <summary>Where an agent's tip lands before the daemon decides anything about it. Deliberately
    /// outside <c>refs/heads/</c>: nothing that consumes the merge queue's input ever sees it, and it
    /// cannot be confused for a branch.</summary>
    public const string QuarantineRefPrefix = "refs/mainguard/incoming/";

    private static readonly string ZeroSha = new('0', 40);

    private readonly AgentRepoManager _agentRepos;
    private readonly Func<string, string> _bareRepoPathFor;
    private readonly Action<AgentRefPublishResult>? _observer;

    /// <param name="agentRepos">Locates each agent's own repository (the fetch source).</param>
    /// <param name="bareRepoPathFor">repoHash → the shared mirror (the fetch destination).</param>
    /// <param name="observer">Receives every outcome; refusals are the interesting half.</param>
    public AgentRefMediator(
        AgentRepoManager agentRepos,
        Func<string, string> bareRepoPathFor,
        Action<AgentRefPublishResult>? observer = null)
    {
        _agentRepos = agentRepos ?? throw new ArgumentNullException(nameof(agentRepos));
        _bareRepoPathFor = bareRepoPathFor ?? throw new ArgumentNullException(nameof(bareRepoPathFor));
        _observer = observer;
    }

    /// <summary>Carries <c>refs/heads/agent/&lt;id&gt;</c> from the agent's own repository into the
    /// mirror, subject to the four rules. Never throws: an unreadable repo is an outcome, not an
    /// exception, because every caller is on a path (verification, a review, a watcher tick) that must
    /// not be taken down by housekeeping.</summary>
    public AgentRefPublishResult Publish(string repoHash, string agentId)
    {
        var result = PublishCore(repoHash, agentId);
        _observer?.Invoke(result);
        return result;
    }

    private AgentRefPublishResult PublishCore(string repoHash, string agentId)
    {
        string target, quarantine, source, barePath, agentRepoPath;
        try
        {
            // Rule 1, at the only place the destination is ever constructed: the ref name is a pure
            // function of the agent id, and the id itself went through AgentRepoLayout's charset gate.
            target = AgentRepoLayout.RefFor(agentId);
            source = target;
            quarantine = QuarantineRefPrefix + AgentRepoLayout.RequireAgentId(agentId);
            barePath = _bareRepoPathFor(repoHash);
            agentRepoPath = _agentRepos.PathFor(repoHash, agentId);
        }
        catch (Exception ex)
        {
            return new AgentRefPublishResult(repoHash, agentId, AgentRefPublishOutcome.RefusedTarget, Reason: ex.Message);
        }

        if (!Directory.Exists(barePath))
        {
            return new AgentRefPublishResult(
                repoHash, agentId, AgentRefPublishOutcome.Failed, Reason: $"no provisioned mirror at '{barePath}'");
        }

        if (!Directory.Exists(agentRepoPath))
        {
            // Rule 3: no source repository is not a delete request. Nothing changes.
            return new AgentRefPublishResult(
                repoHash, agentId, AgentRefPublishOutcome.NothingToPublish,
                Reason: $"agent repository '{agentRepoPath}' does not exist");
        }

        // Rule 4, checked against the mirror's OWN default branch rather than the literal "main": a
        // destination that collides with the branch the merge queue merges INTO is refused outright.
        var defaultRef = "refs/heads/" + DefaultBranch(barePath);
        if (string.Equals(target, defaultRef, StringComparison.Ordinal)
            || string.Equals(target, "refs/heads/main", StringComparison.Ordinal)
            || string.Equals(target, "refs/heads/master", StringComparison.Ordinal))
        {
            return new AgentRefPublishResult(
                repoHash, agentId, AgentRefPublishOutcome.RefusedTarget,
                Reason: $"'{target}' is the repository's integration branch; an agent may only advance {AgentRepoLayout.RefPrefix}<its own id>");
        }

        // Land the agent's tip somewhere policy-free first. The '+' is safe HERE and only here: the
        // quarantine ref is daemon-private, is read by nothing, and is deleted below.
        if (AgentGitCommand.TryRun(
                barePath, out _, "fetch", "--no-tags", agentRepoPath, $"+{source}:{quarantine}") != 0)
        {
            // The agent may simply not have created its branch yet (a jail that has never committed).
            // Either way nothing about the mirror's ref changes.
            TryDeleteRef(barePath, quarantine);
            return new AgentRefPublishResult(
                repoHash, agentId, AgentRefPublishOutcome.NothingToPublish,
                Reason: $"'{source}' could not be fetched from the agent repository");
        }

        try
        {
            var newSha = RevParse(barePath, quarantine);
            if (newSha.Length == 0)
            {
                return new AgentRefPublishResult(
                    repoHash, agentId, AgentRefPublishOutcome.NothingToPublish,
                    Reason: "the fetched ref resolved to nothing");
            }

            var oldSha = RevParse(barePath, target);
            if (string.Equals(oldSha, newSha, StringComparison.Ordinal))
            {
                return new AgentRefPublishResult(
                    repoHash, agentId, AgentRefPublishOutcome.Unchanged, oldSha, newSha);
            }

            // Rule 2: fast-forward only. `merge-base --is-ancestor` is git's own answer to exactly this
            // question, and it is asked about the value we are about to compare-and-swap against.
            if (oldSha.Length > 0
                && AgentGitCommand.TryRun(barePath, out _, "merge-base", "--is-ancestor", oldSha, newSha) != 0)
            {
                return new AgentRefPublishResult(
                    repoHash, agentId, AgentRefPublishOutcome.RefusedNonFastForward, oldSha, newSha,
                    $"{newSha[..Math.Min(8, newSha.Length)]} does not contain the mirror's current "
                    + $"{oldSha[..Math.Min(8, oldSha.Length)]}; the agent rewrote published history");
            }

            // CAS on the value the decision was taken against. The all-zero old value means "must not
            // exist", which is the correct assertion for a first publish.
            var expected = oldSha.Length > 0 ? oldSha : ZeroSha;
            if (AgentGitCommand.TryRun(barePath, out _, "update-ref", target, newSha, expected) != 0)
            {
                return new AgentRefPublishResult(
                    repoHash, agentId, AgentRefPublishOutcome.Failed, oldSha, newSha,
                    "the compare-and-swap on the mirror's ref lost; another publish moved it concurrently");
            }

            return new AgentRefPublishResult(repoHash, agentId, AgentRefPublishOutcome.Published, oldSha, newSha);
        }
        finally
        {
            TryDeleteRef(barePath, quarantine);
        }
    }

    private static void TryDeleteRef(string gitDir, string refName)
        => AgentGitCommand.TryRun(gitDir, out _, "update-ref", "-d", refName);

    private static string RevParse(string gitDir, string refName)
        => AgentGitCommand.TryRun(gitDir, out var output, "rev-parse", "--verify", "--quiet", refName) == 0
            ? output.Trim()
            : string.Empty;

    private static string DefaultBranch(string barePath)
    {
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
}

/// <summary>
/// MG-3 — the other half of the resolved fetch trigger (design §7, "both"): the daemon watches each
/// agent's own ref move and publishes on the spot.
///
/// <para>The pre-verification publish alone would make the merge queue correct but the product feel
/// dead — an agent's push would reach the mirror (and therefore the review cockpit, the host repo's
/// sync fetch and the queue projection) only when something asked for a verification. Watching keeps
/// the agent's own <c>git push</c> meaningful, and re-fetching before verification keeps the bytes that
/// get verified current rather than whatever the watcher last saw. Neither replaces the other.</para>
///
/// <para><b>One loop, not one thread per agent</b>, and a cheap tick: the snapshot is the loose ref
/// file's bytes plus <c>packed-refs</c>' size and timestamp, so an idle agent costs two <c>stat</c>s
/// per second rather than a <c>git</c> process. Only a changed snapshot spawns git.</para>
///
/// <para><see cref="PollOnce"/> is public and does one complete sweep, so the behaviour is testable
/// without sleeping on a background loop — a timing-dependent test is a flake generator, and a test
/// that sleeps "long enough" is exactly the kind that keeps passing after the loop stops running.</para>
/// </summary>
public sealed class AgentRefWatcher : IDisposable
{
    /// <summary>How often the loop sweeps. Fast enough that a push feels immediate, slow enough that six
    /// idle agents cost nothing measurable.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<(string RepoHash, string AgentId), string> _watched = new();
    private readonly AgentRefMediator _mediator;
    private readonly AgentRepoManager _agentRepos;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public AgentRefWatcher(AgentRefMediator mediator, AgentRepoManager agentRepos, TimeSpan? interval = null)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _agentRepos = agentRepos ?? throw new ArgumentNullException(nameof(agentRepos));
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Start watching an agent. Idempotent. The first tick after this always publishes, because
    /// the recorded snapshot starts empty — so an agent that committed before the watch began is not
    /// missed.</summary>
    public void Watch(string repoHash, string agentId)
    {
        _watched[(repoHash, agentId)] = string.Empty;
        EnsureLoopRunning();
    }

    /// <summary>Stop watching (teardown). Idempotent.</summary>
    public void Unwatch(string repoHash, string agentId) => _watched.TryRemove((repoHash, agentId), out _);

    /// <summary>The agents currently watched.</summary>
    public IReadOnlyCollection<(string RepoHash, string AgentId)> Watched => _watched.Keys.ToArrayShim();

    /// <summary>One complete sweep: publish for every watched agent whose ref snapshot changed. Returns
    /// the outcomes it produced (empty when nothing moved).</summary>
    public IReadOnlyList<AgentRefPublishResult> PollOnce()
    {
        var results = new List<AgentRefPublishResult>();
        foreach (var key in _watched.Keys)
        {
            string snapshot;
            try
            {
                snapshot = Snapshot(_agentRepos.PathFor(key.RepoHash, key.AgentId), key.AgentId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!_watched.TryGetValue(key, out var previous) || string.Equals(previous, snapshot, StringComparison.Ordinal))
            {
                continue;
            }

            var result = _mediator.Publish(key.RepoHash, key.AgentId);
            results.Add(result);

            // Record the snapshot only once the mirror actually caught up. A refusal or a transient
            // failure must stay pending, or a single bad tick would make the daemon stop trying — the
            // "it silently gave up" failure mode that looks identical to "nothing has changed".
            if (result.Current)
            {
                _watched.TryUpdate(key, snapshot, previous);
            }
        }

        return results;
    }

    /// <summary>
    /// The cheap change signal for one agent branch: the loose ref file's bytes, plus <c>packed-refs</c>'
    /// length and last-write time (a pack can move the ref without touching the loose file). Absent
    /// files contribute a stable marker rather than an exception, so "the agent has not committed yet"
    /// and "the agent's ref went away" are both just snapshots that differ from the next one.
    /// </summary>
    private static string Snapshot(string agentRepoPath, string agentId)
    {
        var loosePath = Path.Combine(agentRepoPath, "refs", "heads", "agent", agentId);
        var loose = File.Exists(loosePath) ? File.ReadAllText(loosePath).Trim() : "-";

        var packedPath = Path.Combine(agentRepoPath, "packed-refs");
        var packed = "-";
        if (File.Exists(packedPath))
        {
            var info = new FileInfo(packedPath);
            packed = info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                     + "@" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return loose + "|" + packed;
    }

    private void EnsureLoopRunning()
    {
        if (_loop is not null || _stop.IsCancellationRequested)
        {
            return;
        }

        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    PollOnce();
                }
                catch
                {
                    // A watcher must never be the thing that takes the daemon down.
                }

                try
                {
                    await Task.Delay(_interval, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}

internal static class WatcherCollectionShim
{
    // ICollection<T>.ToArray() without pulling System.Linq into a hot dictionary enumeration.
    internal static T[] ToArrayShim<T>(this ICollection<T> source)
    {
        var array = new T[source.Count];
        source.CopyTo(array, 0);
        return array;
    }
}

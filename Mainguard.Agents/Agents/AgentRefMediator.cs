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

    // One in-flight publish per agent. Bounded by the agents this mediator has ever published (one
    // mediator per WorktreeManager, one small object per agent id); see Publish for why it exists.
    private readonly ConcurrentDictionary<(string RepoHash, string AgentId), object> _gates = new();

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
    /// not be taken down by housekeeping.
    ///
    /// <para><b>One publish per agent at a time.</b> Design §7 resolved the fetch trigger to "both", so
    /// two publishes for the SAME agent overlapping is not an edge case — it is the normal shape: the
    /// watcher sweeps on its own clock while the merge queue and the review cockpit publish immediately
    /// before they read the mirror. Overlapped, nothing unsafe happens (every rule is re-checked against
    /// what was actually read, and the final move is still a compare-and-swap), but the outcome stops
    /// telling the truth: both publishes share the one quarantine ref
    /// <c>refs/mainguard/incoming/&lt;id&gt;</c> and each deletes it in its <c>finally</c>, so the loser
    /// either resolves a ref the winner already removed (<see cref="AgentRefPublishOutcome.NothingToPublish"/>,
    /// "the fetched ref resolved to nothing") or loses the CAS (<see cref="AgentRefPublishOutcome.Failed"/>)
    /// — for a mirror that is in fact carrying exactly the tip the caller asked for. That matters because
    /// <c>Current</c> is what <c>PublishAgentBranch</c> returns and what the watcher uses to decide
    /// whether the agent is caught up. Serializing here makes the answer match the mirror. The CAS is
    /// unchanged and still load-bearing: it is what covers a second <i>process</i>, which no lock can.</para>
    /// </summary>
    public AgentRefPublishResult Publish(string repoHash, string agentId)
    {
        var gate = _gates.GetOrAdd((repoHash, agentId), static _ => new object());
        AgentRefPublishResult result;
        lock (gate)
        {
            result = PublishCore(repoHash, agentId);
        }

        // Outside the lock: the observer is the audit/warning sink, and housekeeping must not hold a
        // publish gate while someone else's I/O runs.
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
/// What a probe of an agent's own repository actually ESTABLISHED — as opposed to
/// <see cref="Directory.Exists"/>, which answers <c>false</c> both for "it is not there" and for "I could
/// not tell" (permission denied, a transient I/O error, a path the OS refused). The distinction is
/// load-bearing wherever absence is read as a reason to stop doing something.
/// </summary>
public enum AgentRepoPresence
{
    /// <summary>The directory is there.</summary>
    Present,

    /// <summary>The filesystem answered, and the answer was that nothing is at that path.</summary>
    Absent,

    /// <summary>The filesystem did not answer. This is <b>not</b> evidence of absence.</summary>
    Unreadable,
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
/// that sleeps "long enough" is exactly the kind that keeps passing after the loop stops running.
/// <b>But <see cref="Watch"/> starts the loop</b>, so calling <see cref="PollOnce"/> on a watcher built
/// with a real interval means competing with the watcher's own sweep for the same snapshot delta — the
/// caller's sweep then sees nothing (the loop consumed the change) or collides with it. A caller that
/// needs its sweep to be the only mover must build the watcher with <see cref="DriveManually"/>.</para>
/// </summary>
public sealed class AgentRefWatcher : IDisposable
{
    /// <summary>How often the loop sweeps. Fast enough that a push feels immediate, slow enough that six
    /// idle agents cost nothing measurable.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Interval sentinel: run NO background sweep loop — the caller drives <see cref="PollOnce"/> itself
    /// and is therefore the only thing that can move the mirror.
    ///
    /// <para>This is not "the loop off for convenience". A caller that hand-cranks <see cref="PollOnce"/>
    /// while <see cref="Watch"/> has also started the 1 Hz loop is racing itself: the change signal is a
    /// snapshot delta, so whichever sweep gets there first consumes it and the other observes nothing —
    /// which under load turns "the ref moved" into an intermittent no-op. Making the absence of the loop
    /// explicit is what lets such a caller assert on its own sweep at all.</para>
    ///
    /// <para>The daemon never uses this, and the seam cannot hide a dead loop: the background sweep is
    /// covered on its own by a test that starts a real watcher, touches nothing else, and waits for the
    /// publish to arrive.</para>
    /// </summary>
    public static readonly TimeSpan DriveManually = Timeout.InfiniteTimeSpan;

    /// <summary>How many CONSECUTIVE probes must say "absent" before an agent is evicted from the sweep.
    /// One tick of corroboration costs a second of latency on a teardown nobody is watching; getting it
    /// wrong unwatches a live agent forever.</summary>
    private const int AbsencesBeforeEviction = 2;

    /// <summary>The <see cref="_absences"/> value that means "the last probe could not read the repo"
    /// (and has already been reported), as distinct from any count of established absences.</summary>
    private const int UnreadableMark = -1;

    private readonly ConcurrentDictionary<(string RepoHash, string AgentId), string> _watched = new();

    // Only holds entries for agents whose last probe was NOT Present; a healthy probe removes the entry,
    // so this is bounded by the agents currently in trouble rather than by the agents ever watched.
    private readonly ConcurrentDictionary<(string RepoHash, string AgentId), int> _absences = new();

    private readonly AgentRefMediator _mediator;
    private readonly AgentRepoManager _agentRepos;
    private readonly TimeSpan _interval;
    private readonly Action<string>? _warningSink;
    private readonly Func<string, AgentRepoPresence> _probe;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _loopGate = new();
    private Task? _loop;

    /// <param name="mediator">Publishes an agent's tip into the mirror.</param>
    /// <param name="agentRepos">Locates each agent's own repository.</param>
    /// <param name="interval">Sweep period, or <see cref="DriveManually"/> for no background loop.</param>
    /// <param name="warningSink">
    /// Receives the events that would otherwise be invisible: an agent evicted from the sweep, and a
    /// repository the daemon could not read. An agent that silently stops being watched is the one state
    /// this class can reach where work stops flowing and nothing anywhere says so.
    /// </param>
    /// <param name="presenceProbe">
    /// How the repository's presence is established; defaults to <see cref="ProbeRepo"/>. Injected so a
    /// test can simulate an I/O failure deterministically — the eviction path cannot be proven safe with
    /// a probe that only ever reports the two outcomes a temp directory can produce.
    /// </param>
    public AgentRefWatcher(
        AgentRefMediator mediator,
        AgentRepoManager agentRepos,
        TimeSpan? interval = null,
        Action<string>? warningSink = null,
        Func<string, AgentRepoPresence>? presenceProbe = null)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _agentRepos = agentRepos ?? throw new ArgumentNullException(nameof(agentRepos));
        _interval = interval ?? DefaultInterval;
        _warningSink = warningSink;
        _probe = presenceProbe ?? ProbeRepo;
    }

    /// <summary>
    /// Establishes whether an agent's repository is there, <b>distinguishing absence from an unanswered
    /// question</b>.
    ///
    /// <para><see cref="Directory.Exists"/> cannot be used for a decision like eviction: it returns
    /// <c>false</c> on ANY error — <c>EACCES</c> on the directory or a parent, a transient I/O error, a
    /// path the platform rejects — so a momentary filesystem hiccup under load is indistinguishable from
    /// a teardown. <see cref="File.GetAttributes(string)"/> asks the same one <c>stat</c>, but THROWS
    /// instead of collapsing: only the two not-found exceptions establish absence, and everything else is
    /// explicitly "I could not tell".</para>
    /// </summary>
    public static AgentRepoPresence ProbeRepo(string agentRepoPath)
    {
        try
        {
            // A plain file where a repository should be is not a repository to publish from; it is also
            // not something a retry will fix, so it counts as absence rather than as an unread answer.
            return File.GetAttributes(agentRepoPath).HasFlag(FileAttributes.Directory)
                ? AgentRepoPresence.Present
                : AgentRepoPresence.Absent;
        }
        catch (FileNotFoundException)
        {
            return AgentRepoPresence.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return AgentRepoPresence.Absent;
        }
        catch (Exception)
        {
            // UnauthorizedAccessException, IOException, PathTooLongException, a bad argument out of a
            // caller bug — none of them are evidence that the agent is gone.
            return AgentRepoPresence.Unreadable;
        }
    }

    /// <summary>Start watching an agent. Idempotent. The first tick after this always publishes, because
    /// the recorded snapshot starts empty — so an agent that committed before the watch began is not
    /// missed.</summary>
    public void Watch(string repoHash, string agentId)
    {
        _watched[(repoHash, agentId)] = string.Empty;

        // A fresh watch starts a fresh streak: whatever the last watch of this id saw (a half-counted
        // absence from its teardown, an unreadable mark already reported) must not count towards
        // evicting the agent now being watched.
        _absences.TryRemove((repoHash, agentId), out _);
        EnsureLoopRunning();
    }

    /// <summary>Stop watching (teardown). Idempotent.</summary>
    public void Unwatch(string repoHash, string agentId)
    {
        _watched.TryRemove((repoHash, agentId), out _);
        _absences.TryRemove((repoHash, agentId), out _);
    }

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
                var agentRepoPath = _agentRepos.PathFor(key.RepoHash, key.AgentId);
                if (!StillPresent(key, agentRepoPath))
                {
                    continue;
                }

                snapshot = Snapshot(agentRepoPath, key.AgentId);
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
    /// Whether this tick may go on to snapshot the agent's repository — and the ONE place an agent is
    /// evicted from the sweep.
    ///
    /// <para><b>Eviction has to be earned.</b> The reason to evict at all is cost: a repository that is
    /// really gone publishes <see cref="AgentRefPublishOutcome.NothingToPublish"/>, which is not
    /// <c>Current</c>, so its snapshot is never recorded and the entry would spawn a git process every
    /// tick for the life of the daemon (the swarm reconciler disposes an orphan by calling
    /// <c>RemoveAgentWorktree</c> directly, so nothing else unwatches it). But eviction is the one
    /// outcome this class has that is <b>not</b> self-correcting: every other non-<c>Current</c> outcome
    /// leaves the snapshot unrecorded so the next tick retries and the mirror converges — and an evicted
    /// agent has no next tick. So the cost argument justifies evicting a repository that is GONE; it
    /// justifies nothing about a repository we merely failed to read, which used to be the same
    /// <c>bool</c>.</para>
    ///
    /// <para>Two independent guards, because they fail differently: the probe refuses to turn an I/O
    /// error into an absence at all, and a corroborating second consecutive absence covers the residue
    /// (an error some platform reports as not-found, an unmount mid-sweep). Waiting one extra tick costs
    /// a second of git process on a dead agent; skipping the wait costs a live agent its watch, forever,
    /// silently.</para>
    /// </summary>
    private bool StillPresent((string RepoHash, string AgentId) key, string agentRepoPath)
    {
        switch (_probe(agentRepoPath))
        {
            case AgentRepoPresence.Present:
                _absences.TryRemove(key, out _);
                return true;

            case AgentRepoPresence.Unreadable:
                // Keep watching, and say so once per streak rather than every tick: a watch that is
                // being kept but can never publish is otherwise indistinguishable from an idle agent.
                if (!_absences.TryGetValue(key, out var state) || state != UnreadableMark)
                {
                    _absences[key] = UnreadableMark;
                    _warningSink?.Invoke(
                        $"MG-3: the ref watcher could not read agent repository '{agentRepoPath}' for agent "
                        + $"'{key.AgentId}' in repo '{key.RepoHash}'. The watch is KEPT — an unreadable "
                        + "repository is not evidence the agent is gone.");
                }

                return false;

            default:
                // Absent. Corroborate before acting: a single absence starts a streak, it does not evict.
                var absences = _absences.AddOrUpdate(key, 1, static (_, prior) => prior < 0 ? 1 : prior + 1);
                if (absences < AbsencesBeforeEviction)
                {
                    return false;
                }

                _watched.TryRemove(key, out _);
                _absences.TryRemove(key, out _);
                _warningSink?.Invoke(
                    $"MG-3: stopped watching agent '{key.AgentId}' in repo '{key.RepoHash}' — its repository "
                    + $"'{agentRepoPath}' was absent on {AbsencesBeforeEviction} consecutive sweeps. If that "
                    + "agent is still live, its commits now reach the mirror only when a verification or a "
                    + "review publishes them.");
                return false;
        }
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
        if (_interval == DriveManually || _loop is not null || _stop.IsCancellationRequested)
        {
            return;
        }

        // Watch() is called once per spawning agent and spawns run in parallel, so the check-then-assign
        // has to be atomic — two loops would double every sweep, and two sweeps of the same agent are
        // exactly the collision AgentRefMediator.Publish now has to serialize away.
        lock (_loopGate)
        {
            if (_loop is not null || _stop.IsCancellationRequested)
            {
                return;
            }

            var token = _stop.Token;
            _loop = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
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
                        await Task.Delay(_interval, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                    {
                        return;
                    }
                }
            });
        }
    }

    /// <summary>
    /// Stops the sweep loop and waits for the sweep in flight before releasing anything.
    ///
    /// <para>The wait is the point. Cancelling and returning leaves a <see cref="PollOnce"/> mid-git
    /// against directories the caller is usually about to delete (teardown, or a test's temp VM root),
    /// and disposing the <see cref="CancellationTokenSource"/> underneath a loop parked on
    /// <c>Task.Delay(_stop.Token)</c> throws <see cref="ObjectDisposedException"/> out of it — not the
    /// <see cref="OperationCanceledException"/> the loop is written to expect — faulting the task
    /// unobserved.</para>
    /// </summary>
    public void Dispose()
    {
        _stop.Cancel();

        Task? loop;
        lock (_loopGate)
        {
            loop = _loop;
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // A cancelled or faulted sweep is not a disposal failure.
        }

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

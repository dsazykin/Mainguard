using System;
using System.Collections.Generic;
using System.Linq;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>One repo's freeze mark for an agent: the pause-axis reason
/// <c>AgentSessionStore.MarkFrozen</c> wrote. Repo-scoped because an agent id is unique only inside a
/// repository (the external-PR intake names its sessions <c>pr-&lt;n&gt;</c>).</summary>
public sealed record RestartFrozenJail(string RepoHash, string Reason);

/// <summary>One repo's parked mid-rebase conflict for an agent — the persisted half of
/// <see cref="ParkedRebaseConflict"/>.</summary>
public sealed record RestartParkedConflict(
    string RepoHash,
    string WorktreePath,
    string MainBranch,
    IReadOnlyList<string> ConflictedPaths,
    DateTimeOffset ParkedAt);

/// <summary>
/// Everything the daemon has to remember about ONE agent id in order to let a human undo, from inside
/// the app, whatever the daemon did to that agent before it died.
///
/// <para>Keyed by agent id (owner decision), with the repo-scoped facts carried as sub-lists rather than
/// as separate rows: an agent id names one agent to the human pressing Resume, while the freeze, the
/// parking and the hand-back permit are per (repo, agent) because the id repeats across repositories.
/// One row per id keeps the file readable and keeps the kill switch's id-only contract expressible
/// without inventing a repo for it.</para>
/// </summary>
public sealed record AgentRestartRecord(string AgentId)
{
    /// <summary>A HUMAN holds this agent paused (<c>HumanPauseLedger</c>). Sticky by design: it outranks
    /// the kill switch's own release and it is what <c>UnpauseAgent</c> requires before it will thaw a
    /// jail, so losing it is what made an adopted paused jail unresumable.</summary>
    public bool HumanPaused { get; init; }

    /// <summary>The kill switch's fan-out reached this agent and has not released it
    /// (<c>KillSwitch._fannedOutTo</c>).</summary>
    public bool KillContained { get; init; }

    /// <summary>The containers <c>SandboxKillTarget</c> itself transitioned to paused — the ONLY ones its
    /// release is entitled to wake (its causation ledger).</summary>
    public IReadOnlyList<string> KillPausedContainers { get; init; } = Array.Empty<string>();

    /// <summary>The kill switch, not the spawn path, took this agent's terminal lock.</summary>
    public bool KillTookTerminalLock { get; init; }

    /// <summary>The kill switch, not the gateway's back-off, closed this agent's leader input gate.</summary>
    public bool KillClosedInputGate { get; init; }

    /// <summary>The pause axis, per repo (<c>AgentSessionStore._frozen</c>).</summary>
    public IReadOnlyList<RestartFrozenJail> Frozen { get; init; } = Array.Empty<RestartFrozenJail>();

    /// <summary>Worktrees parked mid-rebase, per repo (<c>RebaseConflictParkingStore</c>).</summary>
    public IReadOnlyList<RestartParkedConflict> Parked { get; init; } = Array.Empty<RestartParkedConflict>();

    /// <summary>Repos in which a human has handed this agent's conflict back to it to finish — the ref
    /// mediator's one-rewrite permit.</summary>
    public IReadOnlyList<string> HandedBackRepos { get; init; } = Array.Empty<string>();

    /// <summary>Nothing left to remember. Empty rows are dropped on write so the file tracks live state
    /// rather than every agent the daemon has ever seen.</summary>
    public bool IsEmpty =>
        !HumanPaused && !KillContained && Frozen.Count == 0 && Parked.Count == 0
        && HandedBackRepos.Count == 0 && KillPausedContainers.Count == 0
        && !KillTookTerminalLock && !KillClosedInputGate;
}

/// <summary>
/// The persistence seam behind the five ledgers a restart used to erase. See
/// <see cref="AgentRestartLedger"/> for why there is one of these rather than five.
/// </summary>
public interface IAgentRestartLedger
{
    /// <summary>Every remembered agent (rehydration).</summary>
    IReadOnlyList<AgentRestartRecord> LoadAll();

    /// <summary>One agent's row — an empty row, never null, so callers read facts rather than nullability.</summary>
    AgentRestartRecord Find(string agentId);

    /// <summary>Read-modify-write ONE row under the store's own lock. The mutation runs inside the lock so
    /// two ledgers writing different facts about the same agent cannot lose one of them to a
    /// read-then-write race — which, with five writers sharing one file, is the whole risk.</summary>
    void Update(string agentId, Func<AgentRestartRecord, AgentRestartRecord> mutate);

    /// <summary>The kill epoch whose containment is still outstanding, or null.</summary>
    string? KillEpochId { get; }

    /// <summary>Records (or with null, closes) the outstanding kill epoch.</summary>
    void SetKillEpochId(string? epochId);
}

/// <summary>
/// An <see cref="IAgentRestartLedger"/> that remembers nothing at all — every write is dropped and every
/// read is empty.
///
/// <para><b>This, and not <see cref="InMemoryAgentRestartLedger"/>, is what the test suites install
/// process-wide,</b> and the difference is not cosmetic. The five ledgers rehydrate from this store in
/// their constructors, so ONE remembering instance shared by an assembly means every rig built after the
/// first inherits the previous rig's state: a kill switch rehydrated another test's contained agent into
/// its fan-out set and then unpaused that test's container out from under it. Forgetting is exactly the
/// behaviour these ledgers had before they were durable, which is what every test predating them
/// asserts.</para>
/// </summary>
public sealed class NullAgentRestartLedger : IAgentRestartLedger
{
    /// <summary>The shared instance — it holds nothing, so there is nothing to keep apart.</summary>
    public static NullAgentRestartLedger Instance { get; } = new();

    public IReadOnlyList<AgentRestartRecord> LoadAll() => Array.Empty<AgentRestartRecord>();

    public AgentRestartRecord Find(string agentId) => new(agentId ?? string.Empty);

    public void Update(string agentId, Func<AgentRestartRecord, AgentRestartRecord> mutate)
    {
    }

    public string? KillEpochId => null;

    public void SetKillEpochId(string? epochId)
    {
    }
}

/// <summary>An <see cref="IAgentRestartLedger"/> that remembers for the life of the process but not
/// beyond it. For a test that wants the write-through behaviour without a file; note that it must be
/// scoped to ONE rig — see <see cref="NullAgentRestartLedger"/> for what sharing one costs.</summary>
public sealed class InMemoryAgentRestartLedger : IAgentRestartLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentRestartRecord> _rows = new(StringComparer.Ordinal);
    private string? _epoch;

    public IReadOnlyList<AgentRestartRecord> LoadAll()
    {
        lock (_gate) { return _rows.Values.ToList(); }
    }

    public AgentRestartRecord Find(string agentId)
    {
        lock (_gate)
        {
            return _rows.TryGetValue(agentId ?? string.Empty, out var row)
                ? row
                : new AgentRestartRecord(agentId ?? string.Empty);
        }
    }

    public void Update(string agentId, Func<AgentRestartRecord, AgentRestartRecord> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var id = agentId ?? string.Empty;
        lock (_gate)
        {
            var current = _rows.TryGetValue(id, out var row) ? row : new AgentRestartRecord(id);
            var next = mutate(current) with { AgentId = id };
            if (next.IsEmpty)
            {
                _rows.Remove(id);
            }
            else
            {
                _rows[id] = next;
            }
        }
    }

    public string? KillEpochId
    {
        get { lock (_gate) { return _epoch; } }
    }

    public void SetKillEpochId(string? epochId)
    {
        lock (_gate) { _epoch = string.IsNullOrEmpty(epochId) ? null : epochId; }
    }
}

/// <summary>
/// The durable <see cref="IAgentRestartLedger"/> — one JSON file, written with the same
/// write-to-temp-then-rename discipline as <see cref="JsonHeldTaskStore"/>, whose problem this is a
/// direct copy of.
///
/// <para>Reads fail to <b>nothing remembered</b>, which is the pre-existing restart behaviour: an
/// unreadable file leaves the daemon exactly as forgetful as it was before, never asserting a freeze or a
/// pause it cannot substantiate.</para>
/// </summary>
public sealed class JsonAgentRestartLedger : IAgentRestartLedger
{
    private readonly string _path;
    private readonly object _gate = new();

    public JsonAgentRestartLedger(string path)
        => _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>The file this ledger writes (surfaced so an operator can be told where to look).</summary>
    public string Path => _path;

    public IReadOnlyList<AgentRestartRecord> LoadAll()
    {
        lock (_gate)
        {
            return LoadLocked().Agents.Select(FromDto).ToList();
        }
    }

    public AgentRestartRecord Find(string agentId)
    {
        var id = agentId ?? string.Empty;
        lock (_gate)
        {
            var dto = LoadLocked().Agents.Find(a => string.Equals(a.AgentId, id, StringComparison.Ordinal));
            return dto is null ? new AgentRestartRecord(id) : FromDto(dto);
        }
    }

    public void Update(string agentId, Func<AgentRestartRecord, AgentRestartRecord> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var id = agentId ?? string.Empty;
        lock (_gate)
        {
            var doc = LoadLocked();
            var index = doc.Agents.FindIndex(a => string.Equals(a.AgentId, id, StringComparison.Ordinal));
            var current = index < 0 ? new AgentRestartRecord(id) : FromDto(doc.Agents[index]);
            var next = mutate(current) with { AgentId = id };
            if (next.IsEmpty)
            {
                if (index < 0)
                {
                    return; // nothing was remembered and nothing is now — do not touch the file
                }

                doc.Agents.RemoveAt(index);
            }
            else if (index < 0)
            {
                doc.Agents.Add(ToDto(next));
            }
            else
            {
                doc.Agents[index] = ToDto(next);
            }

            WriteLocked(doc);
        }
    }

    public string? KillEpochId
    {
        get { lock (_gate) { return string.IsNullOrEmpty(LoadLocked().KillEpochId) ? null : LoadLocked().KillEpochId; } }
    }

    public void SetKillEpochId(string? epochId)
    {
        lock (_gate)
        {
            var doc = LoadLocked();
            var next = string.IsNullOrEmpty(epochId) ? null : epochId;
            if (string.Equals(doc.KillEpochId, next, StringComparison.Ordinal))
            {
                return;
            }

            doc.KillEpochId = next;
            WriteLocked(doc);
        }
    }

    private LedgerDto LoadLocked()
    {
        if (!System.IO.File.Exists(_path))
        {
            return new LedgerDto();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<LedgerDto>(System.IO.File.ReadAllText(_path))
                   ?? new LedgerDto();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.IO.IOException
                                       or UnauthorizedAccessException)
        {
            return new LedgerDto();
        }
    }

    private void WriteLocked(LedgerDto doc)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            System.IO.File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(doc));
            System.IO.File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Same posture as the kill journal: an unwritable ledger must never take down the pause, the
            // kill or the parking it was only trying to remember. The cost is a forgotten fact after a
            // restart — exactly today's behaviour — not a failed containment.
        }
    }

    private static AgentRestartRecord FromDto(AgentDto d) => new(d.AgentId ?? string.Empty)
    {
        HumanPaused = d.HumanPaused,
        KillContained = d.KillContained,
        KillPausedContainers = d.KillPausedContainers ?? new List<string>(),
        KillTookTerminalLock = d.KillTookTerminalLock,
        KillClosedInputGate = d.KillClosedInputGate,
        Frozen = (d.Frozen ?? new List<FrozenDto>())
            .Select(f => new RestartFrozenJail(f.RepoHash ?? string.Empty, f.Reason ?? string.Empty))
            .Where(f => f.Reason.Length > 0)
            .ToList(),
        Parked = (d.Parked ?? new List<ParkedDto>())
            .Select(p => new RestartParkedConflict(
                p.RepoHash ?? string.Empty,
                p.WorktreePath ?? string.Empty,
                p.MainBranch ?? string.Empty,
                p.ConflictedPaths ?? new List<string>(),
                p.ParkedAt))
            .ToList(),
        HandedBackRepos = d.HandedBackRepos ?? new List<string>(),
    };

    private static AgentDto ToDto(AgentRestartRecord r) => new()
    {
        AgentId = r.AgentId,
        HumanPaused = r.HumanPaused,
        KillContained = r.KillContained,
        KillPausedContainers = r.KillPausedContainers.ToList(),
        KillTookTerminalLock = r.KillTookTerminalLock,
        KillClosedInputGate = r.KillClosedInputGate,
        Frozen = r.Frozen.Select(f => new FrozenDto { RepoHash = f.RepoHash, Reason = f.Reason }).ToList(),
        Parked = r.Parked.Select(p => new ParkedDto
        {
            RepoHash = p.RepoHash,
            WorktreePath = p.WorktreePath,
            MainBranch = p.MainBranch,
            ConflictedPaths = p.ConflictedPaths.ToList(),
            ParkedAt = p.ParkedAt,
        }).ToList(),
        HandedBackRepos = r.HandedBackRepos.ToList(),
    };

    private sealed class LedgerDto
    {
        public string? KillEpochId { get; set; }

        public List<AgentDto> Agents { get; set; } = new();
    }

    private sealed class AgentDto
    {
        public string? AgentId { get; set; }
        public bool HumanPaused { get; set; }
        public bool KillContained { get; set; }
        public List<string>? KillPausedContainers { get; set; }
        public bool KillTookTerminalLock { get; set; }
        public bool KillClosedInputGate { get; set; }
        public List<FrozenDto>? Frozen { get; set; }
        public List<ParkedDto>? Parked { get; set; }
        public List<string>? HandedBackRepos { get; set; }
    }

    private sealed class FrozenDto
    {
        public string? RepoHash { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class ParkedDto
    {
        public string? RepoHash { get; set; }
        public string? WorktreePath { get; set; }
        public string? MainBranch { get; set; }
        public List<string>? ConflictedPaths { get; set; }
        public DateTimeOffset ParkedAt { get; set; }
    }
}

/// <summary>
/// The daemon-wide restart ledger — <b>one</b> store behind the five memory-only ledgers whose loss is
/// audit finding F3: the human pause ledger, the kill switch's fan-out + causation ledger, the pause axis,
/// the mid-rebase conflict parking, and the conflict hand-back permit.
///
/// <para><b>Why one store and not five.</b> They are five facts about one question — "what did the daemon
/// do to this agent that a human now has to be able to undo?" — and every exit the audit found closed
/// needed several of them at once. Five files would also be five chances for a restart to rehydrate half a
/// state: a jail remembered as frozen with no record of who froze it is worse than a jail remembered as
/// nothing, because the first one refuses every release path while looking recoverable.</para>
///
/// <para><b>Why a process-wide default rather than a DI registration.</b> The five owners are constructed
/// in five different places, and one of them (<see cref="MergeQueueProvisioner.ParkedConflicts"/>) is a
/// field initializer with no constructor argument to thread. A single accessor is the only seam that
/// reaches all five without changing every one of their signatures — and it is replaceable, which is what
/// the test suites use so a shared file can never couple two tests together.</para>
/// </summary>
public static class AgentRestartLedger
{
    /// <summary>The file name under the daemon data root.</summary>
    public const string FileName = "mainguard-restart-ledger.json";

    private static readonly object Gate = new();
    private static IAgentRestartLedger? _process;

    /// <summary>The path the durable default resolves to: beside the daemon's other JSON stores, under
    /// the (test-relocatable) data root.</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), FileName);

    /// <summary>
    /// The ledger every one of the five owners writes through. Durable by default; resolved lazily so the
    /// data root can be relocated (which the test suites do in a module initializer) before first use.
    /// </summary>
    public static IAgentRestartLedger Process
    {
        get
        {
            lock (Gate)
            {
                return _process ??= new JsonAgentRestartLedger(DefaultPath);
            }
        }
    }

    /// <summary>
    /// Replaces the process ledger. <b>For test isolation only</b> — the daemon never calls this.
    ///
    /// <para>Both test assemblies install an <see cref="InMemoryAgentRestartLedger"/> from their module
    /// initializer, next to the data-root redirect and for the same reason: a single JSON file shared by
    /// every test in an assembly running in parallel would couple them through agent ids, and a test that
    /// wants durability constructs its own <see cref="JsonAgentRestartLedger"/> over its own temp file —
    /// which is also the honest shape for a restart test, since it is two stores over one path.</para>
    /// </summary>
    public static void UseForTests(IAgentRestartLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        lock (Gate)
        {
            _process = ledger;
        }
    }
}

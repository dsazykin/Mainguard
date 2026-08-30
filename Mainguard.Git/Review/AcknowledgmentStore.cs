using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mainguard.Git.Audit;

namespace Mainguard.Git.Review;

/// <summary>What kind of flagged item this is — each is a dedicated must-acknowledge row (P2-11 §3.3/§3.3a/§3.3b).</summary>
public enum FlaggedKind
{
    /// <summary>A flag-worthy <see cref="RiskCategory"/> hunk (ExecutableConfig / CiWorkflow / GitHooks / SecuritySensitivePath).</summary>
    RiskCategory,

    /// <summary>OPS SA-1 / F6: a file outside the managed worker's approved <c>TaskPlan.Scope</c>.</summary>
    OutOfApprovedScope,

    /// <summary>RT-D2: the branch's resolved verification command/config drifted from the main baseline.</summary>
    ChangedTestCommand,

    /// <summary>A lockfile row that adds an install-scripted dependency.</summary>
    LockfileScript,

    /// <summary>A lockfile row that introduces a dependency version with a known offline-OSV CVE.</summary>
    LockfileCve,

    /// <summary>
    /// A lockfile whose introduced dependencies were <b>not established to be advisory-free</b> — the
    /// offline snapshot was missing, unreadable or aged out, or the manifest itself could not be parsed.
    ///
    /// <para>Its own kind rather than an absent item, for the reason <c>ToolchainStatus.CouldNotCheck</c>
    /// exists: an absent item is an acknowledged item (an empty flagged set is
    /// <see cref="AcknowledgmentStore.AllAcknowledged"/>), so omitting it would report "we could not check
    /// this dependency for CVEs" as "this dependency has no known CVEs" — on the one screen where that
    /// distinction decides a merge.</para>
    /// </summary>
    LockfileAdvisoryUnknown,

    /// <summary>
    /// A departure from the approved plan's <c>Approach</c> that the WORKER declared at commit time.
    ///
    /// <para>The sibling of <see cref="OutOfApprovedScope"/>, for the half of an approval that is not a
    /// file list. A plan's scope is machine-comparable and is compared; its approach is prose and is not —
    /// so a worker whose approved approach said "no error handling, keep plain <c>a / b</c>" could ship a
    /// validation layer that changed three pre-existing functions with the scope honoured, the tests it
    /// wrote itself green, and nothing flagged. This row is the worker's own statement of what it did
    /// differently, put in front of the human beside the approach it departed from.</para>
    /// </summary>
    DeclaredDeviation,

    /// <summary>
    /// This branch's approved work carries <b>no deviation declaration at all</b> — the worker was never
    /// asked, or answered through a path that predates the question.
    ///
    /// <para>Its own kind rather than an absent item, for exactly the reason
    /// <see cref="LockfileAdvisoryUnknown"/> is: an omitted item is an acknowledged item, so silence here
    /// would report "nobody established whether this follows the approved approach" as "it does".</para>
    /// </summary>
    DeviationDeclarationMissing,
}

/// <summary>
/// One must-acknowledge flagged change. <see cref="ContentHash"/> is the SHA-256 of the item's underlying
/// diff content, so a new push that alters the change produces a new hash → the acknowledgment resets
/// (invariant 2). <see cref="Id"/> is stable within a hash so the panel can address an item by identity.
/// </summary>
public sealed record FlaggedChange(
    string Path,
    RiskCategory Category,
    FlaggedKind Kind,
    string ContentHash,
    string Detail)
{
    /// <summary>Stable within a flagged set: <c>kind|path|contentHash</c>. Item-by-item acks key on this.</summary>
    public string Id => $"{Kind}|{Path}|{ContentHash}";
}

/// <summary>
/// The acknowledgment ledger for one branch (P2-11 step 4). An acknowledgment binds to the <b>content hash
/// of the canonical flagged-hunk set</b> (paths + per-item content hashes): a new push produces a new hash
/// and <b>every</b> prior ack is invalid (edge row 5). Acks are <b>item-by-item</b> — there is deliberately
/// <b>no</b> "acknowledge all" method, so a single global checkbox is impossible by construction (a
/// rejection trigger). Each ack appends an <c>acknowledged_flagged_change</c> audit event (P2-15 chains it).
/// </summary>
public sealed class AcknowledgmentStore
{
    private readonly object _gate = new();
    private readonly IAuditLog _audit;
    private readonly string _agentId;
    private readonly HashSet<string> _acked = new(StringComparer.Ordinal);
    private IReadOnlyList<FlaggedChange> _items = Array.Empty<FlaggedChange>();
    private string _hash = ComputeHash(Array.Empty<FlaggedChange>());

    /// <param name="agentId">The branch/agent these acks belong to (carried into audit events).</param>
    /// <param name="audit">Audit sink for <c>acknowledged_flagged_change</c> events (in-memory by default).</param>
    public AcknowledgmentStore(string agentId, IAuditLog? audit = null)
    {
        _agentId = agentId ?? string.Empty;
        _audit = audit ?? new InMemoryAuditLog();
    }

    /// <summary>Raised (off the lock) whenever the flagged set or an ack changes so the UI can re-read.</summary>
    public event Action? Changed;

    /// <summary>The SHA-256 of the current flagged set. Acks are valid only for this hash.</summary>
    public string CurrentHash
    {
        get { lock (_gate) { return _hash; } }
    }

    /// <summary>The current flagged items (rank/kind order as supplied).</summary>
    public IReadOnlyList<FlaggedChange> Items
    {
        get { lock (_gate) { return _items; } }
    }

    /// <summary>How many prior acks the most recent <see cref="SetFlagged"/> invalidated (for the "N items reset" copy).</summary>
    public int LastResetCount { get; private set; }

    /// <summary>Count of items not yet acknowledged for the current hash.</summary>
    public int PendingCount
    {
        get { lock (_gate) { return _items.Count(i => !_acked.Contains(i.Id)); } }
    }

    /// <summary>True iff every current item is acknowledged (an empty set is trivially satisfied).</summary>
    public bool AllAcknowledged
    {
        get { lock (_gate) { return _items.All(i => _acked.Contains(i.Id)); } }
    }

    /// <summary>
    /// Installs the current flagged set. If its content hash differs from the previous set, every prior
    /// acknowledgment is cleared (they bound to the old hash) and <see cref="LastResetCount"/> records how
    /// many were lost.
    /// </summary>
    public void SetFlagged(IReadOnlyList<FlaggedChange> items)
    {
        var incoming = items ?? Array.Empty<FlaggedChange>();
        var newHash = ComputeHash(incoming);

        lock (_gate)
        {
            if (!string.Equals(newHash, _hash, StringComparison.Ordinal))
            {
                LastResetCount = _acked.Count;
                _acked.Clear();
                _hash = newHash;
            }
            else
            {
                LastResetCount = 0;
                // Same hash — keep acks, but drop any that no longer name a live item (defensive).
                var live = incoming.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
                _acked.RemoveWhere(id => !live.Contains(id));
            }

            _items = incoming.ToList();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Acknowledges a single item by <see cref="FlaggedChange.Id"/>. Returns true iff it was a live,
    /// not-yet-acknowledged item (which also appends the audit event). Unknown ids are ignored.
    /// </summary>
    public bool Acknowledge(string itemId)
    {
        FlaggedChange? item;
        string hash;
        lock (_gate)
        {
            item = _items.FirstOrDefault(i => i.Id == itemId);
            if (item is null || _acked.Contains(itemId))
            {
                return false;
            }

            _acked.Add(itemId);
            hash = _hash;
        }

        _audit.Append(new AuditEvent("acknowledged_flagged_change", new Dictionary<string, string>
        {
            ["agent"] = _agentId,
            ["item"] = itemId,
            ["path"] = item.Path,
            ["category"] = item.Category.ToString(),
            ["kind"] = item.Kind.ToString(),
            ["hash"] = hash,
        }));

        Changed?.Invoke();
        return true;
    }

    /// <summary>True iff <paramref name="itemId"/> is acknowledged for the current hash.</summary>
    public bool IsAcknowledged(string itemId)
    {
        lock (_gate) { return _acked.Contains(itemId); }
    }

    /// <summary>
    /// The canonical SHA-256 over the flagged set: items sorted by <c>(kind, path, contentHash)</c>, each
    /// serialized as <c>kind\npath\ncontentHash</c>. Order-independent and content-bound so any change to
    /// the flagged hunks yields a new hash while an unrelated (non-flagged) file change does not.
    /// </summary>
    public static string ComputeHash(IReadOnlyList<FlaggedChange> items)
    {
        var ordered = (items ?? Array.Empty<FlaggedChange>())
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ThenBy(i => i.ContentHash, StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var item in ordered)
        {
            sb.Append(item.Kind).Append('\n')
              .Append(item.Path).Append('\n')
              .Append(item.ContentHash).Append('\n')
              .Append("--\n");
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>SHA-256 (lower-case hex) of arbitrary content — the per-item <see cref="FlaggedChange.ContentHash"/> source.</summary>
    public static string HashContent(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

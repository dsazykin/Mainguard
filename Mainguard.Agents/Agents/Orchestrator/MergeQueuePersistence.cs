using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git;
using Mainguard.Git.Models;
namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>SQLite-backed <see cref="IMergeQueueStore"/> — durable queue state (daemon DB).</summary>
public sealed class DbMergeQueueStore : IMergeQueueStore
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly object _gate = new();

    public DbMergeQueueStore(Func<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public IReadOnlyList<MergeQueueRow> LoadAll(string repoHash)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.MergeQueueRows.Where(r => r.RepoHash == repoHash).OrderBy(r => r.Id).ToList();
        }
    }

    public void Save(MergeQueueRow row)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var existing = db.MergeQueueRows.FirstOrDefault(r => r.RepoHash == row.RepoHash && r.AgentId == row.AgentId);
            if (existing is null)
            {
                db.MergeQueueRows.Add(new MergeQueueRow
                {
                    RepoHash = row.RepoHash,
                    AgentId = row.AgentId,
                    State = row.State,
                    LastVerificationId = row.LastVerificationId,
                    UpdatedUtc = row.UpdatedUtc,
                    VerifiedAtUtc = row.VerifiedAtUtc,
                    Origin = row.Origin,
                    DiscardedBy = row.DiscardedBy,
                    DiscardedAtUtc = row.DiscardedAtUtc,
                    DiscardReason = row.DiscardReason,
                });
            }
            else
            {
                existing.State = row.State;
                existing.LastVerificationId = row.LastVerificationId;
                existing.UpdatedUtc = row.UpdatedUtc;
                existing.VerifiedAtUtc = row.VerifiedAtUtc;
                existing.Origin = row.Origin;
                // Copied on every save, not only on the discard transition, so the record and the state it
                // belongs to can never drift apart in the row.
                existing.DiscardedBy = row.DiscardedBy;
                existing.DiscardedAtUtc = row.DiscardedAtUtc;
                existing.DiscardReason = row.DiscardReason;
            }

            // The transition and its persistence commit as one SQLite transaction.
            db.SaveChanges();
        }
    }

    public void Delete(string repoHash, string agentId)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var existing = db.MergeQueueRows.FirstOrDefault(r => r.RepoHash == repoHash && r.AgentId == agentId);
            if (existing is null)
            {
                return;
            }

            db.MergeQueueRows.Remove(existing);
            db.SaveChanges();
        }
    }
}

/// <summary>
/// The persistence seam for the RT-D1 merge lease + idempotency record. One outstanding (unconfirmed)
/// lease per repo. The boot reconcile reads outstanding leases and replays the T-19 journal.
/// </summary>
public interface IMergeLeaseStore
{
    /// <summary>
    /// Takes a lease for a repo. Fails (returns null) if an unconfirmed lease is already outstanding.
    ///
    /// <para>K3 — the lease records the IDENTITY the merge is authorized for, not merely the fact that one
    /// is in flight: the agent, the <c>main@sha</c> it may fast-forward, and
    /// <paramref name="expectedBranchSha"/>, the <c>agent/&lt;id&gt;</c> tip the queue's verification was
    /// measured on. Every leg of the conversation re-checks that identity at the moment it acts, which is
    /// what makes this a compare-and-swap rather than a mutex. Pass <c>""</c> when the caller genuinely has
    /// no verified branch sha — it is read everywhere as "not measured", never as a mismatch.</para>
    /// </summary>
    MergeLeaseRow? TryBegin(
        string repoHash, string leaseId, string agentId, string expectedMainSha, string mainBranch,
        string expectedBranchSha = "");

    /// <summary>The outstanding (unconfirmed) lease for a repo, or null.</summary>
    MergeLeaseRow? GetOutstanding(string repoHash);

    /// <summary>Every outstanding (unconfirmed) lease across all repos (boot reconcile input).</summary>
    IReadOnlyList<MergeLeaseRow> AllOutstanding();

    /// <summary>Idempotently records the confirm outcome and releases the lease (safe to call twice).</summary>
    void Confirm(string repoHash, string leaseId, string postMergeSha);

    /// <summary>Releases a lease without recording a merge (crash-before-commit path).</summary>
    void Release(string repoHash, string leaseId);
}

/// <summary>In-memory <see cref="IMergeLeaseStore"/> for tests and the pre-persistence path.</summary>
public sealed class InMemoryMergeLeaseStore : IMergeLeaseStore
{
    private readonly object _gate = new();
    private readonly List<MergeLeaseRow> _rows = new();
    private long _nextId;

    public MergeLeaseRow? TryBegin(
        string repoHash, string leaseId, string agentId, string expectedMainSha, string mainBranch,
        string expectedBranchSha = "")
    {
        lock (_gate)
        {
            if (_rows.Any(r => r.RepoHash == repoHash && !r.Confirmed))
            {
                return null;
            }

            var row = new MergeLeaseRow
            {
                Id = ++_nextId,
                RepoHash = repoHash,
                LeaseId = leaseId,
                AgentId = agentId,
                ExpectedMainSha = expectedMainSha,
                ExpectedBranchSha = expectedBranchSha ?? string.Empty,
                MainBranch = mainBranch,
                Confirmed = false,
                BeginUtc = DateTime.UtcNow,
            };
            _rows.Add(row);
            return Clone(row);
        }
    }

    public MergeLeaseRow? GetOutstanding(string repoHash)
    {
        lock (_gate)
        {
            var row = _rows.FirstOrDefault(r => r.RepoHash == repoHash && !r.Confirmed);
            return row is null ? null : Clone(row);
        }
    }

    public IReadOnlyList<MergeLeaseRow> AllOutstanding()
    {
        lock (_gate)
        {
            return _rows.Where(r => !r.Confirmed).Select(Clone).ToList();
        }
    }

    public void Confirm(string repoHash, string leaseId, string postMergeSha)
    {
        lock (_gate)
        {
            var row = _rows.FirstOrDefault(r => r.RepoHash == repoHash && r.LeaseId == leaseId);
            if (row is null || row.Confirmed)
            {
                return; // idempotent.
            }

            row.Confirmed = true;
            row.PostMergeSha = postMergeSha;
        }
    }

    public void Release(string repoHash, string leaseId)
    {
        lock (_gate)
        {
            _rows.RemoveAll(r => r.RepoHash == repoHash && r.LeaseId == leaseId && !r.Confirmed);
        }
    }

    private static MergeLeaseRow Clone(MergeLeaseRow r) => new()
    {
        Id = r.Id,
        RepoHash = r.RepoHash,
        LeaseId = r.LeaseId,
        AgentId = r.AgentId,
        ExpectedMainSha = r.ExpectedMainSha,
        ExpectedBranchSha = r.ExpectedBranchSha,
        MainBranch = r.MainBranch,
        Confirmed = r.Confirmed,
        PostMergeSha = r.PostMergeSha,
        BeginUtc = r.BeginUtc,
    };
}

/// <summary>SQLite-backed <see cref="IMergeLeaseStore"/> — durable lease across a daemon crash (RT-D1).</summary>
public sealed class DbMergeLeaseStore : IMergeLeaseStore
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly object _gate = new();

    public DbMergeLeaseStore(Func<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public MergeLeaseRow? TryBegin(
        string repoHash, string leaseId, string agentId, string expectedMainSha, string mainBranch,
        string expectedBranchSha = "")
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            if (db.MergeLeaseRows.Any(r => r.RepoHash == repoHash && !r.Confirmed))
            {
                return null;
            }

            var row = new MergeLeaseRow
            {
                RepoHash = repoHash,
                LeaseId = leaseId,
                AgentId = agentId,
                ExpectedMainSha = expectedMainSha,
                ExpectedBranchSha = expectedBranchSha ?? string.Empty,
                MainBranch = mainBranch,
                Confirmed = false,
                BeginUtc = DateTime.UtcNow,
            };
            db.MergeLeaseRows.Add(row);
            db.SaveChanges();
            return row;
        }
    }

    public MergeLeaseRow? GetOutstanding(string repoHash)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.MergeLeaseRows.Where(r => r.RepoHash == repoHash && !r.Confirmed).OrderBy(r => r.Id).FirstOrDefault();
        }
    }

    public IReadOnlyList<MergeLeaseRow> AllOutstanding()
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.MergeLeaseRows.Where(r => !r.Confirmed).OrderBy(r => r.Id).ToList();
        }
    }

    public void Confirm(string repoHash, string leaseId, string postMergeSha)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var row = db.MergeLeaseRows.FirstOrDefault(r => r.RepoHash == repoHash && r.LeaseId == leaseId);
            if (row is null || row.Confirmed)
            {
                return; // idempotent.
            }

            row.Confirmed = true;
            row.PostMergeSha = postMergeSha;
            db.SaveChanges();
        }
    }

    public void Release(string repoHash, string leaseId)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var row = db.MergeLeaseRows.FirstOrDefault(r => r.RepoHash == repoHash && r.LeaseId == leaseId && !r.Confirmed);
            if (row is null)
            {
                return;
            }

            db.MergeLeaseRows.Remove(row);
            db.SaveChanges();
        }
    }
}

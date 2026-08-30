using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git;
using Mainguard.Git.Models;
namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The immutable verification-record store (P2-10 invariant 2). The API has <b>no update</b>: a re-run
/// only ever <see cref="Insert"/>s a new row, so a prior record is never mutated. Records are keyed to
/// the exact <c>main@sha</c> they ran against.
/// </summary>
public interface IVerificationStore
{
    /// <summary>Appends a new immutable record. Never updates an existing row.</summary>
    void Insert(string repoHash, VerificationRecord record);

    /// <summary>The most recent record for an agent (newest by id), or null if it has never been verified.</summary>
    VerificationRecord? Latest(string repoHash, string agentId);

    /// <summary>The id of the most recent stored row for an agent (for the queue's persisted pointer), or null.</summary>
    long? LastId(string repoHash, string agentId);

    /// <summary>Every record for an agent, oldest first (immutability assertions read the full history).</summary>
    IReadOnlyList<VerificationRecord> History(string repoHash, string agentId);
}

/// <summary>An in-memory <see cref="IVerificationStore"/> for tests and the pre-persistence path.</summary>
public sealed class InMemoryVerificationStore : IVerificationStore
{
    private readonly object _gate = new();
    private readonly List<(long Id, string RepoHash, VerificationRecord Record)> _rows = new();
    private long _nextId;

    public void Insert(string repoHash, VerificationRecord record)
    {
        lock (_gate)
        {
            _rows.Add((++_nextId, repoHash, record));
        }
    }

    public VerificationRecord? Latest(string repoHash, string agentId)
    {
        lock (_gate)
        {
            return _rows
                .Where(r => r.RepoHash == repoHash && r.Record.AgentId == agentId)
                .OrderByDescending(r => r.Id)
                .Select(r => r.Record)
                .FirstOrDefault();
        }
    }

    public long? LastId(string repoHash, string agentId)
    {
        lock (_gate)
        {
            var ids = _rows.Where(r => r.RepoHash == repoHash && r.Record.AgentId == agentId).Select(r => r.Id).ToList();
            return ids.Count == 0 ? null : ids.Max();
        }
    }

    public IReadOnlyList<VerificationRecord> History(string repoHash, string agentId)
    {
        lock (_gate)
        {
            return _rows
                .Where(r => r.RepoHash == repoHash && r.Record.AgentId == agentId)
                .OrderBy(r => r.Id)
                .Select(r => r.Record)
                .ToList();
        }
    }
}

/// <summary>SQLite-backed <see cref="IVerificationStore"/> — durable immutable records (daemon DB).</summary>
public sealed class DbVerificationStore : IVerificationStore
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly object _gate = new();

    public DbVerificationStore(Func<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public void Insert(string repoHash, VerificationRecord record)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            db.VerificationRows.Add(new VerificationRow
            {
                RepoHash = repoHash,
                AgentId = record.AgentId,
                MainSha = record.MainSha,
                BranchSha = record.BranchSha,
                Passed = record.Passed,
                LogArtifactPath = record.LogArtifactPath,
                ResolvedCommand = record.ResolvedCommand,
                ConfigHash = record.ConfigHash,
                WhenUtc = record.When.UtcDateTime,
            });
            db.SaveChanges();
        }
    }

    public VerificationRecord? Latest(string repoHash, string agentId)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var row = db.VerificationRows
                .Where(v => v.RepoHash == repoHash && v.AgentId == agentId)
                .OrderByDescending(v => v.Id)
                .FirstOrDefault();
            return row is null ? null : Map(row);
        }
    }

    public long? LastId(string repoHash, string agentId)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var ids = db.VerificationRows
                .Where(v => v.RepoHash == repoHash && v.AgentId == agentId)
                .Select(v => v.Id)
                .ToList();
            return ids.Count == 0 ? null : ids.Max();
        }
    }

    public IReadOnlyList<VerificationRecord> History(string repoHash, string agentId)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.VerificationRows
                .Where(v => v.RepoHash == repoHash && v.AgentId == agentId)
                .OrderBy(v => v.Id)
                .ToList()
                .Select(Map)
                .ToList();
        }
    }

    private static VerificationRecord Map(VerificationRow v) => new(
        v.AgentId, v.MainSha, v.Passed, v.LogArtifactPath, v.ResolvedCommand, v.ConfigHash,
        new DateTimeOffset(v.WhenUtc, TimeSpan.Zero),
        // Null-coalesced, not defaulted away: a pre-migration row genuinely has no branch sha, and an
        // empty string is exactly how this record type spells "not measured".
        v.BranchSha ?? string.Empty);
}

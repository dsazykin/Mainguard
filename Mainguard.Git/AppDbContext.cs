using System;
using System.IO;
using System.Linq;
using Mainguard.Git.Models;
using Microsoft.EntityFrameworkCore;

namespace Mainguard.Git;

public class AppDbContext : DbContext
{
    /// <summary>The SQLite file name under <see cref="MainguardPaths.DataRoot"/>. Named once so callers
    /// that must talk about the default database (the startup watchdog's stall diagnosis) cannot drift
    /// from the file this context actually opens.</summary>
    public const string DatabaseFileName = "mainguard.db";

    private readonly string? _dbPath;

    public DbSet<WorkspaceCategory> WorkspaceCategories { get; set; } = null!;
    public DbSet<Repository> Repositories { get; set; } = null!;
    public DbSet<PinnedRef> PinnedRefs { get; set; } = null!;
    public DbSet<JournalEntry> JournalEntries { get; set; } = null!;
    public DbSet<GitProfile> GitProfiles { get; set; } = null!;
    public DbSet<TosAcknowledgment> TosAcknowledgments { get; set; } = null!;
    public DbSet<SpendRecord> SpendRecords { get; set; } = null!;
    public DbSet<ExpectedAgent> ExpectedAgents { get; set; } = null!;
    public DbSet<GatewayBudget> GatewayBudgets { get; set; } = null!;
    public DbSet<MergeQueueRow> MergeQueueRows { get; set; } = null!;
    public DbSet<VerificationRow> VerificationRows { get; set; } = null!;
    public DbSet<MergeLeaseRow> MergeLeaseRows { get; set; } = null!;
    public DbSet<PrIntakeSubscriptionRow> PrIntakeSubscriptions { get; set; } = null!;
    public DbSet<PrIntakeHeadRow> PrIntakeHeads { get; set; } = null!;
    public DbSet<PrIntakeConfigRow> PrIntakeConfig { get; set; } = null!;
    public DbSet<AuditRecordRow> AuditRecords { get; set; } = null!;
    public DbSet<AuditAnchorRow> AuditAnchors { get; set; } = null!;

    public AppDbContext()
    {
        // MainguardPaths, not GetFolderPath: the latter returns "" on Unix for a not-yet-materialized
        // home subdir, silently producing a relative DB path (the mainguardd crash-loop class of bug).
        _dbPath = Path.Combine(MainguardPaths.DataRoot(), DatabaseFileName);
    }

    public AppDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var dbPath = _dbPath ?? Path.Combine(MainguardPaths.DataRoot(), DatabaseFileName);
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Explicit primary keys
        modelBuilder.Entity<WorkspaceCategory>().HasKey(c => c.CategoryId);
        modelBuilder.Entity<Repository>().HasKey(r => r.RepositoryId);

        // Configure CategoryId as foreign key with Cascade delete
        modelBuilder.Entity<Repository>()
            .HasOne(r => r.Category)
            .WithMany(c => c.Repositories)
            .HasForeignKey(r => r.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<WorkspaceCategory>()
            .HasOne(c => c.ParentCategory)
            .WithMany(c => c.SubCategories)
            .HasForeignKey(c => c.ParentCategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ensure Path is indexed
        modelBuilder.Entity<Repository>()
            .HasIndex(r => r.Path)
            .IsUnique();

        // Pinned refs (T-09): keyed by Id, one row per (repo, ref).
        modelBuilder.Entity<PinnedRef>().HasKey(p => p.Id);
        modelBuilder.Entity<PinnedRef>()
            .HasIndex(p => new { p.RepoPath, p.RefName })
            .IsUnique();

        // Operation journal (T-19): one row per mutating op, queried per repo newest-first.
        modelBuilder.Entity<JournalEntry>().HasKey(j => j.Id);
        modelBuilder.Entity<JournalEntry>()
            .HasIndex(j => j.RepoPath);

        // Git identity profiles (T-21): keyed by Id; names are unique (case-insensitive enforced in
        // ProfileService — a plain unique index would be case-sensitive under SQLite's default collation).
        modelBuilder.Entity<GitProfile>().HasKey(p => p.Id);

        // ToS acknowledgments (P2-01): one row per provider the user has acknowledged; queried by
        // provider (case handled by the HasTosAcknowledgment helper). Persists across restarts.
        modelBuilder.Entity<TosAcknowledgment>().HasKey(t => t.Id);
        modelBuilder.Entity<TosAcknowledgment>().HasIndex(t => t.Provider);

        // AI-gateway spend ledger (P2-08): one row per settled model-API request, queried per agent
        // (cost-per-merged-change) and per UTC day (budget caps). Indexed by agent.
        modelBuilder.Entity<SpendRecord>().HasKey(s => s.Id);
        modelBuilder.Entity<SpendRecord>().HasIndex(s => s.AgentId);

        // Swarm reconciler's expected-agents table (P2-08): Docker is the source of truth for liveness;
        // this table records what the daemon expected, so a dead container can be pruned and marked Dead.
        modelBuilder.Entity<ExpectedAgent>().HasKey(a => a.Id);
        modelBuilder.Entity<ExpectedAgent>().HasIndex(a => new { a.RepoHash, a.AgentId }).IsUnique();

        // Gateway budget caps (P2-08): a single persisted row set via SetBudgets, read by GetBudgets.
        modelBuilder.Entity<GatewayBudget>().HasKey(b => b.Id);

        // Merge-queue state (P2-10): one row per (repo, agent), written in the same transaction as every
        // state-machine transition so a daemon restart resumes queue state (never stuck).
        modelBuilder.Entity<MergeQueueRow>().HasKey(m => m.Id);
        modelBuilder.Entity<MergeQueueRow>().HasIndex(m => new { m.RepoHash, m.AgentId }).IsUnique();

        // Verification records (P2-10): immutable — re-runs INSERT new rows; the store has no update.
        // Queried per (repo, agent) newest-first for the current record + provenance.
        modelBuilder.Entity<VerificationRow>().HasKey(v => v.Id);
        modelBuilder.Entity<VerificationRow>().HasIndex(v => new { v.RepoHash, v.AgentId });

        // Merge lease + idempotency (P2-10 / RT-D1): one outstanding lease per repo; the boot reconcile
        // replays the T-19 journal for any unconfirmed lease before admitting a new BeginMerge.
        modelBuilder.Entity<MergeLeaseRow>().HasKey(l => l.Id);
        modelBuilder.Entity<MergeLeaseRow>().HasIndex(l => l.RepoHash);

        // External-PR intake (P2-12): subscriptions unique on (host, owner, repo, filter) so a duplicate
        // subscribe is idempotent; the seen-head rows are keyed by (source, PR number).
        modelBuilder.Entity<PrIntakeSubscriptionRow>().HasKey(s => s.Id);
        modelBuilder.Entity<PrIntakeSubscriptionRow>()
            .HasIndex(s => new { s.Host, s.Owner, s.Repo, s.AuthorFilter }).IsUnique();
        modelBuilder.Entity<PrIntakeHeadRow>().HasKey(h => h.Id);
        modelBuilder.Entity<PrIntakeHeadRow>().HasIndex(h => new { h.SourceKey, h.PrNumber }).IsUnique();
        // The intake's daemon-wide configuration — one row, upserted by a constant id. No value
        // generation on the key: the store names the id, it is never allocated.
        modelBuilder.Entity<PrIntakeConfigRow>().HasKey(c => c.Id);
        modelBuilder.Entity<PrIntakeConfigRow>().Property(c => c.Id).ValueGeneratedNever();

        // P2-15 audit chain: Seq is the chain-assigned key (contiguous from 1, never DB-generated).
        // Append-only is enforced IN THE SCHEMA by the AddAuditChain migration's SQLite triggers —
        // any DELETE and any UPDATE other than the redaction tombstone transition RAISE(ABORT).
        modelBuilder.Entity<AuditRecordRow>().HasKey(a => a.Seq);
        modelBuilder.Entity<AuditRecordRow>().Property(a => a.Seq).ValueGeneratedNever();

        // P2-15 RFC 3161 anchors: one row per timestamped chain head; pending rows have no token
        // yet and are retried best-effort (never blocking the chain). Queried by head seq.
        modelBuilder.Entity<AuditAnchorRow>().HasKey(a => a.Id);
        modelBuilder.Entity<AuditAnchorRow>().HasIndex(a => a.HeadSeq).IsUnique();

        // Seed some initial default categories
        modelBuilder.Entity<WorkspaceCategory>().HasData(
            new WorkspaceCategory { CategoryId = 1, Name = "Personal", DisplayOrder = 1 },
            new WorkspaceCategory { CategoryId = 2, Name = "Work", DisplayOrder = 2 }
        );
    }

    /// <summary>
    /// True when the user has already acknowledged the given provider's ToS (P2-01). Case-insensitive
    /// on the provider name. P2-15 chains off this so the CLI-OAuth path can skip a re-prompt.
    /// </summary>
    public bool HasTosAcknowledgment(string provider) =>
        TosAcknowledgments.Any(t => t.Provider.ToLower() == provider.ToLower());
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Mainguard.Agents;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.Services;
using Mainguard.Git;
using Mainguard.Git.Services;
using Xunit;
namespace Mainguard.Tests;

/// <summary>
/// P2-10 Windows-side foreground merge (plan §6 tests 8,9,10 + TI-P2-10 10,11). A5 ref-level CAS,
/// T-19 journaled/undoable merge, the always-<c>--ignore-scripts</c> post-merge install canary, and the
/// RT-D1 crash-mid-merge exactly-once-or-none reconciliation.
/// </summary>
public class ForegroundMergeServiceTests : IDisposable
{
    private readonly List<string> _dirs = new();

    // ---- Minimal IAgentEnvironment: only ResolveSyncRemote is exercised (SC-2 resolution). ----
    private sealed class FakeAgentEnvironment : IAgentEnvironment
    {
        private readonly SyncRemote _remote;
        public FakeAgentEnvironment(string name, string url) => _remote = new SyncRemote(name, url);
        public string SubstrateId => "test";
        public SubstrateCapabilities Capabilities => throw new NotSupportedException();
        public IRepoProvisioner Repos => throw new NotSupportedException();
        public IAgentWorktreeManager Worktrees => throw new NotSupportedException();
        public ISandboxEngine Sandboxes => throw new NotSupportedException();
        public IEgressPolicy Egress => throw new NotSupportedException();
        public SyncRemote ResolveSyncRemote(string repoHash) => _remote;
    }

    private string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _dirs.Add(path);
        return path;
    }

    private static void Git(string repo, params string[] args)
    {
        var (code, _, err) = GitService.RunGit(repo, args);
        if (code != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({code}): {err}");
        }
    }

    private static string Rev(string repo, string reference)
    {
        var (_, output, _) = GitService.RunGit(repo, "rev-parse", "--verify", reference);
        return output.Trim();
    }

    /// <summary>Builds a repo on branch main with an agent/&lt;id&gt; branch one commit ahead of main.</summary>
    private (string RepoPath, string RepoHash, string MainSha, string SyncName, string SyncUrl) BuildRepo(
        string agentId = "x", bool withLockfile = false)
    {
        var repo = NewDir("mainguard-fmerge-");
        Git(repo, "-c", "init.defaultBranch=main", "init");
        Git(repo, "config", "user.name", "T");
        Git(repo, "config", "user.email", "t@mainguard.local");
        Git(repo, "config", "commit.gpgsign", "false");

        File.WriteAllText(Path.Combine(repo, "README.md"), "seed\n");
        if (withLockfile)
        {
            File.WriteAllText(Path.Combine(repo, "package.json"),
                "{\"name\":\"x\",\"scripts\":{\"postinstall\":\"node -e \\\"require('fs').writeFileSync('POISON','x')\\\"\"}}");
            File.WriteAllText(Path.Combine(repo, "package-lock.json"), "{}\n");
        }

        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "seed");
        var mainSha = Rev(repo, "main");

        // Agent branch: one commit ahead of main (a fast-forward of main — the verified-and-rebased shape).
        Git(repo, "checkout", "-b", $"agent/{agentId}");
        File.WriteAllText(Path.Combine(repo, "feature.txt"), "agent work\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "agent commit");
        Git(repo, "checkout", "main");

        // A bare mirror registered as the sync remote (fetch target).
        var bare = NewDir("mainguard-fmerge-bare-");
        Git(bare, "init", "--bare");
        Git(repo, "remote", "add", "mainguard-vm", bare);

        return (repo, "repohash", mainSha, "mainguard-vm", bare);
    }

    private ForegroundMergeService NewService(
        string syncName, string syncUrl,
        out InMemoryMergeLeaseStore leases,
        out OperationJournal journal,
        List<(string Agent, string Sha)>? merged = null,
        Func<string, IReadOnlyList<string>, int>? installRunner = null)
    {
        leases = new InMemoryMergeLeaseStore();
        var dbPath = Path.Combine(NewDir("mainguard-fmerge-db-"), "journal.db");
        Func<AppDbContext> factory = () => new AppDbContext(dbPath);
        using (var db = factory()) { db.Database.EnsureCreated(); }
        journal = new OperationJournal(factory);

        Action<string, string> onMerged = (a, s) => merged?.Add((a, s));
        return new ForegroundMergeService(
            new FakeAgentEnvironment(syncName, syncUrl), journal, leases,
            onMerged: onMerged, depsRefreshRunner: installRunner);
    }

    // ---- Journaled + undoable (A5 ff-only merge) -------------------------

    [Fact]
    public void ForegroundMerge_JournaledUndoable()
    {
        var repo = BuildRepo();
        var service = NewService(repo.SyncName, repo.SyncUrl, out _, out var journal);

        var result = service.MergeAgentBranch(new ForegroundMergeRequest(
            repo.RepoPath, repo.RepoHash, "x", repo.MainSha, "main"));

        Assert.True(result.Merged);
        Assert.NotEqual(repo.MainSha, result.NewMainSha);
        Assert.NotEqual(repo.MainSha, Rev(repo.RepoPath, "main"));

        // A T-19 Merge journal entry exists → undo restores the pre-merge main tip.
        var entry = journal.GetHistory(repo.RepoPath).First(e => e.Kind == JournalKinds.Merge);
        journal.Undo(repo.RepoPath, entry.Id);
        Assert.Equal(repo.MainSha, Rev(repo.RepoPath, "main"));
    }

    // ---- A5 ref-level CAS: main moved → no merge ------------------------

    [Fact]
    public void A5Cas_Lost_WhenMainMoved_NoMerge()
    {
        var repo = BuildRepo();

        // Main advances after the branch was verified against the OLD sha (a concurrent merge/push).
        File.WriteAllText(Path.Combine(repo.RepoPath, "other.txt"), "someone else\n");
        Git(repo.RepoPath, "add", "-A");
        Git(repo.RepoPath, "commit", "-m", "concurrent main move");
        var movedMain = Rev(repo.RepoPath, "main");
        Assert.NotEqual(repo.MainSha, movedMain);

        var service = NewService(repo.SyncName, repo.SyncUrl, out _, out _);
        var result = service.MergeAgentBranch(new ForegroundMergeRequest(
            repo.RepoPath, repo.RepoHash, "x", repo.MainSha /* stale expected */, "main"));

        Assert.False(result.Merged);
        Assert.True(result.CasLost);
        Assert.Equal(movedMain, Rev(repo.RepoPath, "main")); // main untouched
    }

    // ---- IgnoreScripts canary -------------------------------------------

    [Fact]
    public void PostMergeInstall_AlwaysIgnoreScripts_PoisonedPostinstallDoesNotExecute()
    {
        var repo = BuildRepo(withLockfile: true);
        var calls = 0;
        // A fake npm: it runs the poisoned postinstall (writes POISON) ONLY when --ignore-scripts is
        // absent. It also throws one EBUSY to exercise the NTFS retry wrapper.
        Func<string, IReadOnlyList<string>, int> fakeInstall = (dir, args) =>
        {
            calls++;
            if (calls == 1)
            {
                throw new IOException("EBUSY: resource busy or locked");
            }

            if (!args.Contains("--ignore-scripts"))
            {
                File.WriteAllText(Path.Combine(dir, "POISON"), "ran");
            }

            return 0;
        };

        var service = NewService(repo.SyncName, repo.SyncUrl, out _, out _, installRunner: fakeInstall);
        var result = service.MergeAgentBranch(new ForegroundMergeRequest(
            repo.RepoPath, repo.RepoHash, "x", repo.MainSha, "main"));

        Assert.True(result.Merged);
        Assert.True(calls >= 2); // the EBUSY retry was exercised
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "POISON"))); // postinstall never ran
    }

    // ---- RT-D1: crash-mid-merge exactly-once-or-none --------------------

    [Fact]
    public async System.Threading.Tasks.Task DaemonCrashMidMerge_CommittedButUnconfirmed_RecoversExactlyOnce()
    {
        var repo = BuildRepo();
        var merged = new List<(string, string)>();
        var service = NewService(repo.SyncName, repo.SyncUrl, out var leases, out var journal, merged);

        var request = new ForegroundMergeRequest(repo.RepoPath, repo.RepoHash, "x", repo.MainSha, "main");

        // BeginMerge + the journaled merge commit — then CRASH before ConfirmMerge.
        var lease = service.BeginMerge(request);
        Assert.NotNull(lease);
        var result = service.PerformJournaledMerge(request, lease!);
        Assert.True(result.Merged);
        var postSha = Rev(repo.RepoPath, "main");
        Assert.NotNull(leases.GetOutstanding(repo.RepoHash)); // lease still outstanding (unconfirmed)

        // A new BeginMerge is REFUSED while a lease is outstanding (freezes conflicting queue actions).
        Assert.Null(service.BeginMerge(request));

        // Boot reconcile: replay the journal, synthesize the missing ConfirmMerge, fire NotifyMainMoved.
        var reconciled = new List<(string RepoHash, string AgentId, string Sha)>();
        var reconcile = new MergeReconcileTask(
            leases, journal,
            resolveRepoPath: _ => repo.RepoPath,
            onMerged: (h, a, s) => reconciled.Add((h, a, s)));
        await reconcile.RunAsync(CancellationToken.None);

        Assert.Null(leases.GetOutstanding(repo.RepoHash));       // lease released (confirmed)
        Assert.Single(reconciled);                                // exactly once
        // MG-29: the callback carries the lease's repo hash so the daemon can resolve the queue
        // that owns this agent and fire the stale cascade on it.
        Assert.Equal((repo.RepoHash, "x", postSha), reconciled[0]);
        Assert.NotNull(service.BeginMerge(request));             // a new merge is now accepted
    }

    [Fact]
    public async System.Threading.Tasks.Task DaemonCrashMidMerge_NeverCommitted_ReleasesLease_SurfacesAttempt()
    {
        var repo = BuildRepo();
        var service = NewService(repo.SyncName, repo.SyncUrl, out var leases, out var journal);
        var request = new ForegroundMergeRequest(repo.RepoPath, repo.RepoHash, "x", repo.MainSha, "main");

        // Crash right after BeginMerge — the merge never committed (main unchanged, no Merge journal entry).
        var lease = service.BeginMerge(request);
        Assert.NotNull(lease);

        var merged = new List<(string, string, string)>();
        var interrupted = new List<(string, string)>();
        var reconcile = new MergeReconcileTask(
            leases, journal,
            resolveRepoPath: _ => repo.RepoPath,
            onMerged: (h, a, s) => merged.Add((h, a, s)),
            onInterrupted: (h, r) => interrupted.Add((h, r)));
        await reconcile.RunAsync(CancellationToken.None);

        Assert.Empty(merged);                                    // no merge synthesized
        Assert.Single(interrupted);                              // the interrupted attempt is surfaced
        Assert.Null(leases.GetOutstanding(repo.RepoHash));       // lease released
        Assert.Equal(repo.MainSha, Rev(repo.RepoPath, "main"));  // main never moved
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Never fail a test from cleanup.
            }
        }
    }
}

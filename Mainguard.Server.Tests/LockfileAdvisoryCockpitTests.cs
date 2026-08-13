using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Git.Models;
using Mainguard.Git.Review;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
// Mainguard.Git.Models carries its own `Repository` projection; the seeding below wants libgit2's.
using Repository = LibGit2Sharp.Repository;

namespace Mainguard.Server.Tests;

/// <summary>
/// P2-11 §3.6 — the <b>semantic</b> lockfile review reaches the review cockpit.
///
/// <para><b>What was wrong.</b> A branch that changed a lockfile was flagged <i>by path only</i>. Which
/// package was added, whether it carries a known advisory, and whether it ships install scripts were
/// computed nowhere in the running product: <see cref="LockfileSemanticDiff"/> and <see cref="OsvSnapshot"/>
/// both shipped complete and tested, and the only property that would have carried their output —
/// <c>ReviewCockpitContext.LockfileFlags</c> — is read solely on the cockpit's LOCAL composition branch,
/// which the shipped app never takes because production always supplies <c>live:</c>. So assigning that
/// property would have changed nothing observable, and the semantic diff would still have been dead code
/// with a plausible-looking caller. The real wiring is daemon-side, in
/// <c>MergeQueueProvisioner.ArmFlaggedChangeReview</c>.</para>
///
/// <para><b>Why the assertions are on the ViewModel and not on the parser.</b> The parsers already have
/// tests and were never broken; the gap was every layer between them and a human. Each test here therefore
/// drives a REAL bare mirror, a REAL agent branch, the production <see cref="MergeQueueProvisioner"/>, the
/// real in-proc daemon, the shipped <see cref="DaemonBackedOrchestrator"/> projection, and finally the
/// <see cref="ReviewCockpitViewModel"/> a human actually reads — and the cockpit is deliberately handed an
/// <b>empty</b> merge diff, so anything appearing in its flagged panel provably travelled from the daemon
/// rather than being composed locally.</para>
/// </summary>
public sealed class LockfileAdvisoryCockpitTests : IDisposable
{
    private const string AgentId = "loom-lock";
    private const string ContainerId = "container-lock";
    private const string LockfilePath = "package-lock.json";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly string _vmRoot = NewDir("mainguard-lockcockpit-vm-");
    private readonly string _source = NewDir("mainguard-lockcockpit-src-");
    private readonly string _artifacts = NewDir("mainguard-lockcockpit-art-");

    /// <summary>
    /// <b>The decisive test.</b> The branch adds two dependencies to <c>package-lock.json</c>: one whose
    /// exact version carries a known advisory, and one that declares an install script. Neither fact is
    /// derivable from the path, and the second is the supply-chain case a path-level "lockfile changed" flag
    /// cannot tell apart from a patch bump — an added transitive that runs arbitrary code at install time.
    ///
    /// <para>Both must reach the cockpit as their own must-acknowledge rows, and the merge must be refused
    /// until a human clears them. Before the wiring, the cockpit's flagged panel was empty for this branch.</para>
    /// </summary>
    [Fact]
    public async Task AnAdvisoryCarryingLockfileChange_ReachesTheCockpit_AndBlocksTheMerge()
    {
        using var world = await LockfileWorld.StartAsync(this, HealthySnapshot(), BranchLockfileWithRisk());

        var cockpit = world.OpenCockpit();

        Assert.True(
            cockpit.FlaggedPanel.HasItems,
            "the cockpit's flagged panel was empty for a branch that added an advisory-carrying dependency");

        // (a) the CVE row — the package AND the advisory id, not "package-lock.json changed".
        var cve = Assert.Single(cockpit.FlaggedPanel.Items, i => i.Detail.Contains("CVE-2020-8203", StringComparison.Ordinal));
        Assert.Equal(LockfilePath, cve.Path);
        Assert.Equal(FlaggedKind.LockfileCve, cve.Kind);
        Assert.Contains("lodash", cve.Detail, StringComparison.Ordinal);
        Assert.False(cve.IsAcknowledged);

        // (b) the install-script row — the one a path-level flag can never produce.
        var script = Assert.Single(cockpit.FlaggedPanel.Items, i => i.Kind == FlaggedKind.LockfileScript);
        Assert.Equal(LockfilePath, script.Path);
        Assert.Contains("node-sass-legacy", script.Detail, StringComparison.Ordinal);
        Assert.Contains("install", script.Detail, StringComparison.OrdinalIgnoreCase);

        // (c) THE CONTROL that keeps the unknown-advisory tests below honest: a snapshot that CAN answer
        //     produces no unknown row. Without this, a gate that always raised "not checked" would pass them.
        Assert.DoesNotContain(cockpit.FlaggedPanel.Items, i => i.Kind == FlaggedKind.LockfileAdvisoryUnknown);

        // (d) the daemon refuses the merge, and the cockpit renders the daemon's own words.
        Assert.False(cockpit.CanMerge);
        Assert.Contains("acknowledgment", cockpit.MergeReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(world.Context.Queue.CanMerge(AgentId, out _));

        // (e) ...and the acknowledgment travels to the gate that is doing the refusing. A row the human can
        //     see but not clear would be a dead end on the one screen that has to end in a decision.
        await cve.AcknowledgeCommand.ExecuteAsync(null);
        await script.AcknowledgeCommand.ExecuteAsync(null);

        Assert.True(cve.IsAcknowledged);
        Assert.True(
            world.Context.Queue.CanMerge(AgentId, out var afterReason),
            $"the cockpit's acknowledgments never reached the daemon's gate — still: {afterReason}");
    }

    /// <summary>
    /// The branch's added dependency is <b>ordinary</b> — no advisory, no install script — but the offline
    /// snapshot is not there, so nothing was established about it.
    ///
    /// <para>The cockpit must say <i>unknown</i>. An omitted item is an acknowledged item (an empty flagged
    /// set is <see cref="AcknowledgmentStore.AllAcknowledged"/>), so staying silent would report "we could
    /// not check this dependency for CVEs" as "this dependency has no known CVEs" — the same
    /// unknown-rendered-as-fine defect <c>RttMeasured</c> and <c>CouldNotCheck</c> exist to prevent, on the
    /// one screen where it decides a merge.</para>
    /// </summary>
    [Fact]
    public async Task WithNoAdvisorySnapshot_TheCockpitReportsUnknown_NotClean()
    {
        using var world = await LockfileWorld.StartAsync(
            this, OsvSnapshot.Unavailable(), BranchLockfileWithOrdinaryAddition());

        var cockpit = world.OpenCockpit();

        Assert.True(
            cockpit.FlaggedPanel.HasItems,
            "the cockpit reported NOTHING for a lockfile change that was never checked against any advisory "
            + "database — and an absent item is an acknowledged one, i.e. it reads as reviewed and clean");

        var unknown = Assert.Single(cockpit.FlaggedPanel.Items, i => i.Kind == FlaggedKind.LockfileAdvisoryUnknown);
        Assert.Equal(LockfilePath, unknown.Path);
        Assert.Contains("NOT checked", unknown.Detail, StringComparison.Ordinal);
        Assert.Contains("missing", unknown.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("left-pad", unknown.Detail, StringComparison.Ordinal);

        // Unknown is not a footnote: it blocks, exactly as a real hit would, until a human takes it on.
        Assert.False(cockpit.CanMerge);
        Assert.False(world.Context.Queue.CanMerge(AgentId, out _));
    }

    /// <summary>
    /// Same branch, same ordinary dependency, and a snapshot that loaded perfectly — three years ago.
    ///
    /// <para>Its hits are still hits (a CVE does not stop existing), but its <i>silence</i> stopped being
    /// evidence somewhere in those three years, and a bundled database is guaranteed to reach that point
    /// because it is refreshed by shipping a build. So staleness widens the unknown rather than being
    /// treated as a clean answer, and the reviewer is told the capture date.</para>
    /// </summary>
    [Fact]
    public async Task WithAStaleAdvisorySnapshot_TheCockpitReportsUnknown_NotClean()
    {
        var stale = OsvSnapshot.FromEntries(
            Array.Empty<(string, string, IReadOnlyList<string>)>(),
            capturedOn: DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-3));

        using var world = await LockfileWorld.StartAsync(this, stale, BranchLockfileWithOrdinaryAddition());

        var cockpit = world.OpenCockpit();

        Assert.True(
            cockpit.FlaggedPanel.HasItems,
            "the cockpit reported NOTHING for a lockfile change checked against a three-year-old advisory "
            + "database — an absent item is an acknowledged one, i.e. it reads as reviewed and clean");

        var unknown = Assert.Single(cockpit.FlaggedPanel.Items, i => i.Kind == FlaggedKind.LockfileAdvisoryUnknown);
        Assert.Contains("NOT checked", unknown.Detail, StringComparison.Ordinal);
        Assert.Contains("older than 90 days", unknown.Detail, StringComparison.Ordinal);
        Assert.Contains(stale.SnapshotDate, unknown.Detail, StringComparison.Ordinal);

        Assert.False(cockpit.CanMerge);
        Assert.False(world.Context.Queue.CanMerge(AgentId, out _));
    }

    // ---- the world -------------------------------------------------------

    /// <summary>
    /// A real mirror + agent branch, the production provisioner registering its queue into the REAL
    /// daemon's registry, and the shipped client adapter pointed at it — i.e. every hop the flagged item
    /// has to survive between the blob in the mirror and the row a human reads.
    /// </summary>
    private sealed class LockfileWorld : IDisposable
    {
        private readonly DaemonFixture _daemon;
        private readonly DaemonClient _client;

        private LockfileWorld(
            DaemonFixture daemon, DaemonClient client, DaemonBackedOrchestrator adapter, MergeQueueContext context)
        {
            _daemon = daemon;
            _client = client;
            Adapter = adapter;
            Context = context;
        }

        public DaemonBackedOrchestrator Adapter { get; }

        public MergeQueueContext Context { get; }

        public static async Task<LockfileWorld> StartAsync(
            LockfileAdvisoryCockpitTests test, OsvSnapshot snapshot, string branchLockfile)
        {
            var repoHash = test.SeedAndProvision();
            test.CommitOnAgentBranch(repoHash, branchLockfile);

            var daemon = new DaemonFixture();
            _ = daemon.Token; // force one synchronous host build before the pumps race on it
            var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
            var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
            adapter.Start();

            // The production provisioner, registering into the DAEMON'S OWN registry and sharing its lease
            // singleton — so what the RPCs serve is the queue this built, not a stand-in beside it.
            var provisioner = test.NewProvisioner(daemon, snapshot);
            var context = provisioner.EnsureQueue(repoHash)
                ?? throw new InvalidOperationException("the provisioner built no queue for the seeded mirror");

            // Green, so nothing but the flagged gate can be what refuses the merge below. Arming the review
            // is a side effect of this call — the same cadence the RT-D2 gate is armed at.
            var record = await context.Queue.RunVerificationAsync(AgentId, CancellationToken.None);
            Assert.True(record.Passed, "the seeded branch did not verify green");

            adapter.SetActiveRepo(repoHash);

            // Let the queue stream settle so the cockpit is read against a converged projection — but
            // deliberately do NOT assert here. The claim under test is about what the human sees, so the
            // cockpit's own assertion must be the one that reports; a guard at this layer would fail first
            // and describe the plumbing instead of the surface.
            await WaitUntilAsync(() =>
                adapter.GetQueue().FirstOrDefault(e => e.AgentId == AgentId) is { FlaggedItems.Count: > 0 });

            return new LockfileWorld(daemon, client, adapter, context);
        }

        /// <summary>
        /// Builds the cockpit exactly as <c>ControlCenterViewModel.OpenReviewAsync</c> does — with one
        /// deliberate narrowing: the merge diff is EMPTY. The local composition branch is the only thing
        /// that could manufacture a flagged item client-side, and handing it nothing to work with makes
        /// every row in the panel provably the daemon's.
        /// </summary>
        public ReviewCockpitViewModel OpenCockpit() => new(
            new ReviewCockpitContext(AgentId, "Loom-Lock", $"agent/{AgentId}", Array.Empty<FilePatch>()),
            onMerge: _ => { },
            live: new DaemonFlaggedChangeSource(Adapter));

        public void Dispose()
        {
            Adapter.Dispose();
            _client.Dispose();
            _daemon.Dispose();
        }
    }

    // ---- fixtures --------------------------------------------------------

    /// <summary>A snapshot that CAN answer: today's date, carrying the advisory the branch walks into.</summary>
    private static OsvSnapshot HealthySnapshot() => OsvSnapshot.FromEntries(new[]
    {
        ("CVE-2020-8203", "lodash", (IReadOnlyList<string>)new[] { "4.17.15" }),
    });

    // The base manifest both branches start from: one benign, advisory-free dependency.
    private const string MainLockfile = """
    {
      "name": "app",
      "lockfileVersion": 3,
      "packages": {
        "": { "name": "app", "version": "1.0.0" },
        "node_modules/ms": { "version": "2.1.3", "resolved": "https://registry.npmjs.org/ms/-/ms-2.1.3.tgz" }
      }
    }
    """;

    /// <summary>Adds a version with a known advisory AND a package declaring an install script.</summary>
    private static string BranchLockfileWithRisk() => """
    {
      "name": "app",
      "lockfileVersion": 3,
      "packages": {
        "": { "name": "app", "version": "1.0.0" },
        "node_modules/ms": { "version": "2.1.3", "resolved": "https://registry.npmjs.org/ms/-/ms-2.1.3.tgz" },
        "node_modules/lodash": { "version": "4.17.15", "resolved": "https://registry.npmjs.org/lodash/-/lodash-4.17.15.tgz" },
        "node_modules/node-sass-legacy": {
          "version": "4.14.1",
          "resolved": "https://registry.npmjs.org/node-sass-legacy/-/node-sass-legacy-4.14.1.tgz",
          "hasInstallScript": true
        }
      }
    }
    """;

    /// <summary>Adds one ordinary dependency: no advisory, no install script. Whether it is clean is
    /// exactly what an absent or stale snapshot cannot say.</summary>
    private static string BranchLockfileWithOrdinaryAddition() => """
    {
      "name": "app",
      "lockfileVersion": 3,
      "packages": {
        "": { "name": "app", "version": "1.0.0" },
        "node_modules/ms": { "version": "2.1.3", "resolved": "https://registry.npmjs.org/ms/-/ms-2.1.3.tgz" },
        "node_modules/left-pad": { "version": "1.3.0", "resolved": "https://registry.npmjs.org/left-pad/-/left-pad-1.3.0.tgz" }
      }
    }
    """;

    /// <summary>Seeds a source repo carrying main's verify config + base lockfile, then provisions its mirror.</summary>
    private string SeedAndProvision()
    {
        Repository.Init(_source);
        using (var repo = new Repository(_source))
        {
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        }

        WriteAndCommit(_source, MergeQueueProvisioner.VerificationConfigPath, "npm test\n", "seed verify config");
        WriteAndCommit(_source, LockfilePath, MainLockfile, "seed lockfile");

        return new RepoProvisioner(_vmRoot).Provision(_source).RepoHash;
    }

    /// <summary>Lands the agent's work — a rewritten lockfile — on <c>agent/&lt;id&gt;</c>.</summary>
    private void CommitOnAgentBranch(string repoHash, string branchLockfile)
    {
        var worktree = new WorktreeManager(_vmRoot).CreateAgentWorktree(repoHash, AgentId);
        WriteAndCommit(worktree, LockfilePath, branchLockfile, "bump dependencies");
    }

    /// <summary>
    /// The production provisioner over the daemon's own registry and lease store. Only
    /// <paramref name="snapshot"/> is a test input; every other argument is what
    /// <c>GatewayServiceRegistration</c> passes.
    /// </summary>
    private MergeQueueProvisioner NewProvisioner(DaemonFixture daemon, OsvSnapshot snapshot) => new(
        registry: (MergeQueueRegistry)daemon.Services.GetRequiredService<IMergeQueueRegistry>(),
        repos: new RepoProvisioner(_vmRoot),
        leases: daemon.Services.GetRequiredService<IMergeLeaseStore>(),
        resolveContainerId: (_, _) => ContainerId,
        queueStore: _ => new InMemoryMergeQueueStore(),
        verificationStore: _ => new InMemoryVerificationStore(),
        sandboxes: new PassingSandboxEngine(),
        artifactDirectory: _artifacts,
        mergeDiff: new MergeBranchDiffService(
            new RepoProvisioner(_vmRoot),
            (repoHash, agentId) => new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, agentId)),
        publishAgentRef: (repoHash, agentId) => new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, agentId),
        checkAgentBranch: (repoHash, agentId) => new WorktreeManager(_vmRoot).CheckAgentBranch(repoHash, agentId),
        osvSnapshot: snapshot);

    private static void WriteAndCommit(string repoPath, string relPath, string content, string message)
    {
        var full = Path.Combine(repoPath, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, relPath);
        var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
        repo.Commit(message, sig, sig);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private static string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        TryDelete(_vmRoot);
        TryDelete(_source);
        TryDelete(_artifacts);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch { /* never fail a test from cleanup */ }
    }

    /// <summary>Reports a clean container-runtime exit — pass/fail is never what these tests are about.</summary>
    private sealed class PassingSandboxEngine : ISandboxEngine
    {
        public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "output", ""));

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnpauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

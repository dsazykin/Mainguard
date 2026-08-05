using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Git.Models;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The intake → spawn chain, at the tier where it can be driven without Docker: the id scheme, the MG-2
/// gates that decide whether an intake'd pull request may have a jail at all, and the per-source target
/// resolver that used to be the literal <c>_ =&gt; null</c>.
///
/// <para>The end-to-end proof that a jailed <c>pr-&lt;n&gt;</c> really verifies and merges lives in
/// <c>MergeQueueEndToEndDockerTests</c>; this tier pins the decisions that test cannot make visible —
/// what happens when a gate says no, and how a subscribed source finds its repository.</para>
/// </summary>
public sealed class ExternalPrIntakeSpawnWiringTests
{
    private const string UnprovisionedRepo = "repo-with-no-mirror";

    // ================= the id scheme =================

    /// <summary>
    /// <c>AgentSessionStore</c> could only mint GUIDs, so a session named <c>pr-9</c> — and therefore a
    /// jail named <c>pr-9</c> — was not expressible at all. The id has to come from the pull request
    /// because the SAME string is the worktree, the <c>agent/pr-9</c> branch, the container's
    /// <c>mainguard.agent</c> label, the package-cache directory and the merge-queue key.
    /// </summary>
    [Fact]
    public void Spawn_WithAnExplicitId_UsesIt_AndStillMintsOneWhenNoneIsGiven()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());

        var external = store.Spawn(
            ExternalPrIntake.WorkerAgentKind, AgentRoles.Managed, agentId: "pr-9", repoHash: "repo-a");
        Assert.Equal("pr-9", external.Id);
        Assert.Equal("pr-9", store.Find("repo-a", "pr-9")?.Id);

        var minted = store.Spawn("worker", repoHash: "repo-a");
        Assert.NotEqual("pr-9", minted.Id);
        Assert.Equal(32, minted.Id.Length); // still a GUID "N" for every other caller
    }

    /// <summary>
    /// A caller-chosen id can collide with a live session in a way a GUID never could. Overwriting would
    /// drop the running jail's container id, leaking the container, its MG-36 network segment and its
    /// package-cache lease with nothing left able to name them — so the store refuses instead, and the
    /// FIRST session is the one that survives. Scoping sessions by (repo, id) did not soften this: the
    /// duplicate that is refused is a duplicate of the FULL key.
    /// </summary>
    [Fact]
    public void Spawn_WithAnIdThatIsAlreadyLive_IsRefused_AndTheLiveSessionSurvives()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        store.Spawn(ExternalPrIntake.WorkerAgentKind, AgentRoles.Managed, agentId: "pr-9", repoHash: "repo-a");
        store.AttachSandbox("pr-9", "container-original", "repo-a");

        Assert.Throws<InvalidOperationException>(
            () => store.Spawn(
                ExternalPrIntake.WorkerAgentKind, AgentRoles.Managed, agentId: "pr-9", repoHash: "repo-a"));

        Assert.Equal("container-original", store.Find("repo-a", "pr-9")?.ContainerId);
        Assert.Single(store.List());
    }

    // ================= MG-2: the gates an arriving pull request faces =================

    /// <summary>
    /// A live worker is adopted, not respawned — otherwise every poll of an open pull request would burn a
    /// worker-cap slot and try to build a second jail over an existing worktree.
    /// </summary>
    [Fact]
    public async Task EnsureWorker_WhenThisDaemonAlreadyHasTheJail_IsAlreadyLive_AndSpawnsNothing()
    {
        using var daemon = new DaemonFixture();
        var store = daemon.Services.GetRequiredService<AgentSessionStore>();
        store.Spawn(ExternalPrIntake.WorkerAgentKind, AgentRoles.Managed, agentId: "pr-9", repoHash: "repo-a");
        store.AttachSandbox("pr-9", "container-9", "repo-a");

        var host = NewHost(daemon, resolveRunningJail: (_, _) => null);
        var result = await host.EnsureWorkerAsync("repo-a", "pr-9", 9, CancellationToken.None);

        Assert.Equal(PrWorkerOutcome.AlreadyLive, result.Outcome);
        Assert.Single(store.List());
    }

    /// <summary>
    /// <b>The defect this scoping fixes.</b> <c>pr-&lt;n&gt;</c> is the right id INSIDE a repo — worktree,
    /// branch, container labels and queue entry are all per-repo — but the session store was
    /// daemon-global, so two subscribed repositories that each had a pull request #n contended for one id.
    /// The second was refused BY NAME ("already taken by repo …") and its pull request was never intake'd
    /// at all. With sessions keyed by (repo, id) the second repo is simply a different session: it is not
    /// refused, and it does not adopt, evict or otherwise touch the first repo's jail.
    /// </summary>
    [Fact]
    public async Task EnsureWorker_WhenAnotherRepoHoldsTheSameId_IsNotRefusedForTheId_AndLeavesThatJailAlone()
    {
        using var daemon = new DaemonFixture();
        var store = daemon.Services.GetRequiredService<AgentSessionStore>();
        store.Spawn(ExternalPrIntake.WorkerAgentKind, AgentRoles.Managed, agentId: "pr-9", repoHash: "repo-a");
        store.AttachSandbox("pr-9", "container-repo-a", "repo-a");

        var host = NewHost(daemon, resolveRunningJail: (_, _) => null);
        var result = await host.EnsureWorkerAsync(UnprovisionedRepo, "pr-9", 9, CancellationToken.None);

        // It gets PAST the id and into the spawn chain, then fails for the honest reason (this handle has
        // no provisioned mirror). The old failure named the id; nothing may name it now.
        Assert.DoesNotContain("already taken", result.Reason ?? string.Empty);
        Assert.Contains("no provisioned mirror", result.Reason ?? string.Empty);

        // repo-a's jail is untouched — not adopted, not replaced, not stopped by the other repo's rollback.
        Assert.Equal("container-repo-a", store.Find("repo-a", "pr-9")?.ContainerId);
        Assert.Equal("repo-a", store.Find("repo-a", "pr-9")?.RepoHash);
    }

    /// <summary>
    /// Jails are persistent and the session store is memory-only, so after a daemon restart a
    /// <c>pr-&lt;n&gt;</c> container is still running with no session behind it. Adopting it by the
    /// container runtime's own labels — the same source of truth the merge queue resolves jails with — is
    /// what keeps a restarted daemon from trying, and failing, to spawn a duplicate on every poll.
    /// </summary>
    [Fact]
    public async Task EnsureWorker_WhenOnlyTheContainerRuntimeKnowsTheJail_AdoptsIt()
    {
        using var daemon = new DaemonFixture();
        var store = daemon.Services.GetRequiredService<AgentSessionStore>();
        Assert.Null(store.Find("repo-a", "pr-9")); // no session at all — a restarted daemon

        var host = NewHost(daemon, resolveRunningJail: (repo, agent) =>
            repo == "repo-a" && agent == "pr-9" ? "container-9" : null);
        var result = await host.EnsureWorkerAsync("repo-a", "pr-9", 9, CancellationToken.None);

        Assert.Equal(PrWorkerOutcome.AlreadyLive, result.Outcome);
        Assert.Empty(store.List()); // adopted, not respawned
    }

    /// <summary>
    /// MG-3 — <b>adopting a jail has to start its ref sweep</b>, because adoption is by definition the case
    /// where <c>SandboxAgentLauncher.LaunchAsync</c> (the only place a spawn registers the watch) did not
    /// run. An adopted worker that is never swept has its <c>refs/heads/agent/pr-&lt;n&gt;</c> move with
    /// nothing publishing it into the mirror, so the review cockpit, the merge-queue projection and the
    /// stale cascade sit on a tip the worker already moved past until some verification forces a fetch.
    /// Nothing errors, which is why it needs an assertion rather than a log line.
    ///
    /// <para>The boot <c>SwarmReconciler</c> does not answer for this. Its container listing is best-effort
    /// with no retry — an unreachable Docker becomes an EMPTY list and the boot failure is swallowed so the
    /// daemon serves anyway — so a daemon that starts before dockerd is ready watches nothing and never
    /// sweeps again, while this poll loop keeps adopting. And boot runs once, so it cannot cover a jail
    /// adopted mid-life: a release whose teardown half-fails leaves the container up, the session removed
    /// and the ref unwatched, which is exactly the state the next poll adopts.</para>
    /// </summary>
    [Fact]
    public async Task EnsureWorker_WhenItAdoptsAStillRunningJail_HandsItToTheRefWatcher()
    {
        using var daemon = new DaemonFixture();
        var worktrees = new RecordingWorktrees();

        var host = NewHost(
            daemon,
            resolveRunningJail: (repo, agent) => repo == "repo-a" && agent == "pr-9" ? "container-9" : null,
            worktrees: worktrees);
        var result = await host.EnsureWorkerAsync("repo-a", "pr-9", 9, CancellationToken.None);

        Assert.Equal(PrWorkerOutcome.AlreadyLive, result.Outcome);
        Assert.Equal(new[] { ("repo-a", "pr-9") }, worktrees.Watched);
    }

    /// <summary>
    /// The control for the assertion above: an "already live" answer that comes from THIS daemon's session
    /// store is not an adoption — that jail was spawned in-process, so <c>LaunchAsync</c> already watched
    /// it. Without this, a host that watched unconditionally on every early return would pass the adopt
    /// test for the wrong reason.
    /// </summary>
    [Fact]
    public async Task EnsureWorker_WhenTheSessionStoreAlreadyHoldsTheJail_StartsNoWatchHere()
    {
        using var daemon = new DaemonFixture();
        var store = daemon.Services.GetRequiredService<AgentSessionStore>();
        store.Spawn(ExternalPrIntake.WorkerAgentKind, AgentRoles.Managed, agentId: "pr-9", repoHash: "repo-a");
        store.AttachSandbox("pr-9", "container-9", "repo-a");
        var worktrees = new RecordingWorktrees();

        var host = NewHost(daemon, resolveRunningJail: (_, _) => null, worktrees: worktrees);
        var result = await host.EnsureWorkerAsync("repo-a", "pr-9", 9, CancellationToken.None);

        Assert.Equal(PrWorkerOutcome.AlreadyLive, result.Outcome);
        Assert.Empty(worktrees.Watched);
    }

    /// <summary>
    /// The #281 / #284 scoping rule, on the adopt path: the watch is keyed on BOTH labels. A bare
    /// <c>pr-7</c> is unique only INSIDE a repository — two subscribed repos each with an open pull request
    /// #7 is ordinary — so one repo's adoption must register its own <c>(repo, agent)</c> and never stand
    /// in for the other's. Each is adopted from its own jail and each is swept separately.
    /// </summary>
    [Fact]
    public async Task EnsureWorker_AdoptingTheSameIdInTwoRepos_WatchesEachRepoSeparately()
    {
        using var daemon = new DaemonFixture();
        var worktrees = new RecordingWorktrees();

        // Both repos really have a jail named pr-7; only the (repo, agent) pair tells them apart.
        var host = NewHost(
            daemon,
            resolveRunningJail: (repo, agent) => agent == "pr-7" ? $"container-{repo}" : null,
            worktrees: worktrees);

        Assert.Equal(
            PrWorkerOutcome.AlreadyLive,
            (await host.EnsureWorkerAsync("repo-a", "pr-7", 7, CancellationToken.None)).Outcome);
        Assert.Equal(
            PrWorkerOutcome.AlreadyLive,
            (await host.EnsureWorkerAsync("repo-b", "pr-7", 7, CancellationToken.None)).Outcome);

        Assert.Equal(
            new[] { ("repo-a", "pr-7"), ("repo-b", "pr-7") },
            worktrees.Watched.OrderBy(w => w.Repo).ToArray());
    }

    /// <summary>A ref watcher that throws must not fail the poll. The intake loop runs unattended over an
    /// untrusted source: one bad housekeeping call may not cost the pull request its adoption, which is
    /// what keeps the daemon from trying to spawn a duplicate jail on every poll thereafter.</summary>
    [Fact]
    public async Task EnsureWorker_WhenTheRefWatcherThrows_StillAdoptsTheJail()
    {
        using var daemon = new DaemonFixture();

        var host = NewHost(
            daemon,
            resolveRunningJail: (_, _) => "container-9",
            worktrees: new ThrowingWatchWorktrees());
        var result = await host.EnsureWorkerAsync("repo-a", "pr-9", 9, CancellationToken.None);

        Assert.Equal(PrWorkerOutcome.AlreadyLive, result.Outcome);
        Assert.Empty(daemon.Services.GetRequiredService<AgentSessionStore>().List());
    }

    /// <summary>
    /// <b>The worker cap applies to intake spawns, over the same population.</b> An arriving bot pull
    /// request is a spawn request the machine did not ask for, so it draws from the one
    /// <c>MaxActiveWorkers</c> allowance that already caps a coordinator's fan-out rather than a private
    /// one. Filling that pool with managed workers must refuse the intake — which is the whole reason the
    /// <c>pr-&lt;n&gt;</c> worker spawns Managed rather than under a role of its own.
    /// </summary>
    [Fact]
    public async Task EnsureWorker_AtTheManagedWorkerCap_IsRefused_AndLeavesNoSessionBehind()
    {
        using var daemon = new DaemonFixture();
        var store = daemon.Services.GetRequiredService<AgentSessionStore>();
        var limits = new CoordinatorLimits(MaxActiveWorkers: 2);

        // The pool filled by ORDINARY managed workers — not by pull requests. The point is that the two
        // kinds of daemon-driven spawn contend for the same allowance.
        store.Spawn("claude-code", AgentRoles.Managed);
        store.Spawn("claude-code", AgentRoles.Managed);

        var host = NewHost(daemon, limits: limits, resolveRunningJail: (_, _) => null);
        var result = await host.EnsureWorkerAsync("repo-a", "pr-9", 9, CancellationToken.None);

        Assert.Equal(PrWorkerOutcome.Refused, result.Outcome);
        Assert.False(result.HasJail);
        Assert.Contains("cap", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(store.Find("repo-a", "pr-9"));
        Assert.Equal(2, store.List().Count);
    }

    /// <summary>Under the cap the gate admits, and the refusal above was really the cap rather than
    /// anything else about the request — the same call, one slot freer, gets past it.</summary>
    [Fact]
    public async Task EnsureWorker_BelowTheManagedWorkerCap_IsNotRefusedByTheCap()
    {
        using var daemon = new DaemonFixture();
        var store = daemon.Services.GetRequiredService<AgentSessionStore>();
        store.Spawn("claude-code", AgentRoles.Managed);

        var host = NewHost(
            daemon, limits: new CoordinatorLimits(MaxActiveWorkers: 2), resolveRunningJail: (_, _) => null);
        var result = await host.EnsureWorkerAsync(UnprovisionedRepo, "pr-9", 9, CancellationToken.None);

        // It gets THROUGH the cap and into the spawn chain; it then fails for the honest reason (this
        // handle has no provisioned mirror, so there is no jail to be had) rather than being refused.
        Assert.NotEqual(PrWorkerOutcome.Refused, result.Outcome);
    }

    /// <summary>
    /// A repo with no provisioned mirror yields a session-only record and NO jail. An entry admitted on
    /// that basis would answer "no live sandbox" to every verification for the rest of its life, so the
    /// host reports failure and — the part that matters — leaves no session behind holding a cap slot.
    /// </summary>
    [Fact]
    public async Task EnsureWorker_WhenTheRepoHasNoMirror_Fails_AndReclaimsTheSession()
    {
        using var daemon = new DaemonFixture();
        var store = daemon.Services.GetRequiredService<AgentSessionStore>();

        var host = NewHost(daemon, resolveRunningJail: (_, _) => null);
        var result = await host.EnsureWorkerAsync(UnprovisionedRepo, "pr-9", 9, CancellationToken.None);

        Assert.Equal(PrWorkerOutcome.Failed, result.Outcome);
        Assert.False(result.HasJail);
        Assert.Null(store.Find(UnprovisionedRepo, "pr-9"));
        Assert.DoesNotContain(store.List(), s => s.Role == AgentRoles.Managed);
    }

    // ================= the target resolver that used to be `_ => null` =================

    /// <summary>
    /// A subscribed source finds its repository by that repository's OWN origin remote, read out of real
    /// git — the subscription does not get to assert which repo it belongs to. Without this the daemon
    /// could not name the repo behind a handle at all (the hash is one-way), which is why the resolver was
    /// hardwired to null and the intake materialized nothing in production however many sources were
    /// subscribed.
    ///
    /// <para>Two active repos on purpose, so "it found the right one" is a real question. The returned
    /// target carries the handle and path of <c>acme/app</c> and the live queue for that handle, which is
    /// the whole tuple a poll needs.</para>
    /// </summary>
    [Fact]
    public void TargetResolver_MatchesASourceToTheRepoWhoseOriginItReallyIs()
    {
        using var world = new ResolverWorld();
        var app = world.SeedRepo("https://github.com/acme/app.git");
        var other = world.SeedRepo("https://github.com/acme/other.git");

        var target = world.Resolver.Resolve(new ExternalPrSource("github.com", "acme", "app", null));

        Assert.NotNull(target);
        Assert.Equal(app.Handle, target!.RepoHash);
        Assert.Equal(app.Path, target.RepoPath);
        Assert.NotEqual(other.Handle, target.RepoHash);
        Assert.NotNull(target.Queue);

        // …and the OTHER source resolves to the other repo, so the match is on the remote rather than on
        // whichever repo happens to sort first.
        var otherTarget = world.Resolver.Resolve(new ExternalPrSource("github.com", "acme", "other", null));
        Assert.Equal(other.Handle, otherTarget!.RepoHash);
    }

    /// <summary>
    /// The negatives that make the match mean something: a source nobody's origin names resolves to null,
    /// and a null target is a list-and-skip — never a crash, and never a materialization against the wrong
    /// repository. Each row differs from the matching source in exactly one component.
    /// </summary>
    [Theory]
    [InlineData("github.com", "acme", "not-this-repo")]  // right host + owner, wrong repo
    [InlineData("github.com", "someone-else", "app")]    // right host + repo, wrong owner
    [InlineData("gitlab.com", "acme", "app")]            // right slug, wrong host
    public void TargetResolver_ForASourceNoActiveRepoOwns_ResolvesToNull(string host, string owner, string repo)
    {
        using var world = new ResolverWorld();
        world.SeedRepo("https://github.com/acme/app.git");

        // The control: with the same world, the exactly-matching source DOES resolve — so a null below is
        // the mismatch and not a world that can never resolve anything.
        Assert.NotNull(world.Resolver.Resolve(new ExternalPrSource("github.com", "acme", "app", null)));
        Assert.Null(world.Resolver.Resolve(new ExternalPrSource(host, owner, repo, null)));
    }

    /// <summary>Host and slug comparison is case-insensitive, as both hosts and their slugs are.</summary>
    [Fact]
    public void TargetResolver_MatchesCaseInsensitively()
    {
        using var world = new ResolverWorld();
        var app = world.SeedRepo("https://github.com/Acme/App.git");

        var target = world.Resolver.Resolve(new ExternalPrSource("GitHub.com", "acme", "app", null));
        Assert.Equal(app.Handle, target?.RepoHash);
    }

    /// <summary>An empty index (nothing provisioned yet) is the boot state, and it must resolve to null
    /// rather than throw — the poll loop starts before any repository is open.</summary>
    [Fact]
    public void TargetResolver_WithNoActiveRepos_ResolvesToNull()
    {
        using var world = new ResolverWorld();
        Assert.Null(world.Resolver.Resolve(new ExternalPrSource("github.com", "acme", "app", null)));
    }

    /// <summary>A repository that has gone away (or cannot be read) is not a match, and must not fault the
    /// whole resolve — one stale index entry would otherwise poison every source.</summary>
    [Fact]
    public void TargetResolver_WhenARepoCannotBeRead_SkipsIt_WithoutThrowing()
    {
        using var world = new ResolverWorld();
        world.Repos.Record("hash-gone", Path.Combine(Path.GetTempPath(), "mainguard-no-such-repo"));

        Assert.Null(world.Resolver.Resolve(new ExternalPrSource("github.com", "acme", "app", null)));
    }

    // ================= helpers =================

    /// <summary>
    /// Real repositories, real mirrors, real queues, on an isolated VM root — the resolver's inputs are
    /// the production ones (<c>GitService.GetRemotes</c> over a repo on disk, a
    /// <see cref="MergeQueueProvisioner"/> over a provisioned mirror), so a match is a match for the same
    /// reason it would be in the daemon.
    /// </summary>
    private sealed class ResolverWorld : IDisposable
    {
        private readonly List<string> _dirs = new();
        private readonly string _vmRoot;

        public ResolverWorld()
        {
            _vmRoot = NewDir("mainguard-prtarget-vm-");
            Repos = new ActiveRepoIndex();
            Resolver = new PrIntakeTargetResolver(
                Repos,
                new MergeQueueProvisioner(
                    registry: new MergeQueueRegistry(),
                    repos: new RepoProvisioner(_vmRoot),
                    leases: new InMemoryMergeLeaseStore(),
                    resolveContainerId: (_, _) => null,
                    queueStore: _ => new InMemoryMergeQueueStore(),
                    verificationStore: _ => new InMemoryVerificationStore(),
                    sandboxes: new NeverRunSandboxEngine(),
                    artifactDirectory: NewDir("mainguard-prtarget-artifacts-")),
                // The production remote reader, not a fake.
                path => new Mainguard.Git.Services.GitService().GetRemotes(path));
        }

        public ActiveRepoIndex Repos { get; }

        public PrIntakeTargetResolver Resolver { get; }

        /// <summary>A committed repository whose <c>origin</c> is <paramref name="originUrl"/>, provisioned
        /// into the mirror (so it has a queue) and recorded in the index (so it is active).</summary>
        public (string Handle, string Path) SeedRepo(string originUrl)
        {
            var path = NewDir("mainguard-prtarget-src-");
            Git(path, "-c", "init.defaultBranch=main", "init");
            Git(path, "config", "user.name", "test-user");
            Git(path, "config", "user.email", "test@mainguard.local");
            File.WriteAllText(Path.Combine(path, "README.md"), "seed\n");
            Git(path, "add", "-A");
            Git(path, "commit", "-m", "seed");
            Git(path, "remote", "add", "origin", originUrl);

            var handle = new RepoProvisioner(_vmRoot).Provision(path).RepoHash;
            Repos.Record(handle, path);
            return (handle, path);
        }

        public void Dispose()
        {
            foreach (var dir in _dirs)
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        private string NewDir(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            _dirs.Add(dir);
            return dir;
        }

        private static void Git(string workingDirectory, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
            }
        }
    }

    /// <summary>The resolver never verifies anything, so its engine must never be asked to run — if it
    /// ever is, that is a defect and this says so rather than quietly returning a green exit.</summary>
    private sealed class NeverRunSandboxEngine : Mainguard.Agents.Agents.Sandbox.ISandboxEngine
    {
        public Task<Mainguard.Agents.Agents.Sandbox.SandboxHandle> SpawnAsync(
            Mainguard.Agents.Agents.Sandbox.SandboxSpawnRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Mainguard.Agents.Agents.Sandbox.SandboxExecResult> ExecAsync(
            string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task PauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnpauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static ExternalPrWorkerHost NewHost(
        DaemonFixture daemon,
        Func<string, string, string?> resolveRunningJail,
        CoordinatorLimits? limits = null,
        IAgentWorktreeManager? worktrees = null)
        => new(
            spawns: daemon.Services.GetRequiredService<AgentSpawnService>(),
            sessions: daemon.Services.GetRequiredService<AgentSessionStore>(),
            launcher: daemon.Services.GetRequiredService<SandboxAgentLauncher>(),
            admission: daemon.Services.GetRequiredService<AdmissionController>(),
            limits: limits ?? daemon.Services.GetRequiredService<CoordinatorLimits>(),
            resolveRunningJail: resolveRunningJail,
            worktrees: worktrees ?? daemon.Services.GetRequiredService<IAgentEnvironment>().Worktrees,
            audit: daemon.Services.GetRequiredService<IAuditLog>(),
            loggerFactory: NullLoggerFactory.Instance);

    /// <summary>Records every <c>(repo, agent)</c> handed to the MG-3 ref sweep — the same seam
    /// <c>SandboxAgentLauncher</c> registers a SPAWNED agent through, so watching it here asserts the real
    /// wiring rather than a bookkeeping list this host keeps for itself.</summary>
    private sealed class RecordingWorktrees : IAgentWorktreeManager
    {
        public List<(string Repo, string Agent)> Watched { get; } = new();

        public string CreateAgentWorktree(string repoHash, string agentId) => $"/wt/{repoHash}/{agentId}";

        public void RemoveAgentWorktree(string repoHash, string agentId, bool force) { }

        public void Prune(string repoHash) { }

        public IReadOnlyList<WorktreeItem> List(string repoHash) => Array.Empty<WorktreeItem>();

        public void WatchAgentRef(string repoHash, string agentId) => Watched.Add((repoHash, agentId));
    }

    /// <summary>A substrate whose ref watcher is down. The adopt path must still adopt.</summary>
    private sealed class ThrowingWatchWorktrees : IAgentWorktreeManager
    {
        public string CreateAgentWorktree(string repoHash, string agentId) => string.Empty;

        public void RemoveAgentWorktree(string repoHash, string agentId, bool force) { }

        public void Prune(string repoHash) { }

        public IReadOnlyList<WorktreeItem> List(string repoHash) => Array.Empty<WorktreeItem>();

        public void WatchAgentRef(string repoHash, string agentId) =>
            throw new InvalidOperationException("the watcher is unavailable");
    }

    private static Func<string, IReadOnlyList<GitRemoteItem>> Remotes(Dictionary<string, string> originByPath)
        => path => originByPath.TryGetValue(path, out var url)
            ? new[] { new GitRemoteItem { Name = "origin", FetchUrl = url } }
            : Array.Empty<GitRemoteItem>();
}

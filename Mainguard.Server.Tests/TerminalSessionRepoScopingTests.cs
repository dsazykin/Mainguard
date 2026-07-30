using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// <b>A terminal session's identity is (repo, agent id), not the id.</b> The #281 scoping defect, in
/// <see cref="TerminalSessionManager"/> and the <see cref="AgentCliBinder"/> that feeds it.
///
/// <para>The external-PR intake names its workers <c>pr-&lt;n&gt;</c> after the pull-request number, so two
/// subscribed repositories that each have an open pull request #7 both run a <c>pr-7</c>. The manager kept
/// its bound sessions and its pending-bind flags in dictionaries keyed by the agent id alone, and that was
/// not a latent tidiness problem — external-PR workers spawn with <see cref="AgentRoles.Managed"/> through
/// the ordinary spawn chain, so they <b>do</b> bind a real in-jail CLI under a real PTY. What followed:</para>
/// <list type="bullet">
///   <item><see cref="TerminalSessionManager.Bind"/> replaces a same-key registration and <b>disposes what
///   it replaced</b>, and dispose kills the PTY — so repo B's worker binding its CLI killed the live CLI
///   process of repo A's still-running worker.</item>
///   <item>That kill was silent: the exit watcher reflected the death through the id-only
///   <c>MarkState(agentId, …)</c>, which is a deliberate no-op on an id two repos hold. Repo A's card
///   stayed <c>Working</c> over a dead CLI.</item>
///   <item>Every later attach for <c>pr-7</c> resolved to repo B's session, streaming one repository's
///   agent output — its diffs, its file contents, its reasoning — to the other repository's operator.</item>
///   <item><see cref="TerminalSessionManager.MarkBindPending"/>/<c>ClearBindPending</c> shared one flag, so
///   a failed spawn in repo B dropped repo A's in-flight attach into echo.</item>
///   <item>Teardown was guarded by "no session anywhere still answers to this id", so stopping repo A's
///   <c>pr-7</c> while repo B ran one released <b>nothing</b> — repo A's PTY, replay ring and pending flag
///   all leaked until repo B stopped too.</item>
/// </list>
///
/// <para>These tests assert the behaviour, not the key's shape: two repos each run <c>pr-7</c>, and neither
/// can kill, observe, unflag or release the other's terminal. The repo-less RPC boundary
/// (<c>TerminalService.Attach</c> carries an agent id and no repo) is covered too: an ambiguous id resolves
/// to NOTHING and the attach degrades to echo, exactly as <see cref="AgentSessionStore.Find(string)"/>
/// already does — never an arbitrary pick.</para>
/// </summary>
public sealed class TerminalSessionRepoScopingTests
{
    private const string RepoA = "aaaaaaaaaaaa1111";
    private const string RepoB = "bbbbbbbbbbbb2222";
    private const string SharedId = "pr-7";

    private static AgentSessionKey A => new(RepoA, SharedId);

    private static AgentSessionKey B => new(RepoB, SharedId);

    // ===================== the manager: two repos, one agent id =====================

    /// <summary>The defect, at its sharpest. Binding repo B's <c>pr-7</c> must not touch repo A's: keyed by
    /// the id alone, the second <see cref="TerminalSessionManager.Bind"/> replaced the first entry and
    /// disposed it, and <see cref="BoundTerminalSession.Dispose"/> kills the PTY — so intaking a pull
    /// request #7 in one repository killed the running worker CLI of a pull request #7 in another.</summary>
    [Fact]
    public void BindingOneRepos_pr7_DoesNotKillTheOtherRepos_pr7()
    {
        using var mgr = new TerminalSessionManager();
        var stubA = new StubSession();
        var stubB = new StubSession();

        mgr.Bind(A, new BoundTerminalSession(SharedId, stubA));
        mgr.Bind(B, new BoundTerminalSession(SharedId, stubB));

        Assert.False(stubA.Killed); // repo A's CLI is still running
        Assert.False(stubB.Killed);
    }

    /// <summary>…and both remain reachable, each only under its own repo. A third repo sees neither.</summary>
    [Fact]
    public void TheSameAgentId_InTwoRepos_IsTwoIndependentBoundSessions()
    {
        using var mgr = new TerminalSessionManager();
        var boundA = new BoundTerminalSession(SharedId, new StubSession());
        var boundB = new BoundTerminalSession(SharedId, new StubSession());

        mgr.Bind(A, boundA);
        mgr.Bind(B, boundB);

        Assert.Same(boundA, mgr.TryGetBound(A));
        Assert.Same(boundB, mgr.TryGetBound(B));
        Assert.Null(mgr.TryGetBound(new AgentSessionKey("cccccccccccc3333", SharedId)));
    }

    /// <summary>A re-spawn into the SAME repo's reused jail still replaces (and reaps) the old CLI — one
    /// CLI per (repo, agent id). The key narrows the identity; it does not weaken the replacement rule.</summary>
    [Fact]
    public void Rebinding_TheSameKey_StillReplacesAndKillsTheOldSession()
    {
        using var mgr = new TerminalSessionManager();
        var first = new StubSession();
        var second = new StubSession();

        mgr.Bind(A, new BoundTerminalSession(SharedId, first));
        var replacement = new BoundTerminalSession(SharedId, second);
        mgr.Bind(A, replacement);

        Assert.True(first.Killed);
        Assert.Same(replacement, mgr.TryGetBound(A));
    }

    /// <summary>Releasing repo A's <c>pr-7</c> (its StopAgent) leaves repo B's terminal live — and actually
    /// releases repo A's. Both halves matter: the id-keyed shape could only be made safe by refusing to
    /// release while any repo still held the id, which leaked the PTY it was supposed to reap.</summary>
    [Fact]
    public void Release_InOneRepo_ReapsOnlyThatRepos_Session()
    {
        using var mgr = new TerminalSessionManager();
        var stubA = new StubSession();
        var stubB = new StubSession();
        mgr.Bind(A, new BoundTerminalSession(SharedId, stubA));
        mgr.Bind(B, new BoundTerminalSession(SharedId, stubB));

        mgr.Release(A);

        Assert.True(stubA.Killed);            // repo A's PTY is actually reaped, not skipped
        Assert.Null(mgr.TryGetBound(A));
        Assert.False(stubB.Killed);           // repo B's is untouched
        Assert.NotNull(mgr.TryGetBound(B));
    }

    /// <summary>A failed/session-only spawn in one repo must not drop the other repo's in-flight attach
    /// into echo. One shared flag meant <c>ClearBindPending</c> in either repo cleared it for both.</summary>
    [Fact]
    public void ClearBindPending_InOneRepo_LeavesTheOtherReposFlagStanding()
    {
        using var mgr = new TerminalSessionManager();
        mgr.MarkBindPending(A);
        mgr.MarkBindPending(B);

        mgr.ClearBindPending(B);

        Assert.True(mgr.IsBindPending(A));
        Assert.False(mgr.IsBindPending(B));
    }

    /// <summary>The attach wait, cross-wired: repo A's operator attaches while its <c>pr-7</c> is still
    /// starting, and repo B's <c>pr-7</c> binds first. That bind must not satisfy repo A's wait — otherwise
    /// the attach latches onto another repository's live CLI stream for the rest of the session.</summary>
    [Fact]
    public async Task WaitForBound_InOneRepo_IsNotSatisfiedByTheOtherRepos_Bind()
    {
        var prevTimeout = TerminalSessionManager.BindWaitTimeout;
        var prevPoll = TerminalSessionManager.BindWaitPollInterval;
        TerminalSessionManager.BindWaitTimeout = TimeSpan.FromSeconds(10);
        TerminalSessionManager.BindWaitPollInterval = TimeSpan.FromMilliseconds(10);
        try
        {
            using var mgr = new TerminalSessionManager();
            mgr.MarkBindPending(A);
            var wait = mgr.WaitForBoundAsync(A, CancellationToken.None);

            var boundB = new BoundTerminalSession(SharedId, new StubSession());
            mgr.Bind(B, boundB);

            // Several poll intervals with repo B bound and repo A not: the wait must still be running.
            await Task.Delay(150);
            Assert.False(wait.IsCompleted);

            var boundA = new BoundTerminalSession(SharedId, new StubSession());
            mgr.Bind(A, boundA);

            Assert.Same(boundA, await wait);
        }
        finally
        {
            TerminalSessionManager.BindWaitTimeout = prevTimeout;
            TerminalSessionManager.BindWaitPollInterval = prevPoll;
        }
    }

    // ===================== the repo-less RPC boundary: unique-or-nothing =====================

    /// <summary>The <c>TerminalService.Attach</c>/<c>GetScrollback</c> wire contract carries an agent id and
    /// no repo, so the id-only lookup stays — resolving to the SOLE holder, bound or merely pending.</summary>
    [Fact]
    public void IdOnlyLookup_ResolvesTheSoleHolder()
    {
        using var mgr = new TerminalSessionManager();
        var bound = new BoundTerminalSession(SharedId, new StubSession());
        mgr.MarkBindPending(A);

        Assert.True(mgr.IsBindPending(SharedId));  // pending-only registrations resolve too
        Assert.Null(mgr.TryGetBound(SharedId));

        mgr.Bind(A, bound);
        Assert.Same(bound, mgr.TryGetBound(SharedId));
        Assert.False(mgr.IsBindPending(SharedId));
    }

    /// <summary>…and resolves to NOTHING when two repos hold the id, rather than picking one. This is the
    /// disclosure boundary: an arbitrary pick hands repo A's operator repo B's live CLI stream. Echo is
    /// wrong-but-inert; the wrong repository's terminal is wrong-and-live.</summary>
    [Fact]
    public void IdOnlyLookup_ResolvesToNothing_WhenTwoReposHoldTheId()
    {
        using var mgr = new TerminalSessionManager();
        mgr.Bind(A, new BoundTerminalSession(SharedId, new StubSession()));
        mgr.Bind(B, new BoundTerminalSession(SharedId, new StubSession()));

        Assert.Null(mgr.TryGetBound(SharedId));
        Assert.False(mgr.IsBindPending(SharedId));
    }

    /// <summary>Ambiguity is about two REPOS, not two dictionaries: a re-spawn marks a pending bind while
    /// the previous CLI is still bound, so one key legitimately appears in both. That must not read as a
    /// collision and blank out the sole holder's attach.</summary>
    [Fact]
    public void IdOnlyLookup_IsNotConfusedByOneKeyBeingBothBoundAndPending()
    {
        using var mgr = new TerminalSessionManager();
        var bound = new BoundTerminalSession(SharedId, new StubSession());
        mgr.Bind(A, bound);
        mgr.MarkBindPending(A); // re-spawn in flight; the old CLI is still registered

        Assert.Same(bound, mgr.TryGetBound(SharedId));
        Assert.True(mgr.IsBindPending(SharedId));
    }

    // ===================== the binder: the repo hash actually reaches the key =====================

    /// <summary>The thread-through, from the launch spec. <see cref="AgentCliBinder.TryBind"/> is what the
    /// spawn chain calls, and <see cref="AgentCliLaunchSpec.RepoHash"/> is the repo it must key on. A fix
    /// that keys the manager but keeps handing it a bare id would pass every test above and still kill one
    /// repo's CLI from the other's spawn — so this asserts through the binder, not the dictionary.</summary>
    [Fact]
    public void TryBind_KeysBySpecRepoHash_SoOneRepos_pr7_DoesNotDisplaceTheOthers()
    {
        using var rig = new BinderRig();
        var stubA = new StubSession();
        var stubB = new StubSession();

        Assert.True(rig.TryBindWith(Spec(RepoA, "ctr-a"), () => stubA));
        Assert.True(rig.TryBindWith(Spec(RepoB, "ctr-b"), () => stubB));

        Assert.False(stubA.Killed);
        Assert.NotNull(rig.Terminals.TryGetBound(A));
        Assert.NotNull(rig.Terminals.TryGetBound(B));
        Assert.NotSame(rig.Terminals.TryGetBound(A), rig.Terminals.TryGetBound(B));
    }

    /// <summary>The binder's pending-bind flags are repo-scoped end to end: repo B's spawn failing (its
    /// <c>ClearBindPending</c>) must not push repo A's in-flight attach into echo.</summary>
    [Fact]
    public void BinderBindPendingFlags_AreRepoScoped()
    {
        using var rig = new BinderRig();
        rig.Binder.MarkBindPending(A);
        rig.Binder.MarkBindPending(B);

        rig.Binder.ClearBindPending(B);

        Assert.True(rig.Terminals.IsBindPending(A));
        Assert.False(rig.Terminals.IsBindPending(B));
    }

    /// <summary>The binder's release is repo-scoped: repo A's StopAgent reaps repo A's CLI and leaves repo
    /// B's running.</summary>
    [Fact]
    public void BinderRelease_ReapsOnlyThatRepos_Cli()
    {
        using var rig = new BinderRig();
        var stubA = new StubSession();
        var stubB = new StubSession();
        rig.TryBindWith(Spec(RepoA, "ctr-a"), () => stubA);
        rig.TryBindWith(Spec(RepoB, "ctr-b"), () => stubB);

        rig.Binder.Release(A);

        Assert.True(stubA.Killed);
        Assert.Null(rig.Terminals.TryGetBound(A));
        Assert.False(stubB.Killed);
        Assert.NotNull(rig.Terminals.TryGetBound(B));
    }

    /// <summary>
    /// The silent half of the defect. When a bound CLI exits, the binder reflects it as a <c>Dead</c> state
    /// on the session — and it used to do that through the id-only <c>MarkState</c>, which is a documented
    /// no-op when two repos hold the id. So with a <c>pr-7</c> in each of two repos, EITHER one's CLI could
    /// die and neither card ever changed: the operator saw a healthy "Working" agent that had no CLI left.
    /// With the key in hand the right session is marked, and only that one.
    /// </summary>
    [Fact]
    public async Task WhenOneRepos_pr7_CliExits_OnlyThatRepos_SessionGoesDead()
    {
        using var rig = new BinderRig();
        rig.Store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoA);
        rig.Store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoB);
        rig.Store.AttachSandbox(A, "ctr-a");
        rig.Store.AttachSandbox(B, "ctr-b");
        Assert.Equal("Working", rig.Store.Find(A)?.State);

        var stubA = new StubSession();
        rig.TryBindWith(Spec(RepoA, "ctr-a"), () => stubA);
        rig.TryBindWith(Spec(RepoB, "ctr-b"), () => new StubSession());

        stubA.Exit(3); // repo A's in-jail CLI dies

        Assert.True(
            await WaitForAsync(() => rig.Store.Find(A)?.State == "Dead"),
            $"repo A's session never went Dead (state was '{rig.Store.Find(A)?.State}').");
        Assert.Equal("Working", rig.Store.Find(B)?.State); // repo B's worker is untouched
    }

    // ===================== the stop path: the call site that decides the scope =====================

    /// <summary>
    /// <see cref="AgentSpawnService.StopAsync(AgentSessionKey,CancellationToken)"/> — the site the keying
    /// has to reach to matter. It used to release the terminal only when NO session anywhere on the daemon
    /// still answered to the id, which was the only way an id-keyed manager could be made non-destructive.
    /// The price was the opposite failure: stopping repo A's <c>pr-7</c> while repo B ran one released
    /// nothing at all, leaking repo A's PTY, its replay ring and its pending-bind flag until repo B
    /// stopped too. Repo-keyed, the release is unconditional and exact.
    /// </summary>
    [Fact]
    public async Task StopAsync_InOneRepo_ReleasesThatReposTerminal_AndOnlyThatOne()
    {
        using var daemon = new DaemonFixture();
        var store = daemon.Services.GetRequiredService<AgentSessionStore>();
        var terminals = daemon.Services.GetRequiredService<TerminalSessionManager>();
        var spawns = daemon.Services.GetRequiredService<AgentSpawnService>();

        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoA);
        store.Spawn("external-pr", AgentRoles.Managed, agentId: SharedId, repoHash: RepoB);

        var stubA = new StubSession();
        var stubB = new StubSession();
        terminals.Bind(A, new BoundTerminalSession(SharedId, stubA));
        terminals.Bind(B, new BoundTerminalSession(SharedId, stubB));

        await spawns.StopAsync(A, CancellationToken.None);

        Assert.True(stubA.Killed);              // repo A's PTY is reaped …
        Assert.Null(terminals.TryGetBound(A));
        Assert.False(stubB.Killed);             // … and repo B's still-open pull request keeps its terminal
        Assert.NotNull(terminals.TryGetBound(B));
    }

    private static AgentCliLaunchSpec Spec(string repoHash, string containerId) =>
        new(SharedId, repoHash, containerId, new[] { "claude" });

    private static async Task<bool> WaitForAsync(Func<bool> predicate)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (predicate()) return true;
            await Task.Delay(20);
        }

        return predicate();
    }

    /// <summary>A real <see cref="AgentCliBinder"/> over fake PTY sessions — no Docker, no daemon host.
    /// The seam is the binder's <c>sessionFactory</c>, the same one the wiring tests inject.</summary>
    private sealed class BinderRig : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "mg-term-scope-" + Guid.NewGuid().ToString("N")[..8]);
        private readonly Dictionary<string, Func<ITerminalSession>> _factories = new(StringComparer.Ordinal);

        public BinderRig()
        {
            Directory.CreateDirectory(_root);
            Terminals = new TerminalSessionManager();
            var audit = new InMemoryAuditLog();
            Store = new AgentSessionStore(audit);
            Binder = new AgentCliBinder(
                Terminals,
                new SessionLeader(new LeaderRegistry(Path.Combine(_root, "leader.json"))),
                Store,
                audit,
                spec => _factories[spec.RepoHash]());
        }

        public TerminalSessionManager Terminals { get; }

        public AgentSessionStore Store { get; }

        public AgentCliBinder Binder { get; }

        /// <summary>Binds through the real binder, with <paramref name="session"/> as the PTY the factory
        /// hands back for this spec's repo.</summary>
        public bool TryBindWith(AgentCliLaunchSpec spec, Func<ITerminalSession> session)
        {
            _factories[spec.RepoHash] = session;
            return Binder.TryBind(spec);
        }

        public void Dispose()
        {
            Terminals.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>A PTY stand-in that records its own reaping and can be made to exit on demand.</summary>
    private sealed class StubSession : ITerminalSession
    {
        private readonly TaskCompletionSource<int> _exit = new();
        private int _killed;

        public Stream IO { get; } = new MemoryStream();

        public Task<int> ExitCode => _exit.Task;

        /// <summary>True once something killed this PTY — the assertion that a cross-repo bind/release
        /// did NOT reach in and reap another repository's live CLI.</summary>
        public bool Killed => Volatile.Read(ref _killed) != 0;

        public void Exit(int code) => _exit.TrySetResult(code);

        public void Resize(int cols, int rows) { }

        public void Kill()
        {
            Interlocked.Exchange(ref _killed, 1);
            _exit.TrySetResult(-1);
        }

        public void Dispose() { }
    }
}

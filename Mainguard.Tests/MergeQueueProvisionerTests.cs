using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-10 / MG-11 (2) — the daemon-side queue factory, over a REAL bare mirror and a REAL agent branch.
///
/// <para><see cref="VerificationCommandResolver"/> shipped complete and had <b>no production caller at
/// all</b>: the review cockpit built its context with the 4-argument constructor, leaving
/// <c>ChangedTestCommand = false</c> forever, so in the running app a branch that rewrote its own test
/// command to something that always passes produced no flag and merged unremarked. RT-D2 exists precisely
/// to stop a branch self-greening. Resolving the command here — at verification time, from the two committed
/// trees, daemon-side — is what makes the flag fire, and what pins the provenance into the immutable
/// verification record.</para>
/// </summary>
public sealed class MergeQueueProvisionerTests : IDisposable
{
    private const string AgentId = "loom-rtd2";
    private const string ContainerId = "container-rtd2";

    private readonly string _vmRoot = NewDir("mainguard-mqprov-vm-");
    private readonly string _source = NewDir("mainguard-mqprov-src-");

    [Fact]
    public async Task BranchThatRewritesItsOwnTestCommand_IsFlagged_AndCannotMerge()
    {
        // main's baseline runs the real suite; the branch rewrites it to something that always passes.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "true");

        var provisioner = NewProvisioner(exitCode: 0, out _);
        var ctx = provisioner.EnsureQueue(repoHash);
        Assert.NotNull(ctx);

        var record = await ctx!.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        // The provenance is resolved from the BRANCH tree and recorded immutably (RT-D2 ResolvedCommand +
        // ConfigHash) — that is the evidence a reviewer reads to see what actually ran.
        Assert.Equal("true", record.ResolvedCommand);
        Assert.Equal(VerificationCommandResolver.Sha256("true\n"), record.ConfigHash);
        Assert.True(record.Passed); // it "passes" — which is exactly why the drift has to be flagged.

        // ...and the drift blocks the merge until a human acknowledges it.
        Assert.True(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Contains("test command changed", reason);

        ctx.ChangedTestCommand.Acknowledge(AgentId);
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));
    }

    [Fact]
    public async Task BranchThatLeavesTheTestCommandAlone_IsNotFlagged()
    {
        // The control: identical config on both sides must NOT flag, or the gate is just noise that
        // trains people to acknowledge without reading.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var provisioner = NewProvisioner(exitCode: 0, out var engine);
        var ctx = provisioner.EnsureQueue(repoHash)!;

        var record = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.Equal("npm test", record.ResolvedCommand);
        Assert.False(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));

        // The command ran in the AGENT'S OWN JAIL, argv-style — host execution is a rejection trigger.
        Assert.Equal(ContainerId, engine.LastContainerId);
        Assert.Equal(new[] { "npm", "test" }, engine.LastCommand);
    }

    [Fact]
    public async Task AFailingRealExit_LeavesTheBranchUnmergeable()
    {
        // OPS SA-1: pass/fail is the container-runtime exit the engine reports, and nothing else.
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        CommitOnAgentBranch(repoHash, branchVerifyCommand: "npm test");

        var ctx = NewProvisioner(exitCode: 1, out _).EnsureQueue(repoHash)!;
        var record = await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(record.Passed);
        Assert.Equal(WorkerMergeState.Working, ctx.Queue.GetState(AgentId));
        Assert.False(ctx.Queue.CanMerge(AgentId, out _));
    }

    [Fact]
    public void EnsureQueue_RegistersTheRepo_AndIsIdempotent()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        var registry = new MergeQueueRegistry();
        var provisioner = NewProvisioner(exitCode: 0, out _, registry);

        Assert.Null(registry.Resolve(repoHash)); // empty until something makes the repo active

        var first = provisioner.EnsureQueue(repoHash);
        var second = provisioner.EnsureQueue(repoHash);

        Assert.NotNull(first);
        // Idempotent by identity, not just by outcome: a second queue over the same repo would be a second
        // state machine racing the first over one main branch.
        Assert.Same(first, second);
        Assert.Same(first, registry.Resolve(repoHash));
        Assert.Equal(MainSha(repoHash), first!.Queue.CurrentMainSha);
    }

    [Fact]
    public void EnsureQueue_ForAnUnprovisionedRepo_StaysAbsent()
    {
        // Honest absence: a handle with no mirror keeps resolving to NOT_FOUND rather than getting a queue
        // pinned to an unknown main, against which every record would look fresh.
        Assert.Null(NewProvisioner(exitCode: 0, out _).EnsureQueue("never-provisioned"));
    }

    [Fact]
    public void EnsureEntry_MakesTheAgentVisibleToTheQueue()
    {
        var repoHash = SeedAndProvision(mainVerifyCommand: "npm test");
        var provisioner = NewProvisioner(exitCode: 0, out _, out var registry);

        provisioner.EnsureEntry(repoHash, AgentId, MergeEntryOrigin.Local);

        var ctx = registry.Resolve(repoHash);
        Assert.NotNull(ctx);
        Assert.Contains(AgentId, ctx!.Queue.Agents);
        Assert.Equal(WorkerMergeState.Working, ctx.Queue.GetState(AgentId));
    }

    // ---- harness ---------------------------------------------------------

    private MergeQueueProvisioner NewProvisioner(int exitCode, out FakeSandboxEngine engine)
        => NewProvisioner(exitCode, out engine, new MergeQueueRegistry());

    private MergeQueueProvisioner NewProvisioner(int exitCode, out FakeSandboxEngine engine, out MergeQueueRegistry registry)
    {
        registry = new MergeQueueRegistry();
        return NewProvisioner(exitCode, out engine, registry);
    }

    private MergeQueueProvisioner NewProvisioner(int exitCode, out FakeSandboxEngine engine, MergeQueueRegistry registry)
    {
        engine = new FakeSandboxEngine(exitCode);
        return new MergeQueueProvisioner(
            registry: registry,
            repos: new RepoProvisioner(_vmRoot),
            leases: new InMemoryMergeLeaseStore(),
            resolveContainerId: (_, _) => ContainerId,
            queueStore: _ => new InMemoryMergeQueueStore(),
            verificationStore: _ => new InMemoryVerificationStore(),
            sandboxes: engine,
            artifactDirectory: NewDir("mainguard-mqprov-artifacts-"));
    }

    /// <summary>Seeds a source repo carrying a main-side verification config, then provisions its mirror.</summary>
    private string SeedAndProvision(string mainVerifyCommand)
    {
        Repository.Init(_source);
        using (var repo = new Repository(_source))
        {
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        }

        WriteAndCommit(_source, MergeQueueProvisioner.VerificationConfigPath, mainVerifyCommand + "\n", "seed verify config");
        return new RepoProvisioner(_vmRoot).Provision(_source).RepoHash;
    }

    /// <summary>
    /// Creates the agent worktree and lands work on agent/&lt;id&gt;. The branch always carries a real commit
    /// (an agent that changed nothing is not a merge candidate); the verification config is rewritten only
    /// when the test is exercising drift, so the "left it alone" control is a genuinely untouched config
    /// rather than a rewrite that happens to be identical.
    /// </summary>
    private void CommitOnAgentBranch(string repoHash, string branchVerifyCommand)
    {
        var worktree = new WorktreeManager(_vmRoot).CreateAgentWorktree(repoHash, AgentId);
        WriteAndCommit(worktree, "feature.cs", "public class Feature { }\n", "the agent's actual work");

        var config = Path.Combine(worktree, MergeQueueProvisioner.VerificationConfigPath);
        if (!string.Equals(File.ReadAllText(config), branchVerifyCommand + "\n", StringComparison.Ordinal))
        {
            WriteAndCommit(worktree, MergeQueueProvisioner.VerificationConfigPath, branchVerifyCommand + "\n", "branch verify config");
        }
    }

    private string MainSha(string repoHash)
    {
        using var repo = new Repository(new RepoProvisioner(_vmRoot).BareRepoPathFor(repoHash));
        return repo.Head.Tip.Sha;
    }

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

    /// <summary>Reports a fixed container-runtime exit and records what it was asked to run, where.</summary>
    private sealed class FakeSandboxEngine : ISandboxEngine
    {
        private readonly int _exitCode;

        public FakeSandboxEngine(int exitCode) => _exitCode = exitCode;

        public string? LastContainerId { get; private set; }
        public IReadOnlyList<string>? LastCommand { get; private set; }

        public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
        {
            LastContainerId = containerId;
            LastCommand = command;
            return Task.FromResult(new SandboxExecResult(_exitCode, "output", ""));
        }

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnpauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

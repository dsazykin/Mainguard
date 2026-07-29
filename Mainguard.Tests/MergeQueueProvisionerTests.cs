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
using Mainguard.Git.Exceptions;
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

    // ---- per-repo toolchain declaration (MG-42) --------------------------
    //
    // Every claim below is its OWN test on purpose. xUnit stops a test at its first failing assertion,
    // so a five-assertion test measures assertion one and merely asserts the rest; when each of these
    // was re-run against a deliberately broken resolver, the split is what showed WHICH control had
    // stopped working rather than just that something had.

    /// <summary>
    /// <b>The security property.</b> A branch that edits <c>.mainguard/toolchain</c> does not get its
    /// toolchain installed — main's is. The observable is which presence probe the daemon ran in the
    /// jail: main declares <c>dotnet-10</c>, the branch demands <c>rust-stable</c>, and the daemon must
    /// probe <c>dotnet --version</c> and never <c>cargo --version</c>.
    ///
    /// <para>This is the assertion that separates "we flagged the install" from "we never did the
    /// install". Flagging alone would be worthless here: the package would already have run its install
    /// scripts as root, inside the jail, before any human saw the diff.</para>
    /// </summary>
    [Fact]
    public async Task ABranchsToolchain_IsNeverTheOneProvisioned()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "rust-stable");

        var ctx = NewProvisioner(exitCode: 0, out var engine).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        var probed = engine.Commands.Select(c => string.Join(' ', c)).ToList();
        Assert.Contains("dotnet --version", probed);
        Assert.DoesNotContain("cargo --version", probed);
    }

    /// <summary>The branch's toolchain edit arms the RT-D2 gate.</summary>
    [Fact]
    public async Task BranchThatChangesTheToolchain_IsFlagged()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "rust-stable");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.True(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
    }

    /// <summary>...and the armed gate actually blocks the merge, naming the toolchain specifically —
    /// a reason that said "test command" would send the reviewer to the wrong file.</summary>
    [Fact]
    public async Task BranchThatChangesTheToolchain_CannotMerge_AndTheReasonNamesTheToolchain()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "rust-stable");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Contains("verification toolchain changed vs main", reason, StringComparison.Ordinal);
    }

    /// <summary>The gate is a gate, not a wall: a human acknowledgment clears it.</summary>
    [Fact]
    public async Task AcknowledgingTheToolchainChange_UnblocksTheMerge()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "rust-stable");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);
        Assert.False(ctx.Queue.CanMerge(AgentId, out _));

        ctx.ChangedTestCommand!.Acknowledge(AgentId);
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));
    }

    /// <summary>
    /// Adding a declaration where main has none is drift too. Without this the cheapest attack — a repo
    /// that declares nothing, a branch that adds a line — would be the one case that sailed through.
    /// </summary>
    [Fact]
    public async Task BranchThatAddsAToolchainWhereMainHasNone_IsFlagged()
    {
        var repoHash = SeedAndProvision("npm test"); // no main-side toolchain at all
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "dotnet-10");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.True(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
    }

    /// <summary>
    /// The control. Identical declarations on both sides must NOT flag — a gate that fires on every
    /// branch is noise, and noise trains reviewers to acknowledge without reading.
    /// </summary>
    [Fact]
    public async Task BranchThatLeavesTheToolchainAlone_IsNotFlagged()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "dotnet-10");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
        Assert.True(ctx.Queue.CanMerge(AgentId, out _));
    }

    /// <summary>A comment or a blank line is not toolchain drift; normalisation is real, not decorative.</summary>
    [Fact]
    public async Task AComment_IsNotToolchainDrift()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "# why we need this\n\ndotnet-10   # the SDK");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(ctx.ChangedTestCommand!.IsUnacknowledged(AgentId));
    }

    /// <summary>
    /// Both files drifting must produce BOTH items in the reason. A reason that mentioned only one
    /// would let a reviewer acknowledge what they read and merge what they did not.
    /// </summary>
    [Fact]
    public async Task CommandDriftAndToolchainDrift_AreBothNamed()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "true", branchToolchain: "rust-stable");

        var ctx = NewProvisioner(exitCode: 0, out _).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.False(ctx.Queue.CanMerge(AgentId, out var reason));
        Assert.Contains("test command", reason, StringComparison.Ordinal);
        Assert.Contains("verification toolchain", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure mode that matters. A jail that does not actually carry the declared toolchain must
    /// raise a typed <see cref="ToolchainProvisioningException"/> and run NOTHING — never a
    /// <c>NoVerificationCommandException</c> ("nothing to check") and never an ordinary failed record
    /// ("the agent's code is broken"). Both of those read like verdicts.
    /// </summary>
    [Fact]
    public async Task AJailMissingItsDeclaredToolchain_IsATypedProvisioningFailure()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "dotnet-10");

        // The probe fails; every other exec would succeed.
        var provisioner = NewProvisioner(
            exitCode: 0, out var engine, new MergeQueueRegistry(),
            exitFor: cmd => cmd.Count > 0 && cmd[0] == "dotnet" ? 127 : 0);
        var ctx = provisioner.EnsureQueue(repoHash)!;

        var ex = await Assert.ThrowsAsync<ToolchainProvisioningException>(
            () => ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None));

        Assert.Contains("dotnet-10", ex.Message, StringComparison.Ordinal);
        // The verify command was never launched — a provisioning failure is not a test result.
        Assert.DoesNotContain("npm test", engine.Commands.Select(c => string.Join(' ', c)));
    }

    /// <summary>...and that failure leaves the branch unmergeable rather than quietly Verified.</summary>
    [Fact]
    public async Task AJailMissingItsDeclaredToolchain_LeavesTheBranchUnmergeable()
    {
        var repoHash = SeedAndProvision("npm test", mainToolchain: "dotnet-10");
        CommitOnAgentBranch(repoHash, "npm test", branchToolchain: "dotnet-10");

        var ctx = NewProvisioner(
            exitCode: 0, out _, new MergeQueueRegistry(),
            exitFor: cmd => cmd.Count > 0 && cmd[0] == "dotnet" ? 127 : 0).EnsureQueue(repoHash)!;

        await Assert.ThrowsAsync<ToolchainProvisioningException>(
            () => ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None));

        Assert.NotEqual(WorkerMergeState.Verified, ctx.Queue.GetState(AgentId));
        Assert.False(ctx.Queue.CanMerge(AgentId, out _));
    }

    /// <summary>
    /// A repo declaring nothing keeps behaving exactly as it did before this feature existed: no
    /// probes, no extra execs, straight to the verification command.
    /// </summary>
    [Fact]
    public async Task ARepoWithNoToolchainDeclaration_RunsNoProbes()
    {
        var repoHash = SeedAndProvision("npm test");
        CommitOnAgentBranch(repoHash, "npm test");

        var ctx = NewProvisioner(exitCode: 0, out var engine).EnsureQueue(repoHash)!;
        await ctx.Queue.RunVerificationAsync(AgentId, CancellationToken.None);

        Assert.Equal(new[] { "npm test" }, engine.Commands.Select(c => string.Join(' ', c)));
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
        => NewProvisioner(exitCode, out engine, registry, exitFor: null);

    private MergeQueueProvisioner NewProvisioner(
        int exitCode, out FakeSandboxEngine engine, MergeQueueRegistry registry, Func<IReadOnlyList<string>, int>? exitFor)
    {
        engine = new FakeSandboxEngine(exitCode, exitFor);
        return new MergeQueueProvisioner(
            registry: registry,
            repos: new RepoProvisioner(_vmRoot),
            leases: new InMemoryMergeLeaseStore(),
            resolveContainerId: (_, _) => ContainerId,
            queueStore: _ => new InMemoryMergeQueueStore(),
            verificationStore: _ => new InMemoryVerificationStore(),
            sandboxes: engine,
            artifactDirectory: NewDir("mainguard-mqprov-artifacts-"),
            // MG-3: the production wiring. The agent commits into its OWN repository now, so without the
            // daemon-side publish the RT-D2 provenance would be read off the mirror's stale copy of
            // agent/<id> — the branch's rewritten test command would be invisible and the drift gate
            // would silently stop firing while every assertion below still looked plausible.
            publishAgentRef: (repoHash, agentId) => new WorktreeManager(_vmRoot).PublishAgentBranch(repoHash, agentId));
    }

    /// <summary>Seeds a source repo carrying a main-side verification config, then provisions its mirror.</summary>
    private string SeedAndProvision(string mainVerifyCommand, string? mainToolchain = null)
    {
        Repository.Init(_source);
        using (var repo = new Repository(_source))
        {
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        }

        WriteAndCommit(_source, MergeQueueProvisioner.VerificationConfigPath, mainVerifyCommand + "\n", "seed verify config");
        if (mainToolchain is not null)
        {
            WriteAndCommit(_source, MergeQueueProvisioner.ToolchainConfigPath, mainToolchain + "\n", "seed toolchain config");
        }

        return new RepoProvisioner(_vmRoot).Provision(_source).RepoHash;
    }

    /// <summary>
    /// Creates the agent worktree and lands work on agent/&lt;id&gt;. The branch always carries a real commit
    /// (an agent that changed nothing is not a merge candidate); the verification config is rewritten only
    /// when the test is exercising drift, so the "left it alone" control is a genuinely untouched config
    /// rather than a rewrite that happens to be identical.
    /// </summary>
    private void CommitOnAgentBranch(string repoHash, string branchVerifyCommand, string? branchToolchain = null)
    {
        var worktree = new WorktreeManager(_vmRoot).CreateAgentWorktree(repoHash, AgentId);
        WriteAndCommit(worktree, "feature.cs", "public class Feature { }\n", "the agent's actual work");

        RewriteIfDifferent(worktree, MergeQueueProvisioner.VerificationConfigPath, branchVerifyCommand, "branch verify config");
        if (branchToolchain is not null)
        {
            RewriteIfDifferent(worktree, MergeQueueProvisioner.ToolchainConfigPath, branchToolchain, "branch toolchain config");
        }
    }

    /// <summary>Writes only when the content really differs, so the "left it alone" controls are a
    /// genuinely untouched file rather than a rewrite that happens to be identical.</summary>
    private static void RewriteIfDifferent(string worktree, string relPath, string content, string message)
    {
        var full = Path.Combine(worktree, relPath);
        var wanted = content + "\n";
        if (File.Exists(full) && string.Equals(File.ReadAllText(full), wanted, StringComparison.Ordinal))
        {
            return;
        }

        WriteAndCommit(worktree, relPath, wanted, message);
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
        private readonly Func<IReadOnlyList<string>, int>? _exitFor;

        public FakeSandboxEngine(int exitCode, Func<IReadOnlyList<string>, int>? exitFor = null)
        {
            _exitCode = exitCode;
            _exitFor = exitFor;
        }

        public string? LastContainerId { get; private set; }
        public IReadOnlyList<string>? LastCommand { get; private set; }

        /// <summary>EVERY exec, in order — the toolchain presence probes run before the verify command,
        /// so a test that only ever saw the LAST one could not tell which toolchain was probed.</summary>
        public List<IReadOnlyList<string>> Commands { get; } = new();

        public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
        {
            LastContainerId = containerId;
            LastCommand = command;
            Commands.Add(command);
            return Task.FromResult(new SandboxExecResult(_exitFor?.Invoke(command) ?? _exitCode, "output", ""));
        }

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnpauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

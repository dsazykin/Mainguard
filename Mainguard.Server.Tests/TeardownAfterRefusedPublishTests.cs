using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The launcher's half of "a refused publish must not delete the work": <c>TeardownAsync</c> reads the
/// last publish's OUTCOME and takes the repository-keeping removal on a refusal, the deleting one
/// otherwise. What the two removals do to real git is pinned in <c>AgentWorktreeManagerTests</c>; this
/// pins the routing, which is the line a correct manager nobody calls correctly would not have (the M7
/// shape the phase-3 decisions doc keeps re-finding).
/// </summary>
public sealed class TeardownAfterRefusedPublishTests
{
    [Theory]
    [InlineData(AgentRefPublishOutcome.RefusedNonFastForward, true)]
    [InlineData(AgentRefPublishOutcome.RefusedTarget, true)]
    [InlineData(AgentRefPublishOutcome.Published, false)]
    [InlineData(AgentRefPublishOutcome.Unchanged, false)]
    [InlineData(AgentRefPublishOutcome.NothingToPublish, false)]
    public async Task Teardown_KeepsTheRepository_ExactlyWhenTheLastPublishWasRefused(
        AgentRefPublishOutcome outcome, bool expectKept)
    {
        var worktrees = new RecordingWorktrees { PublishOutcome = outcome };
        var registry = Path.Combine(Path.GetTempPath(), "mg-teardown-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(registry);
        try
        {
            var launcher = new SandboxAgentLauncher(
                new WorktreesOnlyEnvironment(worktrees), new InstalledAdapterCatalog(registry), NullLoggerFactory.Instance);

            await launcher.TeardownAsync("repo-hash", "agent-1", "container-1", CancellationToken.None);

            Assert.Equal(new[] { ("repo-hash", "agent-1") }, worktrees.Published);
            if (expectKept)
            {
                Assert.Equal(new[] { ("repo-hash", "agent-1") }, worktrees.KeptRepository);
                Assert.Empty(worktrees.Removed);
                Assert.Contains(outcome.ToString(), Assert.Single(worktrees.KeepReasons), StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(new[] { ("repo-hash", "agent-1") }, worktrees.Removed);
                Assert.Empty(worktrees.KeptRepository);
            }
        }
        finally
        {
            try { Directory.Delete(registry, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Records which removal the launcher chose; performs neither.</summary>
    private sealed class RecordingWorktrees : IAgentWorktreeManager
    {
        public AgentRefPublishOutcome PublishOutcome { get; set; } = AgentRefPublishOutcome.Published;
        public List<(string Repo, string Agent)> Published { get; } = new();
        public List<(string Repo, string Agent)> Removed { get; } = new();
        public List<(string Repo, string Agent)> KeptRepository { get; } = new();
        public List<string> KeepReasons { get; } = new();

        public string CreateAgentWorktree(string repoHash, string agentId) => throw new NotSupportedException();

        public void RemoveAgentWorktree(string repoHash, string agentId, bool force) => Removed.Add((repoHash, agentId));

        public void RemoveAgentWorktreeKeepingRepository(string repoHash, string agentId, string reason)
        {
            KeptRepository.Add((repoHash, agentId));
            KeepReasons.Add(reason);
        }

        public AgentRefPublishOutcome PublishAgentBranchOutcome(string repoHash, string agentId)
        {
            Published.Add((repoHash, agentId));
            return PublishOutcome;
        }

        public void Prune(string repoHash)
        {
        }

        public IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
            Array.Empty<Mainguard.Git.Models.WorktreeItem>();
    }

    /// <summary>Only the worktree manager is real; the teardown's other legs are best-effort and swallow
    /// the throws these getters produce, which is exactly the posture a Stop has toward housekeeping.</summary>
    private sealed class WorktreesOnlyEnvironment : IAgentEnvironment
    {
        public WorktreesOnlyEnvironment(IAgentWorktreeManager worktrees) => Worktrees = worktrees;

        public string SubstrateId => "teardown-probe";

        public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

        public IAgentWorktreeManager Worktrees { get; }

        public ISandboxEngine Sandboxes => throw new NotSupportedException("no engine in this leg");

        public IRepoProvisioner Repos => throw new NotSupportedException("no provisioner in this leg");

        public IEgressPolicy Egress => throw new NotSupportedException("no egress in this leg");

        public SyncRemote ResolveSyncRemote(string repoHash) => throw new NotSupportedException();
    }
}

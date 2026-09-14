using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Server.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// A spawn that fails AFTER its jail exists must remove that jail (2026-09-04) <b>and release its network
/// segment</b> (audit F27). The launcher's rollback used to clean up the worktree and leave the container
/// running, unowned, for good — nothing that could stop it later knew it existed; and even once the
/// container was removed the MG-36 per-agent segment, created BEFORE the container, was still left behind.
/// Docker's default local address pool is about 32 networks, so a few dozen failed spawns exhaust it and
/// every subsequent spawn fails at network creation on a machine with no running agents. Driven through the
/// real launcher with an engine that records removals and a worktree manager whose ref-watch (the last step
/// after the container starts) throws.
/// </summary>
public sealed class SpawnRollbackTests
{
    [Fact]
    public async Task AJailWhoseSpawnFailsAfterItStarted_IsRemoved_NotLeftRunning()
    {
        var root = Path.Combine(Path.GetTempPath(), "mg-rollback-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "repos", "repo-a"));
        Directory.CreateDirectory(Path.Combine(root, "registry"));
        try
        {
            var engine = new AgentSessionRepoScopingTests.RecordingEngine();
            var environment = new ThrowingWatchEnvironment(root, engine);
            var launcher = new SandboxAgentLauncher(
                environment,
                new InstalledAdapterCatalog(Path.Combine(root, "registry")),
                NullLoggerFactory.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                launcher.TryLaunchAsync("repo-a", "agent-1", "claude-code", null, ct: CancellationToken.None));

            // The jail was spawned — and then removed, because nothing else ever will.
            var spawned = Assert.Single(engine.Spawns);
            Assert.Equal(("repo-a", "agent-1"), spawned);
            Assert.NotEmpty(engine.Removed);

            // ...and so was its segment. A grant made before the container must have a release on the
            // path that gives up before the container exists, which is most of the ways a spawn fails.
            var released = Assert.Single(((ThrowingWatchEnvironment.NoEgress)environment.Egress).RemovedSegments);
            Assert.Equal(("repo-a", "agent-1"), released);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class ThrowingWatchEnvironment : IAgentEnvironment
    {
        public ThrowingWatchEnvironment(string root, ISandboxEngine engine)
        {
            Sandboxes = engine;
            Worktrees = new ThrowingWatchWorktrees(root);
            Repos = new Provisioner(root);
        }

        public string SubstrateId => "rollback-probe";

        public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

        public IRepoProvisioner Repos { get; }

        public IAgentWorktreeManager Worktrees { get; }

        public ISandboxEngine Sandboxes { get; }

        public IEgressPolicy Egress { get; } = new NoEgress();

        public SyncRemote ResolveSyncRemote(string repoHash) => new("fake-remote", $"fake://{repoHash}");

        private sealed class Provisioner : IRepoProvisioner
        {
            private readonly string _root;

            public Provisioner(string root) => _root = root;

            public ProvisionResult Provision(string windowsRepoPathNormalized) => throw new NotSupportedException();

            public string BareRepoPathFor(string repoHash) => Path.Combine(_root, "repos", repoHash);
        }

        private sealed class ThrowingWatchWorktrees : IAgentWorktreeManager
        {
            private readonly string _root;

            public ThrowingWatchWorktrees(string root) => _root = root;

            public string CreateAgentWorktree(string repoHash, string agentId)
            {
                var path = Path.Combine(_root, "wt", repoHash, agentId);
                Directory.CreateDirectory(path);
                return path;
            }

            public void RemoveAgentWorktree(string repoHash, string agentId, bool force)
            {
                try { Directory.Delete(Path.Combine(_root, "wt", repoHash, agentId), recursive: true); }
                catch (DirectoryNotFoundException) { }
            }

            public void Prune(string repoHash)
            {
            }

            public IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
                Array.Empty<Mainguard.Git.Models.WorktreeItem>();

            // The last thing the launcher does after the container is running.
            public void WatchAgentRef(string repoHash, string agentId) =>
                throw new InvalidOperationException("the ref watcher refused this agent");
        }

        internal sealed class NoEgress : IEgressPolicy
        {
            /// <summary>Every (repo, agent) whose per-agent segment was released.</summary>
            public List<(string Repo, string Agent)> RemovedSegments { get; } = new();

            public Task RemoveAgentSegmentAsync(
                string repoHash, string agentId, CancellationToken ct = default)
            {
                RemovedSegments.Add((repoHash, agentId));
                return Task.CompletedTask;
            }

            public EgressAllowlist Allowlist { get; } = EgressAllowlist.WithDefaults(new InMemoryAuditLog());

            public string NetworkName => "fake-net";

            public string ProxyUrl => "http://fake-proxy:3128";

            public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

            public EgressVerdict Evaluate(string host) => EgressVerdict.Denied;
        }
    }
}

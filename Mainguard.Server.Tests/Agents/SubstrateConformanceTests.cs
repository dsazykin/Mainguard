using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

// ESC §4 — the portability conformance suite, run against THIS host's substrate composition
// (the same classes AgentEnvironmentFactory resolves, with the root redirected to a temp dir so
// the suite never touches the user's real ~/mainguard). The §4 rows this file does not encode
// are already load-bearing elsewhere in this tier and are cited from the B-doc: #3 quarantine
// remotes + #1's in-jail-commit leg (MirrorReadOnlyDockerTests), #8 hardened spec
// (JailRuntimePostureDockerTests + the ContainerSpec* unit suites), #9 secret channels
// (SecretDeliveryDockerTests), #6/#7 (EditionReferenceGraphTests, the G-16 no-build design).
// Rows #4/#5/#10 depend on facade members (TeardownAsync/HealthCheckAsync/UpgradeAsync) the
// interface deliberately does not declare yet — deferred with the interface's own additive-
// growth rationale.
public class SubstrateConformanceTests
{
    private static IAgentEnvironment CreateSubstrate(string vmRoot) =>
        OperatingSystem.IsMacOS()
            ? new MacHostAgentEnvironment(vmRoot: vmRoot)
            : new Wsl2AgentEnvironment(vmRoot: vmRoot);

    // ---- §4 #1: GitObjectsRoundTrip_ShouldBeByteIdentical --------------------------------------
    // A commit made in a substrate worktree, published through the mediated publish, reaches the
    // HOST repo byte-identically through the OPAQUE SyncRemote handle (ESC-I1/ESC-I8): the host
    // registers exactly ResolveSyncRemote(hash) and fetches — nothing else crosses the boundary.
    //
    // macOS-only for now: the WSL2 substrate's handle is a \\wsl.localhost UNC path only the
    // WINDOWS host can fetch, so its leg belongs to the B2 doc's own run, not the linux CI leg.
    [MacOnlyFact("the WSL2 handle is a UNC path only a Windows host can fetch — B2 owns that leg")]
    public void GitObjectsRoundTrip_ShouldBeByteIdentical()
    {
        var vmRoot = Path.Combine(CanonicalTemp.Root, "mainguard-conf-vm-" + Guid.NewGuid().ToString("N"));
        var hostRepo = Path.Combine(CanonicalTemp.Root, "mainguard-conf-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(hostRepo);
            AgentTestGit.RunChecked(hostRepo, "init", "-q", ".");
            AgentTestGit.RunChecked(hostRepo, "config", "user.email", "conf@test");
            AgentTestGit.RunChecked(hostRepo, "config", "user.name", "conf");
            File.WriteAllText(Path.Combine(hostRepo, "seed.txt"), "seed\n");
            AgentTestGit.RunChecked(hostRepo, "add", "-A");
            AgentTestGit.RunChecked(hostRepo, "commit", "-qm", "seed");

            var environment = CreateSubstrate(vmRoot);
            var provision = environment.Repos.Provision(hostRepo);
            const string agentId = "conformance-1";
            var worktree = environment.Worktrees.CreateAgentWorktree(provision.RepoHash, agentId);

            File.WriteAllText(Path.Combine(worktree, "agent.txt"), "made in the substrate\n");
            AgentTestGit.RunChecked(worktree, "add", "-A");
            AgentTestGit.RunChecked(worktree, "-c", "user.email=agent@test", "-c", "user.name=agent",
                "commit", "-qm", "agent work");
            var substrateSha = AgentTestGit.RunChecked(worktree, "rev-parse", "HEAD").Trim();
            var substrateTree = AgentTestGit.RunChecked(worktree, "rev-parse", "HEAD^{tree}").Trim();

            Assert.True(environment.Worktrees.PublishAgentBranch(provision.RepoHash, agentId));

            // The host's ONE remote is the resolved handle — name and URL exactly as the
            // substrate answered them (SC-2: the name literal lives in the substrate alone).
            var remote = environment.ResolveSyncRemote(provision.RepoHash);
            AgentTestGit.RunChecked(hostRepo, "remote", "add", remote.Name, remote.Url);
            AgentTestGit.RunChecked(hostRepo, "fetch", "-q", remote.Name, "refs/heads/agent/" + agentId);

            var fetchedSha = AgentTestGit.RunChecked(hostRepo, "rev-parse", "FETCH_HEAD").Trim();
            var fetchedTree = AgentTestGit.RunChecked(hostRepo, "rev-parse", "FETCH_HEAD^{tree}").Trim();
            Assert.Equal(substrateSha, fetchedSha);
            Assert.Equal(substrateTree, fetchedTree);
        }
        finally
        {
            foreach (var dir in new[] { vmRoot, hostRepo })
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }
    }

    // ---- §4 #2: NoHostPathMount_ShouldHoldForEveryContainer ------------------------------------
    // The LIVE container's bind sources: every one sits under the suite's substrate temp roots,
    // and none is a host-path shape (drvfs, UNC, drive-letter) or anything under the user's home
    // outside the substrate root. The pure-spec half of this row is ContainerSpecMountRootsTests.
    [RequiresDockerFact]
    [Trait("Category", "RequiresDocker")]
    public async Task NoHostPathMount_ShouldHoldForEveryContainer()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "conf-mounts");

        var inspect = await fx.Docker.Containers.InspectContainerAsync(handle.ContainerId, CancellationToken.None);
        var binds = (inspect.Mounts ?? new System.Collections.Generic.List<MountPoint>())
            .Where(m => string.Equals(m.Type, "bind", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(binds); // the worktree at least — an empty list would prove nothing

        var home = Mainguard.Git.MainguardPaths.HomeDirectory();
        foreach (var mount in binds)
        {
            Assert.False(mount.Source.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase),
                $"drvfs-shaped bind source: {mount.Source}");
            Assert.DoesNotContain("wsl.localhost", mount.Source, StringComparison.OrdinalIgnoreCase);
            Assert.False(mount.Source.Length > 1 && mount.Source[1] == ':',
                $"drive-letter bind source: {mount.Source}");
            // The suite's fixture roots live under the temp dir — never under the user's home,
            // which is where "host-home virtiofs" leaks would surface (ESC §4 test 2).
            Assert.False(
                mount.Source.StartsWith(home + "/", StringComparison.Ordinal)
                    && !mount.Source.Contains("/mainguard", StringComparison.Ordinal),
                $"bind source under the user's home outside a mainguard tree: {mount.Source}");
            Assert.StartsWith(CanonicalTemp.Root.TrimEnd('/'), mount.Source, StringComparison.Ordinal);
        }
    }
}

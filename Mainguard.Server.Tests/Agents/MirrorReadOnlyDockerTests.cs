using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// MG-3 stage 3, RequiresDocker — <b>the finding, performed</b>.
///
/// <para>The quarantine that was supposed to stop an agent rewriting <c>main</c> was
/// <c>receive.denyNonFastForwards</c> + <c>receive.denyDeletes</c>, and both are enforced inside
/// <c>git receive-pack</c> — the code path a <i>push</i> takes. They are not filesystem permissions.
/// The mirror was bind-mounted into the jail read-write at its real VM path, so an agent could simply
/// overwrite <c>&lt;bare&gt;/refs/heads/main</c> (a 41-byte file), rewrite <c>packed-refs</c>, or drop
/// loose objects — none of which invokes receive-pack, so the deny rules never executed. The lock was
/// on the front door and the agent walked through the wall.</para>
///
/// <para><b>This test walks through the wall.</b> It launches the real production jail through the real
/// <see cref="SandboxAgentLauncher"/> chain and, from inside it, attempts every one of those writes.
/// Each must be refused. Asserting <c>ReadOnly == true</c> on a spec object would not be this test: a
/// mount option that never reached a container proves nothing, and the whole finding is that a control
/// which looked right was not in the path.</para>
///
/// <para><b>Why the refusal is real and not an accident of ownership.</b> On a box with no userns remap
/// — the developer machine this runs on — container uid 1000 IS the daemon's uid 1000, which OWNS the
/// mirror, and <c>core.sharedRepository=group</c> leaves it group-writable besides. With the mount
/// read-write every probe below SUCCEEDS; that is the non-vacuity proof, taken by flipping
/// <see cref="ContainerSpecBuilder.MirrorMountReadOnly"/> back to false and watching this test fail.
/// The read-only bind is what refuses the write, and it refuses it regardless of who the writer is.</para>
///
/// <para>Positive controls run in the same exec, because "everything failed" is exactly what a broken
/// probe also reports: the agent must still be able to write its OWN repository and still be able to
/// <c>git commit</c> in its worktree, which is the whole reason a plain read-only mount was not the
/// answer on its own.</para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public class MirrorReadOnlyDockerTests
{
    // Each fact is wrapped in its own sentinel frame, printed by the SHELL and not by the command being
    // probed, so a probe that never ran is a MISSING frame — its own distinct, reported failure — rather
    // than an empty string that reads as "refused". The openers are mutually non-overlapping: no one of
    // them appears inside another (the NOAAAA-contains-AAAA class of bug).
    private const string MainRefFrame = "MG3MAINREF[";
    private const string PackedRefsFrame = "MG3PACKED[";
    private const string LooseObjectFrame = "MG3LOOSE[";
    private const string UpdateRefFrame = "MG3UPDATEREF[";
    private const string ConfigFrame = "MG3CONFIG[";
    private const string OwnRepoFrame = "MG3OWNREPO[";
    private const string CommitFrame = "MG3COMMIT[";
    private const string ReadFrame = "MG3READ[";

    private const string Wrote = "WROTE";
    private const string Refused = "REFUSED";

    /// <summary>
    /// The probe. Every branch prints WROTE or REFUSED — never nothing — so a shell that failed to run
    /// the command at all cannot be mistaken for a refusal. Paths arrive as positional arguments and are
    /// never interpolated into script text.
    /// </summary>
    private static string ProbeScript =>
        "bare=\"$1\"; own=\"$2\"; "
        + "printf '" + MainRefFrame + "'; "
        + "if printf '%s\\n' 0000000000000000000000000000000000000000 > \"$bare/refs/heads/main\" 2>/dev/null; "
        + "  then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
        + "printf '" + PackedRefsFrame + "'; "
        + "if printf 'x\\n' > \"$bare/packed-refs\" 2>/dev/null; then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
        + "printf '" + LooseObjectFrame + "'; "
        + "if mkdir -p \"$bare/objects/ab\" 2>/dev/null && printf 'x' > \"$bare/objects/ab/planted\" 2>/dev/null; "
        + "  then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
        + "printf '" + ConfigFrame + "'; "
        + "if printf '[core]\\n' >> \"$bare/config\" 2>/dev/null; then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
        + "printf '" + UpdateRefFrame + "'; "
        + "if git --git-dir=\"$bare\" update-ref refs/heads/main "
        + "     \"$(git --git-dir=\\\"$own\\\" rev-parse HEAD)\" >/dev/null 2>&1; "
        + "  then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
        // ---- positive controls: the agent must still be able to work ----
        + "printf '" + ReadFrame + "'; "
        + "if git --git-dir=\"$bare\" rev-parse HEAD >/dev/null 2>&1; then printf 'READ'; else printf 'BLIND'; fi; printf ']'; "
        + "printf '" + OwnRepoFrame + "'; "
        + "if printf 'x' > \"$own/mainguard-write-probe\" 2>/dev/null; then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
        + "printf '" + CommitFrame + "'; "
        + "cd /workspace && printf 'agent work\\n' > mg3-agent.txt 2>/dev/null "
        + "  && git -c user.name=agent -c user.email=agent@mainguard.local add mg3-agent.txt >/dev/null 2>&1 "
        + "  && git -c user.name=agent -c user.email=agent@mainguard.local commit -m 'mg3 agent commit' >/dev/null 2>&1 "
        + "  && git push origin HEAD >/dev/null 2>&1; "
        + "if [ $? -eq 0 ]; then printf 'COMMITTED'; else printf 'BROKEN'; fi; printf ']'";

    [RequiresDockerFact]
    public async Task TheMirrorIsUnwritableFromInsideTheJail_WhileTheAgentCanStillCommitAndPublish()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = cts.Token;

        var vmRoot = NewTempDir("mainguard-mg3-vm-");
        var sourceRepo = NewTempDir("mainguard-mg3-src-");
        SeedRepo(sourceRepo);

        var environment = new Wsl2AgentEnvironment(vmRoot: vmRoot);
        var launcher = new SandboxAgentLauncher(environment);
        using var docker = new DockerClientConfiguration().CreateClient();

        var provision = environment.Repos.Provision(sourceRepo);
        const string agentId = "mg3-agent-1";
        var barePath = environment.Repos.BareRepoPathFor(provision.RepoHash);
        var mainBranch = HostGit(barePath, "symbolic-ref", "--short", "HEAD").Trim();
        var mainShaBefore = HostGit(barePath, "rev-parse", mainBranch).Trim();

        SandboxLaunchResult? launch = null;
        try
        {
            launch = await launcher.TryLaunchAsync(
                provision.RepoHash, agentId, agentKind: "worker", modelApiKey: "sk-test-not-a-real-key",
                ipcDirPath: null, ct);
            Assert.NotNull(launch);

            var agentRepoPath = environment.Worktrees.AgentRepoPathFor(provision.RepoHash, agentId);
            Assert.False(string.IsNullOrEmpty(agentRepoPath), "MG-3 requires a per-agent repository to mount");

            var probe = await environment.Sandboxes.ExecAsync(
                launch!.ContainerId,
                new[] { "sh", "-c", ProbeScript, "sh", barePath, agentRepoPath },
                ct);

            var output = probe.Stdout;

            // ---- the attack, refused on every surface the finding names ----
            Assert.Equal(Refused, ReadFrameValue(output, MainRefFrame));
            Assert.Equal(Refused, ReadFrameValue(output, PackedRefsFrame));
            Assert.Equal(Refused, ReadFrameValue(output, LooseObjectFrame));
            Assert.Equal(Refused, ReadFrameValue(output, ConfigFrame));
            Assert.Equal(Refused, ReadFrameValue(output, UpdateRefFrame));

            // ---- the controls: the probe could write, and the product still works ----
            // Without these "everything was refused" would also be what a broken shell reports.
            Assert.Equal("READ", ReadFrameValue(output, ReadFrame));
            Assert.Equal(Wrote, ReadFrameValue(output, OwnRepoFrame));
            Assert.Equal("COMMITTED", ReadFrameValue(output, CommitFrame));

            // ---- and the host-side truth: refs/heads/main never moved ----
            Assert.Equal(mainShaBefore, HostGit(barePath, "rev-parse", mainBranch).Trim());
            Assert.False(File.Exists(Path.Combine(barePath, "objects", "ab", "planted")));

            // The write the agent legitimately made is real, and reaches the merge queue's input only
            // through the daemon — refs/heads/agent/<id> in the MIRROR, exactly as before MG-3.
            var agentTip = HostGit(
                environment.Worktrees.AgentRepoPathFor(provision.RepoHash, agentId),
                "rev-parse", "refs/heads/agent/" + agentId).Trim();
            Assert.NotEqual(mainShaBefore, agentTip);
            Assert.True(environment.Worktrees.PublishAgentBranch(provision.RepoHash, agentId));
            Assert.Equal(agentTip, HostGit(barePath, "rev-parse", "refs/heads/agent/" + agentId).Trim());

            // Corroboration (not the test): docker itself reports the mount read-only.
            var inspect = await docker.Containers.InspectContainerAsync(launch.ContainerId, ct);
            var mirrorMount = Assert.Single(
                inspect.Mounts, m => string.Equals(m.Destination, barePath, StringComparison.Ordinal));
            Assert.False(mirrorMount.RW, "the shared mirror must be mounted read-only into the jail");
        }
        finally
        {
            if (launch is not null)
            {
                await launcher.TeardownAsync(provision.RepoHash, agentId, launch.ContainerId, CancellationToken.None);
            }

            await CleanupEgressAsync(docker);
            TryDelete(vmRoot);
            TryDelete(sourceRepo);
        }
    }

    /// <summary>Reads one sentinel-framed value. A MISSING frame throws with its own message: the probe
    /// did not run, which is a different fact from "the write was refused" and must never be read as
    /// one.</summary>
    private static string ReadFrameValue(string output, string opener)
    {
        var start = output.IndexOf(opener, StringComparison.Ordinal);
        Assert.True(
            start >= 0,
            $"the in-jail probe never printed the '{opener}' frame — it did not run, so nothing was proven. "
            + $"Raw output: <<{output}>>");
        start += opener.Length;
        var end = output.IndexOf(']', start);
        Assert.True(end >= 0, $"the '{opener}' frame was never closed. Raw output: <<{output}>>");
        return output[start..end];
    }

    private static string HostGit(string workDir, params string[] args)
        => AgentTestGit.RunChecked(workDir, args);

    private static void SeedRepo(string path)
    {
        Repository.Init(path);
        using var repo = new Repository(path);
        repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
        repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
        var file = Path.Combine(path, "README.md");
        File.WriteAllText(file, "seed\n");
        Commands.Stage(repo, "README.md");
        var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
        repo.Commit("seed commit", sig, sig);
    }

    private static async Task CleanupEgressAsync(IDockerClient docker)
    {
        try
        {
            await docker.Containers.RemoveContainerAsync(
                EgressProxyConfigurator.ProxyContainerName, new ContainerRemoveParameters { Force = true });
        }
        catch { /* best effort */ }

        foreach (var network in new[]
                 {
                     EgressProxyConfigurator.AgentNetworkName, EgressProxyConfigurator.EgressNetworkName,
                 })
        {
            try
            {
                var matches = await docker.Networks.ListNetworksAsync(new NetworksListParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { [network] = true },
                    },
                });
                foreach (var net in matches)
                {
                    if (net.Name == network)
                    {
                        await docker.Networks.DeleteNetworkAsync(net.ID);
                    }
                }
            }
            catch { /* best effort */ }
        }
    }

    private static string NewTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
}

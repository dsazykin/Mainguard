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
/// read-write every probe below SUCCEEDS. The read-only bind is what refuses the write, and it refuses
/// it regardless of who the writer is.</para>
///
/// <para><b>Non-vacuity was taken per PROBE, not just per test</b>, and it was worth it: three separate
/// ways for a frame to report a refusal it had never attempted were found and fixed, each by flipping
/// <see cref="ContainerSpecBuilder.MirrorMountReadOnly"/> to false, inverting every attack assertion to
/// <c>WROTE</c>, and requiring the whole set to pass. (1) The <c>update-ref</c> sha was an inlined
/// nested command substitution whose escaped quotes reached git literally, so its inner
/// <c>rev-parse</c> died on every run. (2) Corrected, it named the AGENT's HEAD — an object the mirror
/// does not have — so <c>update-ref</c> failed on the object lookup rather than on permissions.
/// (3) The reads came AFTER the destructive writes, so on a writable mirror the earlier probes
/// corrupted <c>packed-refs</c> and everything downstream then failed for that reason instead.
/// A test-level flip alone would have passed through all three: the first assertion fails either way,
/// and the frames behind it were measuring nothing. In the final form all five attack probes report
/// <c>WROTE</c> against a writable mirror and <c>REFUSED</c> against a read-only one.</para>
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
    private const string ResolvedFrame = "MG3TARGETSHA[";

    /// <summary>The branch the in-jail <c>update-ref</c> probe tries to plant in the mirror. Named so a
    /// stray one is unmistakable, and asserted absent host-side after the run.</summary>
    private const string PlantedRef = "refs/heads/mg3-attacker-planted";
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
        // Every git below runs against a repository the jail's uid does not OWN — in production because
        // of the userns remap, on a CI runner because the runner's uid is not 1000. Git refuses such a
        // repository outright with "detected dubious ownership", which would make every frame report a
        // refusal that has nothing to do with the mount. GIT_CONFIG_* is used rather than a per-command
        // `-c` so it also covers the git the CLI would run.
        + "export GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=safe.directory GIT_CONFIG_VALUE_0='*'; "
        // ---- READS FIRST, before anything below can damage what they read ----
        //
        // Ordering is load-bearing and was got wrong once. The destructive probes overwrite
        // `refs/heads/main` and `packed-refs`, so on a WRITABLE mirror they succeed and leave the
        // repository unreadable — after which `rev-parse HEAD` fails and every later frame reports a
        // refusal it never attempted. Resolving the sha and taking the read control up front makes each
        // frame mean the same thing in both configurations, which is the only way the non-vacuity run
        // (mirror writable, every attack frame required to say WROTE) can be trusted.
        + "target=$(git --git-dir=\"$bare\" rev-parse HEAD 2>/dev/null); "
        + "printf '" + ResolvedFrame + "'; "
        + "if [ -n \"$target\" ]; then printf 'RESOLVED'; else printf 'UNRESOLVED'; fi; printf ']'; "
        + "printf '" + ReadFrame + "'; "
        + "if [ -n \"$target\" ]; then printf 'READ'; else printf 'BLIND'; fi; printf ']'; "
        // ---- the attack ----
        //
        // The git-level ref write took two corrections to become honest, both caught by running the
        // fragment standalone against a WRITABLE mirror and requiring it to say WROTE: the sha was
        // first inlined as a nested command substitution whose escaped quotes reached git literally
        // (`not a git repository: \'"/…/own.git"\'` on every run), and then it named the AGENT's HEAD —
        // an object the mirror does not have, so `update-ref` failed on the lookup whether or not the
        // mount was writable. Resolved from the MIRROR and planting a NEW branch, the only thing that
        // can still make it fail is writability.
        + "printf '" + UpdateRefFrame + "'; "
        + "if [ -n \"$target\" ] && git --git-dir=\"$bare\" update-ref " + PlantedRef + " \"$target\" >/dev/null 2>&1; "
        + "  then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
        + "printf '" + MainRefFrame + "'; "
        + "if ( printf '%s\\n' 0000000000000000000000000000000000000000 > \"$bare/refs/heads/main\" ) 2>/dev/null; "
        + "  then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
        + "printf '" + PackedRefsFrame + "'; "
        + "if ( printf 'x\\n' > \"$bare/packed-refs\" ) 2>/dev/null; then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
        + "printf '" + LooseObjectFrame + "'; "
        + "if ( mkdir -p \"$bare/objects/ab\" && printf 'x' > \"$bare/objects/ab/planted\" ) 2>/dev/null; "
        + "  then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
        + "printf '" + ConfigFrame + "'; "
        + "if ( printf '[core]\\n' >> \"$bare/config\" ) 2>/dev/null; then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
        // ---- positive controls: the agent must still be able to work ----
        + "printf '" + OwnRepoFrame + "'; "
        + "if ( printf 'x' > \"$own/mainguard-write-probe\" ) 2>/dev/null; then printf '" + Wrote + "'; else printf '" + Refused + "'; fi; printf ']'; "
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

            // Open EVERY mount as wide as the filesystem allows — including the mirror — before probing.
            //
            // This is the opposite of weakening the test; it is what makes its verdict attributable.
            // The jail runs as container uid 1000. In production that is host uid 101000 (userns-remap)
            // and the shared trees carry the `mainguard-jail` group with setgid by construction (MG-17),
            // so the agent can write what it is meant to write. A CI runner has neither: its uid is not
            // 1000 and there is no such group, so without this the jail cannot write ANYTHING and all
            // five attack frames come back REFUSED because of a file mode rather than because of the
            // mount. That is exactly what happened — CI failed on the OWN-REPO control while every
            // attack assertion "passed", which is the control earning its place.
            //
            // With the mirror itself mode 0777 on the host, the ONLY thing left that can refuse a write
            // to it is the read-only bind. That is the claim MG-3 rests on, stated in its strongest form.
            MakeWorldWritable(barePath);
            MakeWorldWritable(agentRepoPath);
            MakeWorldWritable(launch!.WorktreePath);
            AssertHostWritable(barePath);

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
            // The update-ref probe is only meaningful if it had a real sha to write. Asserted FIRST,
            // because this exact frame once printed REFUSED unconditionally — a nested command
            // substitution passed escaped quotes to git, the inner rev-parse always failed, and the
            // probe reported a refusal it had never attempted.
            Assert.Equal("RESOLVED", ReadFrameValue(output, ResolvedFrame));
            Assert.Equal(Refused, ReadFrameValue(output, UpdateRefFrame));

            // ---- the controls: the probe could write, and the product still works ----
            // Without these "everything was refused" would also be what a broken shell reports.
            Assert.Equal("READ", ReadFrameValue(output, ReadFrame));
            Assert.Equal(Wrote, ReadFrameValue(output, OwnRepoFrame));
            Assert.Equal("COMMITTED", ReadFrameValue(output, CommitFrame));

            // ---- and the host-side truth: nothing the jail attempted landed ----
            Assert.Equal(mainShaBefore, HostGit(barePath, "rev-parse", mainBranch).Trim());
            Assert.False(File.Exists(Path.Combine(barePath, "objects", "ab", "planted")));
            Assert.NotEqual(0, AgentTestGit.Run(barePath, "rev-parse", "--verify", "--quiet", PlantedRef).Code);

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

    /// <summary>
    /// <c>chmod -R a+rwX</c>, so a jail whose uid matches nothing on this host can still write the trees
    /// it legitimately owns. Stands in for what <c>UsernsRemapPolicy</c>'s setgid <c>mainguard-jail</c>
    /// group does in production, which a bare CI runner has no equivalent of.
    /// </summary>
    private static void MakeWorldWritable(string root)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(root))
        {
            return;
        }

        const UnixFileMode dir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                 | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                 | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        File.SetUnixFileMode(root, dir);
        foreach (var child in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            try { File.SetUnixFileMode(child, dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var mode = File.GetUnixFileMode(file)
                           | UnixFileMode.UserRead | UnixFileMode.UserWrite
                           | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                           | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
                File.SetUnixFileMode(file, mode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>States out loud that the mirror is writable ON THE HOST, so a refusal observed inside the
    /// jail can only have come from the bind mount.</summary>
    private static void AssertHostWritable(string barePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var refsHeads = Path.Combine(barePath, "refs", "heads");
        Assert.True(
            File.GetUnixFileMode(refsHeads).HasFlag(UnixFileMode.OtherWrite),
            $"{refsHeads} must be world-writable on the host, or an in-jail refusal proves only a file mode");

        // And demonstrably so: the daemon side really can write here, right now.
        var canary = Path.Combine(refsHeads, "mg3-host-writability-canary");
        File.WriteAllText(canary, "host can write\n");
        File.Delete(canary);
    }

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

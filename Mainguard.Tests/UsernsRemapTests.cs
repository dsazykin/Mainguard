using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-17 — "user-namespaced" was claimed but no remap was enabled. These are the pure, VM-free half of
/// the fix: the mapping arithmetic, the daemon.json the bootstrapper writes, the sentinel-framed boot
/// probe (and specifically its inability to pass when it did not actually observe anything), and the
/// builder's refusal of the per-container opt-out. The parts that need a real dockerd — that the remap
/// takes effect, that a jail's writes land as host uid 101000 — are manual-matrix; see the PR body.
/// </summary>
public class UsernsRemapTests
{
    // ---- The mapping itself ----------------------------------------------------------------------

    [Fact]
    public void Mapping_ContainerIdsLandOnTheProvisionedHostRange()
    {
        // Container root is NOT host root any more — that is the whole point of the finding.
        Assert.Equal(100_000, UsernsRemapPolicy.HostIdFor(0));
        Assert.NotEqual(0, UsernsRemapPolicy.HostIdFor(0));

        // …and the agent CLI's uid is no longer the VM's own service uid.
        Assert.Equal(101_000, UsernsRemapPolicy.AgentHostUid);
        Assert.Equal(101_000, UsernsRemapPolicy.AgentHostGid);
        Assert.NotEqual(UsernsRemapPolicy.AgentContainerUid, UsernsRemapPolicy.AgentHostUid);

        // The supervisor uid (G2 control 1) stays distinct from the agent uid after remapping — a
        // mapping that collapsed them would hand the agent the OOB key K.
        Assert.NotEqual(UsernsRemapPolicy.HostIdFor(1000), UsernsRemapPolicy.HostIdFor(1001));
    }

    [Fact]
    public void Mapping_RefusesAnIdOutsideTheRange()
    {
        // Silently mapping to the kernel's overflow id (nobody) is how a bind mount ends up unusable
        // with nothing anywhere saying why.
        Assert.Throws<ArgumentOutOfRangeException>(() => UsernsRemapPolicy.HostIdFor(UsernsRemapPolicy.SubordinateCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => UsernsRemapPolicy.HostIdFor(-1));
    }

    [Fact]
    public void SubidFile_DeclaresExactlyTheRangeTheOwnershipIsProvisionedAgainst()
    {
        Assert.Equal("mainguard:100000:65536\n", UsernsRemapPolicy.SubidFileContent);
        // The chown target and the declared range must be the same arithmetic, not two constants that
        // happen to agree today.
        Assert.Equal(
            UsernsRemapPolicy.SubordinateBase + UsernsRemapPolicy.AgentContainerUid,
            UsernsRemapPolicy.AgentHostGid);
    }

    // ---- daemon.json ------------------------------------------------------------------------------

    [Fact]
    public void DaemonJson_CarriesTheRemapAndKeepsTheSubnetPin()
    {
        var json = FirstBootStep.DockerDaemonJson;

        Assert.Contains("\"userns-remap\": \"mainguard\"", json, StringComparison.Ordinal);
        // The pre-existing reason this file exists must not regress (Docker Desktop subnet collision).
        Assert.Contains("10.202.0.1/24", json, StringComparison.Ordinal);
        Assert.Contains("10.203.0.0/16", json, StringComparison.Ordinal);

        // It must be valid JSON — a trailing comma here bricks dockerd on every boot.
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("mainguard", parsed.RootElement.GetProperty("userns-remap").GetString());
    }

    [Fact]
    public void DaemonJson_MatchesTheOneBakedIntoTheMainguardOsPayload()
    {
        // The Dockerfile bakes daemon.json for a fresh import and FirstBootStep rewrites it for an
        // upgraded VM. If those two ever disagree, a freshly imported VM and an upgraded one run
        // different isolation postures — and nothing would say so.
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "build", "mainguardos", "Dockerfile"));
        Assert.Contains("\"userns-remap\": \"mainguard\"", dockerfile, StringComparison.Ordinal);
        Assert.Contains("printf 'mainguard:100000:65536\\n' > /etc/subuid", dockerfile, StringComparison.Ordinal);
        Assert.Contains("printf 'mainguard:100000:65536\\n' > /etc/subgid", dockerfile, StringComparison.Ordinal);
        // Whole-token, not a prefix: `mainguard-jail` is a prefix of any longer name, and a substring
        // assertion here would have accepted `groupadd -g 101000 mainguard-jail-something-else` —
        // caught by deliberately renaming it during the non-vacuity probe, which the loose form passed.
        Assert.Contains("groupadd -g 101000 mainguard-jail;", dockerfile, StringComparison.Ordinal);
        Assert.Contains("usermod -aG mainguard-jail mainguard;", dockerfile, StringComparison.Ordinal);
        Assert.Contains("-g mainguard-jail -m 2775 /home/mainguard/mainguard/repos", dockerfile, StringComparison.Ordinal);
        Assert.Contains("-g mainguard-jail -m 2775 /home/mainguard/mainguard/worktrees", dockerfile, StringComparison.Ordinal);
    }

    // ---- The boot probe: the assertion that must not be able to pass vacuously -------------------

    /// <summary>A probe output in the exact shape the in-VM script emits.</summary>
    private static string Probe(string securityOptions, string rootDir, string groups) =>
        $"MGUSERNS[{securityOptions}]MGROOT[{rootDir}]MGGROUPS[{groups}]";

    private const string RemappedRoot = "/var/lib/docker/100000.100000";
    private const string GoodOptions = "name=seccomp,profile=builtin;name=cgroupns;name=userns;";
    private const string GoodGroups = "mainguard docker mainguard-jail";

    [Fact]
    public void Probe_GreenVmIsSatisfied()
    {
        Assert.Null(UsernsRemapPolicy.DescribeUnsatisfied(Probe(GoodOptions, RemappedRoot, GoodGroups)));
    }

    [Fact]
    public void Probe_CannotPassWhenItObservedNothing()
    {
        // THE non-vacuity case. `docker info` failing, bash missing, the distro not answering: all
        // produce empty or partial output, and every one of them must be UNSATISFIED and must say that
        // it observed nothing — not "the remap is off" (indistinguishable from a real regression) and
        // certainly not "fine".
        foreach (var empty in new[] { "", "   ", "\n" })
        {
            var reason = UsernsRemapPolicy.DescribeUnsatisfied(empty);
            Assert.NotNull(reason);
            Assert.Contains("no output at all", reason!, StringComparison.Ordinal);
        }

        Assert.NotNull(UsernsRemapPolicy.DescribeUnsatisfied(null));

        // The shell frame is there (bash ran) but docker answered nothing — a different diagnosis.
        var dockerDead = UsernsRemapPolicy.DescribeUnsatisfied("MGGROUPS[mainguard docker mainguard-jail]");
        Assert.NotNull(dockerDead);
        Assert.Contains("docker info", dockerDead!, StringComparison.Ordinal);

        // A truncated frame (no closing bracket) is "did not observe", never a pass.
        Assert.NotNull(UsernsRemapPolicy.DescribeUnsatisfied("MGUSERNS[name=userns;"));
    }

    [Fact]
    public void Probe_RejectsAnUnremappedDaemon()
    {
        // Exactly the state the audit found: dockerd running, healthy, and not remapping at all.
        var reason = UsernsRemapPolicy.DescribeUnsatisfied(
            Probe("name=seccomp,profile=builtin;name=cgroupns;", "/var/lib/docker", GoodGroups));

        Assert.NotNull(reason);
        Assert.Contains("no user-namespace remapping", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_RejectsARemapToADifferentRange()
    {
        // "Some remap" is not the invariant: the mount ownership is provisioned against base 100000,
        // so a daemon remapped to any other base leaves every bind mount unusable by the jail while
        // `docker info` still reports name=userns. This is the check that tells those apart.
        var reason = UsernsRemapPolicy.DescribeUnsatisfied(
            Probe(GoodOptions, "/var/lib/docker/165536.165536", GoodGroups));

        Assert.NotNull(reason);
        Assert.Contains("not to the range this VM is provisioned for", reason!, StringComparison.Ordinal);
        Assert.Contains("165536.165536", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_RejectsADaemonUserOutsideTheSharedJailGroup()
    {
        // The remap can be perfect and the product still broken: without the shared group the daemon
        // cannot read or write the worktrees it hands the jail.
        var reason = UsernsRemapPolicy.DescribeUnsatisfied(Probe(GoodOptions, RemappedRoot, "mainguard docker"));

        Assert.NotNull(reason);
        Assert.Contains("mainguard-jail", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_GroupMatchIsWholeToken_NotSubstring()
    {
        // `mainguard` is a strict prefix of `mainguard-jail`, and the daemon user is ALWAYS in a group
        // called `mainguard`. A substring test would therefore have passed unconditionally — the exact
        // shape of instrument failure this repo has been bitten by before.
        var reason = UsernsRemapPolicy.DescribeUnsatisfied(Probe(GoodOptions, RemappedRoot, "mainguard docker"));
        Assert.NotNull(reason);

        // …and the inverse: a group whose name merely CONTAINS the required one is not the required one.
        Assert.NotNull(UsernsRemapPolicy.DescribeUnsatisfied(
            Probe(GoodOptions, RemappedRoot, "mainguard mainguard-jail-readonly")));
    }

    [Fact]
    public void Probe_SentinelFramesDoNotOverlap()
    {
        // The `NOAAAA`-contains-`AAAA` lesson: assert the three frame openers are mutually
        // non-overlapping, so no frame's content can ever be read as another's.
        var script = UsernsRemapPolicy.ProbeScript;
        foreach (var frame in new[] { "MGUSERNS[", "MGROOT[", "MGGROUPS[" })
        {
            Assert.Contains(frame, script, StringComparison.Ordinal);
        }

        foreach (var (a, b) in new[]
                 {
                     ("MGUSERNS[", "MGROOT["), ("MGUSERNS[", "MGGROUPS["), ("MGROOT[", "MGGROUPS["),
                 })
        {
            Assert.DoesNotContain(a, b, StringComparison.Ordinal);
            Assert.DoesNotContain(b, a, StringComparison.Ordinal);
        }

        // The security-option frame must be the one that carries `name=userns`, and no OTHER frame's
        // fixed text may contain it (or a failed docker info could still satisfy the test).
        Assert.DoesNotContain(UsernsRemapPolicy.UsernsSecurityOption, UsernsRemapPolicy.DockerRootDir, StringComparison.Ordinal);
    }

    // ---- The per-container knob -------------------------------------------------------------------

    [Fact]
    public void Spec_InheritsTheDaemonRemapByDefault()
    {
        var create = ContainerSpecBuilder.Build(Request());
        Assert.Equal(UsernsRemapPolicy.InheritDaemonRemap, create.HostConfig.UsernsMode);
        Assert.Equal(string.Empty, create.HostConfig.UsernsMode);
    }

    [Fact]
    public void Spec_RefusesTheUsernsOptOut()
    {
        // `--userns=host` puts the container back on host uids (container root = host root) while every
        // other hardening flag still reads as fully applied. It is a typed builder error, like the G2
        // quartet — and this is exactly the value the previous spec test asserted was passed through.
        var ex = Assert.Throws<SandboxSpecException>(
            () => ContainerSpecBuilder.Build(Request() with { UsernsMode = UsernsRemapPolicy.OptOutUsernsMode }));
        Assert.Contains("MG-17", ex.Message, StringComparison.Ordinal);

        // Anything unrecognised is refused too — a spec whose isolation posture nobody can name must
        // not reach the daemon.
        Assert.Throws<SandboxSpecException>(() => ContainerSpecBuilder.Build(Request() with { UsernsMode = "private" }));
    }

    [Fact]
    public void EngineOptions_DefaultToInheritingTheDaemonRemap()
    {
        // The seam the finding named: SandboxEngineOptions.UsernsMode used to be a bare "".
        var options = new SandboxEngineOptions("mainguard-agents", "http://mainguard-egress-proxy:8888");
        Assert.Equal(UsernsRemapPolicy.InheritDaemonRemap, options.UsernsMode);
    }

    // ---- Ownership: the mount sources ------------------------------------------------------------

    [Fact]
    public void OwnershipScript_CoversBothReadWriteMountSourcesAndNothingElse()
    {
        var script = UsernsRemapPolicy.MountOwnershipScript();

        // The two read-write bind-mount sources, grouped to the remapped agent gid with setgid on
        // directories so MG-3's new per-agent repositories inherit it by construction.
        Assert.Contains("$root/repos", script, StringComparison.Ordinal);
        Assert.Contains("$root/worktrees", script, StringComparison.Ordinal);
        Assert.Contains("gid=101000", script, StringComparison.Ordinal);
        Assert.Contains("chmod 2775", script, StringComparison.Ordinal);
        Assert.Contains("chmod g+s", script, StringComparison.Ordinal);

        // The read-only adapters mount is made readable, never group-writable: the jail may run the
        // shared CLIs and must never be able to modify what other agents execute.
        Assert.Contains("chmod -R a+rX \"$root/adapters\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("g+rwX \"$root/adapters\"", script, StringComparison.Ordinal);

        // And the daemon's own state stays out of it entirely — a recursive grant over the home
        // directory would hand the jail the keyring, the session token and the SQLite database.
        Assert.DoesNotContain(".mainguard", script, StringComparison.Ordinal);
        Assert.DoesNotContain("chown -R", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DrainScript_RemovesMainguardContainersAndNetworksAndNeverTheDistro()
    {
        var script = UsernsRemapPolicy.PreFlipDrainScript;

        Assert.Contains("docker rm -f", script, StringComparison.Ordinal);
        Assert.Contains("docker network rm", script, StringComparison.Ordinal);
        // G-12: never the VM-wide shutdown verb, and never anything that is not ours.
        Assert.DoesNotContain("--shutdown", script, StringComparison.Ordinal);
        Assert.DoesNotContain("docker system prune", script, StringComparison.Ordinal);
        Assert.Contains("mainguard", script, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupScript_IsIdempotentAndSignalsOnlyRealChanges()
    {
        var script = UsernsRemapPolicy.GroupProvisionScript;

        Assert.Contains("getent group mainguard-jail", script, StringComparison.Ordinal);
        Assert.Contains("groupadd -g 101000 mainguard-jail", script, StringComparison.Ordinal);
        Assert.Contains("usermod -aG mainguard-jail mainguard", script, StringComparison.Ordinal);
        // The sentinel is printed only inside the "changed" branch — an unconditional print would
        // bounce a healthy daemon on every provisioning re-run.
        var sentinelAt = script.IndexOf(UsernsRemapPolicy.GroupChangedSentinel, StringComparison.Ordinal);
        var guardAt = script.IndexOf("if [ \"$changed\" = 1 ]", StringComparison.Ordinal);
        Assert.True(guardAt >= 0 && sentinelAt > guardAt, "the changed-sentinel must be inside the changed branch");
    }

    [RequiresUnixFileModesFact]
    public void ProvisioningScripts_AreValidShell()
    {
        // These four scripts are C# string literals that no compiler and no other test ever executes —
        // they run exactly once, as root, inside a VM nobody is watching. A quoting slip there is a
        // silent provisioning failure, so at minimum the shell must be able to PARSE them. `bash -n`
        // reads and parses without executing anything.
        foreach (var (name, script) in new[]
                 {
                     (nameof(UsernsRemapPolicy.ProbeScript), UsernsRemapPolicy.ProbeScript),
                     (nameof(UsernsRemapPolicy.GroupProvisionScript), UsernsRemapPolicy.GroupProvisionScript),
                     (nameof(UsernsRemapPolicy.MountOwnershipScript), UsernsRemapPolicy.MountOwnershipScript()),
                     (nameof(UsernsRemapPolicy.PreFlipDrainScript), UsernsRemapPolicy.PreFlipDrainScript),
                 })
        {
            var psi = new System.Diagnostics.ProcessStartInfo("bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);

            using var process = System.Diagnostics.Process.Start(psi);
            Assert.NotNull(process);
            var stderr = process!.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"{name} is not valid shell: {stderr}");
        }
    }

    [RequiresUnixFileModesFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void MountOwnershipScript_CreatesTheTreeSetgid_AndIsIdempotent()
    {
        // Runs the REAL script (with the caller's own gid substituted for the remapped one, which only
        // root can assign) against a temp root, twice. What is proven here is the part that has nothing
        // to do with privilege and everything to do with the script being correct: the directories are
        // created, they end up 2775 — setgid, so MG-3's future per-agent directories inherit the group
        // BY CONSTRUCTION — and a second run converges instead of drifting.
        var root = Directory.CreateTempSubdirectory("mg17-own").FullName;
        try
        {
            var gid = UsernsRemapPolicy.AgentHostGid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var script = UsernsRemapPolicy.MountOwnershipScript(Path.Combine(root, "mainguard"))
                // The chown/chgrp targets require root; neutralise exactly those two so the structural
                // half of the script is exercised for real rather than mocked.
                .Replace("chgrp -R \"$gid\"", "true", StringComparison.Ordinal)
                .Replace("chown mainguard", "true mainguard", StringComparison.Ordinal)
                .Replace($"gid={gid}", "gid=$(id -g)", StringComparison.Ordinal);

            for (var run = 0; run < 2; run++)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("bash") { RedirectStandardError = true };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(script);
                using var process = System.Diagnostics.Process.Start(psi);
                Assert.NotNull(process);
                var stderr = process!.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, $"run {run} failed: {stderr}");

                foreach (var dir in new[] { "repos", "worktrees" })
                {
                    var path = Path.Combine(root, "mainguard", dir);
                    Assert.True(Directory.Exists(path), path + " must exist");
                    var mode = File.GetUnixFileMode(path);
                    Assert.True(mode.HasFlag(UnixFileMode.SetGroup), path + " must be setgid (2775)");
                    Assert.True(mode.HasFlag(UnixFileMode.GroupWrite), path + " must be group-writable");
                    Assert.True(mode.HasFlag(UnixFileMode.GroupExecute), path + " must be group-traversable");
                    Assert.False(mode.HasFlag(UnixFileMode.OtherWrite), path + " must NOT be world-writable");
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- Ownership: what the daemon does per worktree (runs for real on Linux) ---------------------

    [RequiresUnixFileModesFact]
    // The attribute already skips this on Windows; the platform annotation is what tells the CA1416
    // analyzer so, since it cannot see a Skip decided in an attribute constructor.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void GroupShare_MakesADaemonCheckoutWritableByTheJailGroup()
    {
        // The failure this prevents, end to end on a real filesystem: git checks a worktree out under
        // the daemon's 022 umask, so files land 0644 / dirs 0755. Once the jail is remapped it is
        // neither the owner nor in the owner's group by uid, so a 0644 source file is one the agent can
        // read and never edit — the product is broken with no error anywhere.
        var root = Directory.CreateTempSubdirectory("mg17-share").FullName;
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
            var source = Path.Combine(nested, "Program.cs");
            File.WriteAllText(source, "// agent edits this");
            var script = Path.Combine(nested, "build.sh");
            File.WriteAllText(script, "#!/bin/sh\n");
            var data = Path.Combine(nested, "data.json");
            File.WriteAllText(data, "{}");

            // Exactly what a 022-umask checkout leaves behind.
            File.SetUnixFileMode(source, Rw());
            File.SetUnixFileMode(data, Rw());
            File.SetUnixFileMode(script, Rw() | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(nested, Rwx());
            File.SetUnixFileMode(root, Rwx());

            Assert.False(File.GetUnixFileMode(source).HasFlag(UnixFileMode.GroupWrite));

            WorktreeManager.GroupShareRecursive(root);

            // Files: group-writable, and a plain data file did NOT become executable.
            Assert.True(File.GetUnixFileMode(source).HasFlag(UnixFileMode.GroupWrite));
            Assert.True(File.GetUnixFileMode(data).HasFlag(UnixFileMode.GroupWrite));
            Assert.False(File.GetUnixFileMode(data).HasFlag(UnixFileMode.GroupExecute));
            Assert.True(File.GetUnixFileMode(script).HasFlag(UnixFileMode.GroupExecute));

            // Directories: group rwx AND setgid, so anything the agent creates later keeps the shared
            // group instead of falling back to the creator's primary group.
            foreach (var dir in new[] { root, nested })
            {
                var mode = File.GetUnixFileMode(dir);
                Assert.True(mode.HasFlag(UnixFileMode.GroupWrite), dir + " must be group-writable");
                Assert.True(mode.HasFlag(UnixFileMode.GroupExecute), dir + " must be group-traversable");
                Assert.True(mode.HasFlag(UnixFileMode.SetGroup), dir + " must be setgid");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static UnixFileMode Rw() =>
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static UnixFileMode Rwx() =>
        Rw() | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private static ContainerSpecRequest Request() =>
        new(
            RepoHash: "abc123def456abc123",
            AgentId: "agent-1",
            WorktreePath: "/home/mainguard/mainguard/worktrees/abc123/agent-1",
            ImageRef: "mainguard-agent-base:latest",
            Limits: SandboxLimits.Default,
            NetworkName: "mainguard-agents",
            Credentials: CredTmpfsSpec.Create(1000, 1001),
            ProxyUrl: "http://mainguard-egress-proxy:8888",
            DnsServerAddress: "172.30.0.2");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mainguard.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

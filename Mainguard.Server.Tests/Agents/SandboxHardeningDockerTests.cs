using System;
using System.Linq;
using System.Threading.Tasks;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-07 RequiresDocker leg — the G-11/G-15/G2 runtime proofs the master doc names: docker-inspect
/// shows no Windows mounts and live userns/limits; the persistent jail re-starts rather than recreates;
/// the credential tmpfs is 0400/tmpfs; and the agent uid cannot read the supervisor-owned OOB key.
/// Gated by <see cref="RequiresDockerFactAttribute"/> so a Docker-less dev box skips; Linux CI runs them.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(DockerSuiteCollection.Name)]
public class SandboxHardeningDockerTests
{
    [RequiresDockerFact]
    public async Task Inspect_RunningAgentContainer_ShouldShowNoWindowsPaths_UsernsAndLimits()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync();
        var inspect = await fx.InspectAsync(handle.ContainerId);

        // G-11: no mount source resembles a Windows/WSL path.
        foreach (var mount in inspect.Mounts ?? Enumerable.Empty<Docker.DotNet.Models.MountPoint>())
        {
            Assert.DoesNotContain("/mnt/c", mount.Source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("drvfs", mount.Source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\", mount.Source);
        }

        // G-15: limits + hardening live on the running container.
        Assert.True(inspect.HostConfig.Memory > 0);
        Assert.NotNull(inspect.HostConfig.PidsLimit);
        Assert.Contains(inspect.HostConfig.SecurityOpt, o => o.Contains("no-new-privileges"));

        // MG-26: memory+pids left two axes uncapped. A busy loop allocates nothing and forks nothing,
        // so only a CPU ceiling bounds it; a descriptor leak hits the per-process file table long
        // before 512 pids. Both are ceilings on the live container now, not conventions.
        Assert.True(inspect.HostConfig.NanoCPUs > 0);
        Assert.Contains(inspect.HostConfig.Ulimits, u => u.Name == "nofile" && u.Hard > 0);
        Assert.Contains(inspect.HostConfig.Ulimits, u => u.Name == "nproc" && u.Hard > 0);
    }

    [RequiresDockerFact]
    public async Task NofileUlimit_ShouldBeEnforcedInsideTheJail()
    {
        // The rlimit is only real if the kernel applied it — read it back from a process in the jail.
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "rlimits");

        var soft = await fx.ExecAsync(handle.ContainerId, "sh", "-c", "ulimit -n");
        Assert.True(int.TryParse(soft.Stdout.Trim(), out var nofile),
            $"could not read the nofile rlimit from the jail: '{soft.Stdout}'");
        Assert.True(nofile > 0 && nofile <= Mainguard.Agents.Agents.Sandbox.SandboxLimits.DefaultNoFile,
            $"expected a bounded nofile ceiling, got {nofile}");
    }

    [RequiresDockerFact]
    public async Task PersistentJail_StartNotRecreate()
    {
        await using var fx = new SandboxFixture();
        var first = await fx.SpawnAsync(agentId: "jail-1");
        await fx.Engine.StopAsync(first.ContainerId);

        var second = await fx.SpawnAsync(agentId: "jail-1");

        Assert.Equal(first.ContainerId, second.ContainerId); // same container reused
        Assert.True(second.Reused);
    }

    [RequiresDockerFact]
    public async Task CredTmpfs_Mode0400_TmpfsBacked_PerAgent()
    {
        await using var fx = new SandboxFixture();
        var a1 = await fx.SpawnAsync(agentId: "agent-a");
        var a2 = await fx.SpawnAsync(agentId: "agent-b");

        var mode = await fx.ExecAsync(a1.ContainerId, "stat", "-c", "%a", "/run/secrets/agent.env");
        Assert.Equal("400", mode.Stdout.Trim());

        var fsType = await fx.ExecAsync(a1.ContainerId, "stat", "-f", "-c", "%T", "/run/secrets");
        Assert.Contains("tmpfs", fsType.Stdout, StringComparison.OrdinalIgnoreCase);

        // Per-agent: two distinct containers, two distinct credential surfaces.
        Assert.NotEqual(a1.ContainerId, a2.ContainerId);
    }

    [RequiresDockerFact]
    public async Task SupervisorMemory_NotReadableByAgent_ViaPtraceOrVmRead()
    {
        await using var fx = new SandboxFixture();
        var handle = await fx.SpawnAsync(agentId: "keycustody", agentUid: 1000, supervisorUid: 1001);

        // The exec runs as the agent uid (1000); the OOB key is 0400 owned by the supervisor uid (1001).
        // Reading the file must be denied — zero key bytes to the agent (G2 control 1).
        var read = await fx.ExecAsync(handle.ContainerId, "cat", "/run/secrets/oob.key");
        Assert.NotEqual(0, read.ExitCode);
        Assert.DoesNotContain("mainguard", read.Stdout); // no key material leaked to stdout

        // The memory-scrape vector is closed structurally by the seccomp denylist + no CAP_SYS_PTRACE
        // (asserted on every create request by ContainerSpecBuilderTests). A live ptrace attempt has no
        // syscall to make; here we prove the process cannot even see another uid's proc memory node.
        var scrape = await fx.ExecAsync(handle.ContainerId, "sh", "-c", "cat /proc/1/mem >/dev/null 2>&1; echo $?");
        Assert.NotEqual("0", scrape.Stdout.Trim());
    }
}

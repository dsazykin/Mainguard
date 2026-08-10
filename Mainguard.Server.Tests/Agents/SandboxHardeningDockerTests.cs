using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
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

    /// <summary>
    /// A jail created with the OLD flat <c>/run/secrets</c> is recreated rather than reused.
    ///
    /// <para>tmpfs entries are fixed at container create, exactly like mounts. Secrets are now written by
    /// their owners into per-owner <c>0700</c> directories — because a non-root <c>User</c> plus
    /// <c>no-new-privileges</c> leaves even a uid-0 exec with no <c>CAP_CHOWN</c> on the shipping engine
    /// — so reusing a pre-upgrade container would exec a non-root owner into a directory that does not
    /// exist and is not writable by it. That is the very <c>EPERM</c> the layout change removes,
    /// resurrected for every container that outlived the upgrade.</para>
    ///
    /// <para>Not theoretical: <c>SecretDeliveryDockerTests</c> failed on exactly this against a jail left
    /// behind by a pre-change run, and passed once it was gone. This test recreates that container
    /// deliberately so the next person does not have to discover it by accident.</para>
    /// </summary>
    [RequiresDockerFact]
    public async Task JailWithTheOldFlatSecretsTmpfs_IsRecreated_NotReused()
    {
        await using var fx = new SandboxFixture();

        // A healthy, current jail first — the control. Without it, "recreated" below could equally mean
        // "this spawn never reuses anything", which would make the assertion meaningless.
        var current = await fx.SpawnAsync(agentId: "stale-secrets");
        var reused = await fx.SpawnAsync(agentId: "stale-secrets");
        Assert.Equal(current.ContainerId, reused.ContainerId);
        Assert.True(reused.Reused);

        // Now degrade it to the pre-change layout the only way Docker allows: replace the container with
        // one that is IDENTICAL except for the tmpfs set.
        //
        // "Identical except" is the whole design of this test. A hand-built stand-in with no mounts and
        // no network also gets recreated — by the mirror/agent-repo/cache/network checks that already
        // existed — so it would pass whether or not the tmpfs check exists at all. (Measured: the first
        // version of this test did exactly that and stayed green with the check disabled.) Reusing the
        // real container's own HostConfig leaves the tmpfs as the ONLY difference, so a pass is
        // attributable to the tmpfs check and nothing else.
        var inspect = await fx.Docker.Containers.InspectContainerAsync(current.ContainerId);
        var name = inspect.Name.TrimStart('/');
        var legacyHostConfig = inspect.HostConfig;
        legacyHostConfig.Tmpfs = new Dictionary<string, string>(inspect.HostConfig.Tmpfs, StringComparer.Ordinal);
        legacyHostConfig.Tmpfs.Remove(CredTmpfsSpec.DirectoryOf(CredTmpfsSpec.DefaultCredentialPath));
        legacyHostConfig.Tmpfs.Remove(CredTmpfsSpec.DirectoryOf(CredTmpfsSpec.DefaultOobKeyPath));

        await fx.Docker.Containers.RemoveContainerAsync(
            current.ContainerId, new ContainerRemoveParameters { Force = true });

        var legacy = await fx.Docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            // Config.Image (the ref the container was CREATED with), not inspect.Image (the resolved
            // id) — otherwise the image-identity check fires and recreates for that reason instead.
            Image = inspect.Config.Image,
            User = inspect.Config.User,
            Cmd = inspect.Config.Cmd,
            Env = inspect.Config.Env,
            WorkingDir = inspect.Config.WorkingDir,
            Labels = inspect.Config.Labels,
            HostConfig = legacyHostConfig,
        });
        await fx.Docker.Containers.StartContainerAsync(legacy.ID, new ContainerStartParameters());

        var afterUpgrade = await fx.SpawnAsync(agentId: "stale-secrets");

        Assert.NotEqual(legacy.ID, afterUpgrade.ContainerId);
        Assert.False(afterUpgrade.Reused);

        // …and the replacement really carries the per-owner directories, so the recreate fixed the thing
        // it was triggered by rather than merely churning the container.
        var fresh = await fx.Docker.Containers.InspectContainerAsync(afterUpgrade.ContainerId);
        Assert.Contains(CredTmpfsSpec.DirectoryOf(CredTmpfsSpec.DefaultCredentialPath), fresh.HostConfig.Tmpfs.Keys);
        Assert.Contains(CredTmpfsSpec.DirectoryOf(CredTmpfsSpec.DefaultOobKeyPath), fresh.HostConfig.Tmpfs.Keys);
    }

    [RequiresDockerFact]
    public async Task CredTmpfs_Mode0400_TmpfsBacked_PerAgent()
    {
        await using var fx = new SandboxFixture();
        var a1 = await fx.SpawnAsync(agentId: "agent-a");
        var a2 = await fx.SpawnAsync(agentId: "agent-b");

        // Spelled through the constants: the secrets moved into per-owner directories when the in-jail
        // chown was removed (a non-root User plus no-new-privileges leaves even a uid-0 exec with no
        // CAP_CHOWN on Docker 20.10.24), and a hand-written path here would go on asserting about a file
        // the spec no longer creates.
        var credentialPath = CredTmpfsSpec.DefaultCredentialPath;
        var facts = await fx.ExecAsync(a1.ContainerId, "stat", "-c", "%a %u", credentialPath);
        Assert.Equal("400 1000", facts.Stdout.Trim()); // 0400 AND owned by the agent uid that must read it

        // The directory the secret actually lives in is the tmpfs — and the exec runs as the agent uid,
        // so this also proves the owner can reach its own secret's filesystem.
        var fsType = await fx.ExecAsync(
            a1.ContainerId, "stat", "-f", "-c", "%T", CredTmpfsSpec.DirectoryOf(credentialPath));
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
        var read = await fx.ExecAsync(handle.ContainerId, "cat", CredTmpfsSpec.DefaultOobKeyPath);
        Assert.NotEqual(0, read.ExitCode);
        Assert.DoesNotContain("mainguard", read.Stdout); // no key material leaked to stdout

        // And denied one level EARLIER than it used to be. K now lives in the supervisor uid's own 0700
        // directory, so the agent cannot open, list or create in it at all — where before it could
        // traverse the shared 0711 directory to the file and was stopped only by the file's own mode.
        var list = await fx.ExecAsync(
            handle.ContainerId, "ls", CredTmpfsSpec.DirectoryOf(CredTmpfsSpec.DefaultOobKeyPath));
        Assert.NotEqual(0, list.ExitCode);
        var plant = await fx.ExecAsync(
            handle.ContainerId, "touch", CredTmpfsSpec.DefaultOobKeyPath + ".partial");
        Assert.NotEqual(0, plant.ExitCode);

        // ---- and the MEMORY half of the same control, attempted for real ----
        //
        // This used to be `cat /proc/1/mem >/dev/null 2>&1; echo $?` with a non-zero exit required, and
        // that assertion could not fail: `cat` on ANY /proc/<pid>/mem reads sequentially from offset 0,
        // an address never mapped in any process, so it exits non-zero before one permission check
        // matters. Measured: `cat /proc/self/mem` — the caller's OWN memory, every check trivially
        // satisfied — also exits 1. It held under `--privileged` + `seccomp=unconfined` +
        // CAP_SYS_PTRACE. It was the sole runtime evidence for G2 control 1 and it was evidence of
        // kernel read semantics, not of hardening. The probe below makes the syscalls.
        var frames = await JailSyscallProbe.RunAsync(fx, handle.ContainerId);

        // Self-directed first, and it is the frame that makes every other one attributable: against its
        // OWN address space the kernel's ptrace check returns early, Yama does not apply, the address is
        // mapped and the length is right. A refusal here can only be the seccomp filter — and without
        // the profile this call SUCCEEDS and returns the byte count. A cross-process probe alone would
        // go green on a jail carrying no profile at all, because the runner's ptrace_scope=1 refuses it
        // anyway.
        var (selfRc, selfErrno) = JailSyscallProbe.ReadCall(frames, JailSyscallProbe.ProcessVmReadvSelf);
        Assert.True(selfRc < 0,
            $"process_vm_readv read {selfRc} bytes of this process's own memory — the seccomp filter is not "
            + "in the path. Stock moby's default profile ALLOWS this syscall; the three memory-inspection "
            + "denials are the only thing this profile adds over it.");
        Assert.Equal(JailSyscallProbe.Eperm, selfErrno);

        var (tracemeRc, tracemeErrno) = JailSyscallProbe.ReadCall(frames, JailSyscallProbe.PtraceTraceme);
        Assert.True(tracemeRc < 0,
            "ptrace(PTRACE_TRACEME) succeeded. It requires no capability and no permission over any other "
            + "task, so the only thing that can refuse it is the seccomp filter — and it did not.");
        Assert.Equal(JailSyscallProbe.Eperm, tracemeErrno);

        // Then the vector this control is named for: another process's memory, both ways in.
        var (initRc, initErrno) = JailSyscallProbe.ReadCall(frames, JailSyscallProbe.ProcessVmReadvInit);
        Assert.True(initRc < 0, $"process_vm_readv read {initRc} bytes out of pid 1's address space.");
        Assert.Equal(JailSyscallProbe.Eperm, initErrno);

        var (attachRc, attachErrno) = JailSyscallProbe.ReadCall(frames, JailSyscallProbe.PtraceAttachInit);
        Assert.True(attachRc < 0, "ptrace(PTRACE_ATTACH) attached to pid 1.");
        Assert.Equal(JailSyscallProbe.Eperm, attachErrno);
    }
}

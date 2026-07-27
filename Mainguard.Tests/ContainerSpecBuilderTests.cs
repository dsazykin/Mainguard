using System.Linq;
using System.Text.Json;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The pure, security-critical heart of P2-07: asserts every hardening flag AND the G2 per-container
/// quartet on the create request the daemon hands to <c>CreateContainerAsync</c>. These run on every
/// CI leg (no Docker needed) — they are the floor the RequiresDocker suite stands on.
/// </summary>
public class ContainerSpecBuilderTests
{
    private const string Ext4Worktree = "/home/mainguard/mainguard/worktrees/abc123/agent-1";
    private const int AgentUid = 1000;
    private const int SupervisorUid = 1001;

    /// <summary>The egress proxy's address on the agent network — what MG-7 pins as the jail's resolver.</summary>
    private const string ProxyDns = "172.30.0.2";

    private static ContainerSpecRequest ValidRequest(string worktree = Ext4Worktree) =>
        new(
            RepoHash: "abc123def456abc123",
            AgentId: "agent-1",
            WorktreePath: worktree,
            ImageRef: "mainguard-agent-base:latest",
            Limits: new SandboxLimits(4L * 1024 * 1024 * 1024, 256),
            NetworkName: "mainguard-agents",
            Credentials: CredTmpfsSpec.Create(AgentUid, SupervisorUid),
            ProxyUrl: "http://mainguard-egress-proxy:8888",
            UsernsMode: "host",
            DnsServerAddress: ProxyDns);

    [Fact]
    public void Build_SetsAllHardeningFlags()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest());
        var host = create.HostConfig;

        Assert.Contains("no-new-privileges", host.SecurityOpt);
        Assert.Equal("host", host.UsernsMode);
        Assert.Equal(4L * 1024 * 1024 * 1024, host.Memory);
        Assert.Equal(256, host.PidsLimit);
        Assert.True(host.ReadonlyRootfs);
        Assert.False(host.Privileged);

        Assert.Contains("/dev/shm", host.Tmpfs.Keys);
        Assert.Contains("/run/secrets", host.Tmpfs.Keys);

        var mount = Assert.Single(host.Mounts);
        Assert.Equal("bind", mount.Type);
        Assert.Equal(Ext4Worktree, mount.Source);
        Assert.Equal("/workspace", mount.Target);
    }

    [Fact]
    public void Build_SeccompProfile_DeniesMemoryInspectionSyscalls()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest());
        var seccomp = create.HostConfig.SecurityOpt.Single(o => o.StartsWith("seccomp="));
        Assert.DoesNotContain("unconfined", seccomp);

        using var doc = JsonDocument.Parse(seccomp["seccomp=".Length..]);

        // Deny-by-default (the moby default profile), NOT an ALLOW-all overlay — G-15.
        Assert.Equal("SCMP_ACT_ERRNO", doc.RootElement.GetProperty("defaultAction").GetString());

        var syscalls = doc.RootElement.GetProperty("syscalls");
        var denied = syscalls.EnumerateArray()
            .Where(r => r.GetProperty("action").GetString() == "SCMP_ACT_ERRNO")
            .SelectMany(r => r.GetProperty("names").EnumerateArray().Select(n => n.GetString()))
            .ToHashSet();

        Assert.Contains("ptrace", denied);
        Assert.Contains("process_vm_readv", denied);
        Assert.Contains("process_vm_writev", denied);
    }

    [Fact]
    public void Build_DropsAllCaps_AndNeverAddsSysPtrace()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest());
        Assert.Contains("ALL", create.HostConfig.CapDrop);
        Assert.DoesNotContain(create.HostConfig.CapAdd, c => c.Contains("SYS_PTRACE"));
    }

    [Fact]
    public void Build_KTmpfs_OwnedBySupervisorUid_DistinctFromAgent_Mode0400()
    {
        var creds = CredTmpfsSpec.Create(AgentUid, SupervisorUid);
        Assert.NotEqual(creds.AgentUid, creds.SupervisorUid);
        Assert.Equal(0b100_000_000, creds.Mode); // 0400
        Assert.Equal("/run/secrets/oob.key", creds.OobKeyPath);

        // The container process runs as the agent uid (it cannot read the supervisor-owned K file).
        var create = ContainerSpecBuilder.Build(ValidRequest());
        Assert.Equal(AgentUid.ToString(), create.User);
    }

    [Fact]
    public void CredTmpfsSpec_SharedUid_ThrowsTyped()
    {
        Assert.Throws<SandboxSpecException>(() => CredTmpfsSpec.Create(1000, 1000));
    }

    [Theory]
    [InlineData("/mnt/c/Users/dev/repo")]
    [InlineData("/mnt/d/work")]
    [InlineData(@"C:\Users\dev\repo")]
    [InlineData(@"\\wsl.localhost\MainguardEnv\home\dev\repo")]
    [InlineData(@"\\server\share\repo")]
    public void Build_RejectsWindowsAndUncMounts_Typed(string badSource)
    {
        Assert.Throws<SandboxSpecException>(() => ContainerSpecBuilder.Build(ValidRequest(badSource)));
    }

    [Fact]
    public void Build_Env_CarriesProxyOnly_NoSecrets()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest());

        Assert.Contains("HTTP_PROXY=http://mainguard-egress-proxy:8888", create.Env);
        Assert.Contains("HTTPS_PROXY=http://mainguard-egress-proxy:8888", create.Env);
        Assert.Contains(create.Env, e => e.StartsWith("NO_PROXY="));

        // No env var is secret-shaped (G-13): the credential tmpfs is the only secret carrier.
        foreach (var entry in create.Env)
        {
            var name = entry.Split('=', 2)[0].ToUpperInvariant();
            if (name is "HTTP_PROXY" or "HTTPS_PROXY" or "NO_PROXY") continue;
            Assert.DoesNotContain("TOKEN", name);
            Assert.DoesNotContain("SECRET", name);
            Assert.DoesNotContain("KEY", name);
            Assert.DoesNotContain("PASSWORD", name);
        }
    }

    [Fact]
    public void Build_DoesNotSetPtraceScopeSysctl()
    {
        // G2 control 2 (kernel.yama.ptrace_scope) is VM-wide (P2-05); it must NOT be on the create request.
        var create = ContainerSpecBuilder.Build(ValidRequest());
        Assert.True(create.HostConfig.Sysctls is null
            || !create.HostConfig.Sysctls.Keys.Any(k => k.Contains("ptrace_scope")));
    }

    [Fact]
    public void ContainerName_IsStablePerRepoAndAgent()
    {
        var a = ContainerSpecBuilder.ContainerName("abc123def456abc", "agent-1");
        var b = ContainerSpecBuilder.ContainerName("abc123def456abc", "agent-1");
        Assert.Equal(a, b);
        Assert.StartsWith("mainguard-", a);
    }

    // ---- The bare-mirror mount (in-jail git: the worktree's gitdir pointer must resolve) ----

    [Fact]
    public void Build_WithBareRepoPath_MountsItReadWrite_AtItsIdenticalVmPath()
    {
        const string bare = "/home/mainguard/mainguard/repos/abc123def456abc123.git";
        var create = ContainerSpecBuilder.Build(ValidRequest() with { BareRepoPath = bare });

        var mount = Assert.Single(create.HostConfig.Mounts, m => m.Source == bare);
        // Target == Source: the worktree's .git file carries this absolute VM path; anything else
        // leaves the gitdir pointer dangling and in-jail git dead ("not a git repository").
        Assert.Equal(bare, mount.Target);
        // Read-write: commits write objects + the agent/<id> ref into the mirror's common dir.
        Assert.False(mount.ReadOnly);
    }

    [Fact]
    public void Build_WithoutBareRepoPath_CarriesNoMirrorMount()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest());
        var mount = Assert.Single(create.HostConfig.Mounts);
        Assert.Equal("/workspace", mount.Target);
    }

    [Theory]
    [InlineData("/mnt/c/Users/dev/repos/abc.git")]
    [InlineData(@"C:\mainguard\repos\abc.git")]
    [InlineData(@"\\wsl.localhost\MainguardEnv\home\repos\abc.git")]
    public void Build_RejectsNonExt4BareRepoPath_Typed(string badSource)
    {
        Assert.Throws<SandboxSpecException>(() =>
            ContainerSpecBuilder.Build(ValidRequest() with { BareRepoPath = badSource }));
    }

    // ---- PR3: the coordinator's read-only agent-IPC mount ----

    [Fact]
    public void Build_WithIpcDir_MountsItReadOnly_AtTheFixedTarget()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest() with
        {
            IpcDirPath = "/home/mainguard/.mainguard/agent-ipc/agent-1",
        });

        var mount = Assert.Single(create.HostConfig.Mounts,
            m => m.Target == Mainguard.Agents.Agents.Ipc.AgentIpcPaths.SandboxMount);
        Assert.Equal("/home/mainguard/.mainguard/agent-ipc/agent-1", mount.Source);
        Assert.True(mount.ReadOnly); // the jail can dial the socket, never replace shim/socket
    }

    [Fact]
    public void Build_WithoutIpcDir_CarriesNoIpcMount()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest());
        Assert.DoesNotContain(create.HostConfig.Mounts,
            m => m.Target == Mainguard.Agents.Agents.Ipc.AgentIpcPaths.SandboxMount);
    }

    [Theory]
    [InlineData("/mnt/c/Users/dev/ipc")]
    [InlineData(@"C:\mainguard\ipc")]
    [InlineData(@"\\wsl.localhost\MainguardEnv\home\ipc")]
    public void Build_RejectsNonExt4IpcDir_Typed(string badSource)
    {
        Assert.Throws<Mainguard.Git.Exceptions.SandboxSpecException>(() =>
            ContainerSpecBuilder.Build(ValidRequest() with { IpcDirPath = badSource }));
    }

    // ---- MG-7: the jail's resolver is PINNED to the egress proxy's dnsmasq ----

    [Fact]
    public void Build_PinsTheJailResolver_ToTheEgressProxy()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest());

        // Without HostConfig.Dns, Docker hands the container its embedded resolver at 127.0.0.11, which
        // forwards to the VM's upstream DNS. The rendered NXDOMAIN dnsmasq then lives only inside the
        // proxy container and is never consulted — "DNS exfiltration is blocked" would be a no-op.
        var dns = Assert.Single(create.HostConfig.DNS);
        Assert.Equal(ProxyDns, dns);
    }

    [Fact]
    public void Build_OnTheDefaultDenyNetwork_WithoutADnsPin_ThrowsTyped()
    {
        // Fail closed: the default-deny network exists so the proxy is the only route out. Spawning
        // there with the embedded resolver still in play is the exact hole MG-7 names.
        var unpinned = ValidRequest() with { DnsServerAddress = null };
        Assert.Throws<SandboxSpecException>(() => ContainerSpecBuilder.Build(unpinned));
    }

    [Fact]
    public void Build_OffTheDefaultDenyNetwork_MayGoUnpinned()
    {
        // The merge-queue / lifecycle harnesses run jails on `bridge` with no proxy at all; pinning them
        // at an address that does not exist would break them for no security gain.
        var create = ContainerSpecBuilder.Build(ValidRequest() with
        {
            NetworkName = "bridge",
            DnsServerAddress = null,
        });

        Assert.True(create.HostConfig.DNS is null || create.HostConfig.DNS.Count == 0);
    }

    [Theory]
    [InlineData("127.0.0.11")]  // Docker's embedded resolver — precisely what the pin replaces
    [InlineData("127.0.0.1")]   // inside the jail's netns, loopback is the jail itself
    [InlineData("mainguard-egress-proxy")] // Docker's Dns list takes addresses, not names
    [InlineData("not-an-ip")]
    [InlineData("::1")]
    public void Build_RejectsAUselessDnsPin_Typed(string bad)
    {
        Assert.Throws<SandboxSpecException>(() =>
            ContainerSpecBuilder.Build(ValidRequest() with { DnsServerAddress = bad }));
    }

    // ---- MG-26: the jail's CPU + rlimit ceilings ----

    [Fact]
    public void Build_SetsACpuCeiling_MemoryAndPidsDoNotBoundABusyLoop()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest() with
        {
            Limits = new SandboxLimits(4L * 1024 * 1024 * 1024, 256, Cpus: 1.5),
        });

        // NanoCPUs is the cgroup cpu.max ceiling: 1e9 nanoCPU == one core.
        Assert.Equal(1_500_000_000L, create.HostConfig.NanoCPUs);
    }

    [Fact]
    public void Build_SetsNofileAndNprocUlimits()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest() with
        {
            Limits = new SandboxLimits(4L * 1024 * 1024 * 1024, 256, NoFile: 2048, NProc: 3000),
        });

        var nofile = Assert.Single(create.HostConfig.Ulimits, u => u.Name == "nofile");
        Assert.Equal(2048, nofile.Soft);
        Assert.Equal(2048, nofile.Hard);

        var nproc = Assert.Single(create.HostConfig.Ulimits, u => u.Name == "nproc");
        Assert.Equal(3000, nproc.Soft);
        Assert.Equal(3000, nproc.Hard);
    }

    [Fact]
    public void SandboxLimits_Default_BoundsEveryAxis_AndKeepsNprocAbovePids()
    {
        var limits = SandboxLimits.Default;

        Assert.True(limits.MemoryBytes > 0);
        Assert.True(limits.Pids > 0);
        Assert.True(limits.Cpus > 0);
        Assert.True(limits.NoFile > 0);

        // Docker's nproc ulimit is enforced per REAL uid, and with userns-remap every jail shares one
        // host uid — an nproc at or below the pids ceiling would make one agent's processes count
        // against the next agent's fork budget. The per-container bound is PidsLimit; nproc is the
        // outer backstop and must sit above it.
        Assert.True(limits.NProc > limits.Pids);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Build_WithNoCpuCeiling_ThrowsTyped(double cpus)
    {
        Assert.Throws<SandboxSpecException>(() => ContainerSpecBuilder.Build(ValidRequest() with
        {
            Limits = new SandboxLimits(4L * 1024 * 1024 * 1024, 256, Cpus: cpus),
        }));
    }

    [Theory]
    [InlineData(0L, SandboxLimits.DefaultNProc)]
    [InlineData(SandboxLimits.DefaultNoFile, 0L)]
    public void Build_WithAMissingRlimit_ThrowsTyped(long nofile, long nproc)
    {
        Assert.Throws<SandboxSpecException>(() => ContainerSpecBuilder.Build(ValidRequest() with
        {
            Limits = new SandboxLimits(4L * 1024 * 1024 * 1024, 256, NoFile: nofile, NProc: nproc),
        }));
    }
}

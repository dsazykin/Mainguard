using System;
using Mainguard.Agents.Agents;
using Mainguard.Git.Audit;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

// The composition-root substrate choice (ESC §0.3), pinned so a refactor cannot silently hand
// macOS the WSL2 substrate (whose sync remote is a \\wsl.localhost UNC path) or vice versa.
// Construction needs no live Docker engine — every collaborator connects lazily.
public class AgentEnvironmentFactoryTests
{
    [Fact]
    public void CreateForHost_ShouldResolveThePlatformSubstrate()
    {
        var environment = AgentEnvironmentFactory.CreateForHost(new InMemoryAuditLog(), gatewayEndpoint: null);

        if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacHostAgentEnvironment>(environment);
            Assert.Equal("macos-host", environment.SubstrateId);
        }
        else
        {
            Assert.IsType<Wsl2AgentEnvironment>(environment);
            Assert.Equal("wsl2", environment.SubstrateId);
        }
    }

    [MacOnlyFact]
    public void MacHost_SyncRemote_ShouldBeTheLocalBarePath()
    {
        var environment = new MacHostAgentEnvironment(vmRoot: "/tmp/mainguard-test-root");

        var remote = environment.ResolveSyncRemote("abc123");

        // SC-2: the name is substrate-local and appears in exactly one production method; the URL
        // is the plain local bare path — daemon and client are the same machine on macos-host.
        Assert.Equal("mainguard-local", remote.Name);
        Assert.Equal("/tmp/mainguard-test-root/repos/abc123.git", remote.Url);
    }

    [MacOnlyFact]
    public void MacHost_Toolchains_ShouldInstallViaContainers_AndMountTheMacRoot()
    {
        var environment = new MacHostAgentEnvironment(vmRoot: "/tmp/mainguard-test-root");

        // In-jail CLIs and toolchains are linux — installs go through the container-backed host,
        // and the tree the spawn path bind-mounts is the MAC daemon-side root, never the VM
        // constant the interface defaults to (which does not exist on this host).
        Assert.NotNull(((IAgentEnvironment)environment).Toolchains);
        Assert.Equal(
            System.IO.Path.Combine(Mainguard.Git.MainguardPaths.HomeDirectory(), "mainguard", "toolchains"),
            ((IAgentEnvironment)environment).ToolchainsRootPath);
    }
}

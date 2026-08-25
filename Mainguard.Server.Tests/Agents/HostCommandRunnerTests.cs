using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

// The macos-host IWslRunner: in-distro shapes run their inner command directly on the host;
// VM lifecycle verbs are a loud typed refusal, never an emulation.
public class HostCommandRunnerTests
{
    [Fact]
    public void InDistroShape_ShouldStripToTheInnerCommand()
        => Assert.Equal(new[] { "docker", "image", "ls" },
            HostCommandRunner.StripInDistroPrefix(WslCommands.InDistro("docker", "image", "ls")));

    [Fact]
    public void InDistroAsRootShape_ShouldStripToTheInnerCommand_RunAsCurrentUser()
        => Assert.Equal(new[] { "docker", "load", "-i", "/x.tar" },
            HostCommandRunner.StripInDistroPrefix(WslCommands.InDistroAsRoot("docker", "load", "-i", "/x.tar")));

    [Fact]
    public void ALifecycleVerb_ShouldNotStrip()
        => Assert.Null(HostCommandRunner.StripInDistroPrefix(WslCommands.Terminate()));

    [UnixOnlyFact]
    public async Task RunAsync_InDistroShape_RunsTheCommandOnTheHost()
    {
        var result = await new HostCommandRunner()
            .RunAsync(WslCommands.InDistro("/bin/echo", "mainguard-host"), stdin: null, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("mainguard-host", result.StdOut);
    }

    [UnixOnlyFact]
    public async Task RunAsync_Stdin_ReachesTheChild()
    {
        var result = await new HostCommandRunner()
            .RunAsync(WslCommands.InDistro("/bin/cat"), stdin: "fed-via-stdin", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fed-via-stdin", result.StdOut);
    }

    [Fact]
    public async Task RunAsync_LifecycleVerb_ThrowsTyped()
        => await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HostCommandRunner().RunAsync(WslCommands.Terminate(), stdin: null, CancellationToken.None));
}

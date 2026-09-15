using System;
using System.IO;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The one assertion that keeps the conversation-store reclaim from becoming a general primitive.
///
/// <para><see cref="DockerSandboxEngine.TryEmpty"/> runs <c>find -mindepth 1 -delete</c> as ROOT in a
/// container with the named directory bind-mounted. That is the privilege the daemon does not otherwise
/// have, and it exists for exactly one reason: a jail writes its transcripts as its own uid, in a
/// directory the daemon can neither write nor chmod, so a clean stop would otherwise leave the store on
/// disk for a recurring <c>pr-&lt;n&gt;</c> id to resume into.</para>
///
/// <para>The refusal is checked BEFORE any Docker call, so these run without a daemon.</para>
/// </summary>
public sealed class ConversationReclaimRefusalTests
{
    private static DockerSandboxEngine NewEngine()
        => new(DockerEndpointResolver.CreateClient(), new SandboxEngineOptions(string.Empty, string.Empty));

    [Theory]
    [InlineData("/home/mainguard/mainguard/repos/abc")]
    [InlineData("/home/mainguard/mainguard/worktrees/abc/agent-1")]
    [InlineData("/home/mainguard/mainguard/caches/abc/agent-1")]
    [InlineData("/home/mainguard/.claude")]
    [InlineData("/")]
    [InlineData("")]
    public void APathOutsideAConversationsTree_IsRefused(string path)
        // Not "does not delete it" — it must not even start a privileged container for it.
        => Assert.False(NewEngine().TryEmpty(path));

    [Fact]
    public void AConversationStoreThatDoesNotExist_IsAlreadyEmpty_AndStartsNoContainer()
    {
        // Idempotent teardown: a second release, or one for an agent that never got a store, is a
        // no-op rather than a container start.
        var path = Path.Combine(
            Path.GetTempPath(), "mg-reclaim-" + Guid.NewGuid().ToString("N"), "conversations", "repo", "agent");
        Assert.True(NewEngine().TryEmpty(path));
    }
}

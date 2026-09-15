using System;
using System.Linq;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The in-jail store probe's pure half: the parser that decides whether a started container really has
/// writable conversation stores.
///
/// <para>This detector carries more weight than its size suggests. The store's mount target sits under
/// the tmpfs <c>$HOME</c> — that is where the CLI reads it — so a mount that did not take produces NO
/// error at all: the CLI writes its transcripts to the tmpfs, the whole session works, and the loss is
/// discovered later by whoever came back for the conversation. The probe is the one moment the
/// difference is observable, so a parser that reads an empty pipe as PASS would hand the feature back
/// its original failure mode with a green light on top.</para>
/// </summary>
public class ConversationStoreProbeTests
{
    private const string Target = "/home/agent/.claude/projects";

    [Fact]
    public void AnOkFrame_IsNotAFailure()
        => Assert.Null(ConversationStorePolicy.DescribeProbeFailure("MGCONV[OK]", 0));

    [Fact]
    public void NoFrameAtAll_IsAFailure_NotAPass()
    {
        // The case a naive `!stdout.Contains("UNWRITABLE")` reads as success: a dead container, a missing
        // shell, or a transport that dropped the output all produce nothing. "The probe did not run" is
        // its own reported reason, never either verdict.
        var failure = ConversationStorePolicy.DescribeProbeFailure(string.Empty, exitCode: 0);
        Assert.NotNull(failure);
        Assert.Contains("did not run", failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void ATruncatedFrame_IsAFailure()
        // Half a frame is not a verdict.
        => Assert.NotNull(ConversationStorePolicy.DescribeProbeFailure("MGCONV[OK", 0));

    [Fact]
    public void AMissingMount_IsReported_WithThePathAndTheConsequence()
    {
        var failure = ConversationStorePolicy.DescribeProbeFailure($"MGCONV[MISSING:{Target}]", 0);
        Assert.NotNull(failure);
        Assert.Contains(Target, failure!, StringComparison.Ordinal);
        // The message has to name the CONSEQUENCE, because "directory missing" alone reads as harmless.
        Assert.Contains("tmpfs", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnwritableMount_IsReported_WithTheOwnershipRemedy()
    {
        var failure = ConversationStorePolicy.DescribeProbeFailure($"MGCONV[UNWRITABLE:{Target}]", 0);
        Assert.NotNull(failure);
        Assert.Contains(Target, failure!, StringComparison.Ordinal);
        Assert.Contains(UsernsRemapPolicy.JailGroupName, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognisedVerdict_IsAFailure_NotSilentlyTreatedAsOk()
        => Assert.NotNull(ConversationStorePolicy.DescribeProbeFailure("MGCONV[WHAT]", 0));

    [Fact]
    public void TheVerdictTokens_CannotBeReadOutOfOneAnother()
    {
        // MISSING/UNWRITABLE/OK are mutually non-overlapping, so no substring test can confuse them —
        // the reason the package-cache probe frames its verdicts the same way.
        Assert.Null(ConversationStorePolicy.DescribeProbeFailure("MGCONV[OK]", 0));
        Assert.NotNull(ConversationStorePolicy.DescribeProbeFailure("MGCONV[MISSING:/x]", 0));
        Assert.NotNull(ConversationStorePolicy.DescribeProbeFailure("MGCONV[UNWRITABLE:/x]", 0));
    }

    [Fact]
    public void TheProbeCommand_PassesTargetsPositionally_NeverInterpolatedIntoTheScript()
    {
        // Same rule as every other in-jail command built from a manifest-sourced path: the path is an
        // argv entry, so nothing in it can be read as shell syntax.
        var command = ConversationStorePolicy.WritabilityProbe(new[] { Target });

        Assert.Equal("sh", command[0]);
        Assert.Equal("-c", command[1]);
        Assert.DoesNotContain(Target, command[2], StringComparison.Ordinal);
        Assert.Contains(Target, command);
    }

    [Fact]
    public void TheProbeCommand_CoversEveryDeclaredTarget()
    {
        var command = ConversationStorePolicy.WritabilityProbe(
            new[] { Target, "/home/agent/.config/sessions" });

        Assert.Contains(Target, command);
        Assert.Contains("/home/agent/.config/sessions", command);
    }

    [Fact]
    public void TheProbeCommand_RefusesToBeBuiltWithNoTargets()
        // A probe over nothing would print OK and prove nothing — the exact "green for the wrong reason"
        // shape this whole area refuses.
        => Assert.Throws<ArgumentException>(
            () => ConversationStorePolicy.WritabilityProbe(Array.Empty<string>()));

    // ---- the mount parents ---------------------------------------------------------------------

    [Fact]
    public void TheMountParent_IsTheDirectoryBetweenHomeAndTheStore()
        // /home/agent/.claude for a .claude/projects store: the directory the runtime invents, and the
        // one the CLI credential restore also writes into.
        => Assert.Equal(
            new[] { ContainerSpecBuilder.AgentHome + "/.claude" },
            ConversationStorePolicy.MountParentDirectories(
                new[] { ContainerSpecBuilder.AgentHome + "/.claude/projects" }));

    [Fact]
    public void TheStoreItself_IsNeverAParent()
        // The mount target is the daemon-owned store; its ownership is the MG-17 group share and must
        // not be re-owned to the agent. Only the invented directories above it are.
        => Assert.DoesNotContain(
            ContainerSpecBuilder.AgentHome + "/.claude/projects",
            ConversationStorePolicy.MountParentDirectories(
                new[] { ContainerSpecBuilder.AgentHome + "/.claude/projects" }));

    [Fact]
    public void ATopLevelStore_HasNoParentToRepair()
        // $HOME itself is the tmpfs, created with the agent's own uid/gid — never chowned, never listed.
        => Assert.Empty(ConversationStorePolicy.MountParentDirectories(
            new[] { ContainerSpecBuilder.AgentHome + "/.transcripts" }));

    [Fact]
    public void ADeepStore_YieldsEveryIntermediate_ShallowestFirst()
        // Shallowest first so a chown walks down rather than up.
        => Assert.Equal(
            new[]
            {
                ContainerSpecBuilder.AgentHome + "/a",
                ContainerSpecBuilder.AgentHome + "/a/b",
            },
            ConversationStorePolicy.MountParentDirectories(
                new[] { ContainerSpecBuilder.AgentHome + "/a/b/c" }));

    [Fact]
    public void TwoStoresSharingAParent_ListItOnce()
        => Assert.Equal(
            new[] { ContainerSpecBuilder.AgentHome + "/.claude" },
            ConversationStorePolicy.MountParentDirectories(new[]
            {
                ContainerSpecBuilder.AgentHome + "/.claude/projects",
                ContainerSpecBuilder.AgentHome + "/.claude/history",
            }));

    [Fact]
    public void APathOutsideHome_IsIgnoredRatherThanChowned()
        // Nothing outside $HOME is this repair's business, and a chown there would be a real escape.
        => Assert.Empty(ConversationStorePolicy.MountParentDirectories(new[] { "/etc/ssl/private" }));
}

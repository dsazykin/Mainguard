using System;
using System.IO;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// What an agent is CALLED, and where that name is kept.
///
/// <para>The defect these pin: an agent's only labels were its CLI kind and its 32-hex id, so four
/// <c>claude-code</c> sessions rendered four identical rows. Every assertion here is about the rung of
/// the ladder that produces a name — and above all about the floor, because the floor is what the
/// surface fell through before.</para>
/// </summary>
public class AgentDisplayNameTests
{
    private static AgentInfo Info(
        string id = "70b21c1301fe45acbc3dd2aaa71bd908",
        string kind = "claude-code",
        string role = AgentRoles.Managed,
        string title = "",
        string userName = "") =>
        new(id, kind, $"agent/{id}", AgentLifecycleState.Working, "", DateTimeOffset.UtcNow,
            Role: role, Title: title, UserName: userName);

    [Fact]
    public void WithNothingElse_TheNameIsRoleAndShortId_NeverTheIdAndNeverTheCliKind()
    {
        var name = Info().DisplayName;

        Assert.Equal("Worker 70b21c13", name);

        // The two things the rail used to show. Both are why this ladder exists: the kind is identical
        // for every session of that CLI, and the raw id is 32 characters of hex.
        Assert.DoesNotContain("claude-code", name, StringComparison.Ordinal);
        Assert.NotEqual("70b21c1301fe45acbc3dd2aaa71bd908", name);
    }

    [Fact]
    public void TwoAgentsOfTheSameKind_DoNotShareAName()
    {
        // The whole complaint, as an assertion: same CLI, same role, no briefs, no names.
        var first = Info(id: "70b21c1301fe45acbc3dd2aaa71bd908").DisplayName;
        var second = Info(id: "2e8bda86f8ec4d61a2fa662253b9fe70").DisplayName;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TheBrief_OutranksTheDerivedName()
    {
        Assert.Equal("rewrite the diff gutter", Info(title: "rewrite the diff gutter").DisplayName);
    }

    [Fact]
    public void AHumansName_OutranksTheBrief()
    {
        // The ordering that matters most: a worker revising its plan must not silently rename an agent
        // the person has already named.
        var info = Info(title: "rewrite the diff gutter", userName: "gutter work");

        Assert.Equal("gutter work", info.DisplayName);
        Assert.True(info.IsRenamed);
    }

    [Fact]
    public void ACoordinator_IsCalledACoordinator()
    {
        Assert.StartsWith("Coordinator ", Info(role: AgentRoles.Coordinator).DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void AManualSession_IsNotCalledAWorker()
    {
        // "Worker" would claim a place in the coordinator hierarchy that a hand-started agent has not got.
        Assert.StartsWith("Agent ", Info(role: AgentRoles.Manual).DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortId_IsNotPaddedOrTruncatedIntoSomethingElse()
    {
        Assert.Equal("abc", AgentInfo.ShortId("abc"));
        Assert.Equal("abcdefgh", AgentInfo.ShortId("abcdefghijkl"));
    }

    // ---- the store ------------------------------------------------------------------------------

    private static AgentNameStore Store(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "mg-agent-names-" + Guid.NewGuid().ToString("N"));
        return new AgentNameStore(root);
    }

    [Fact]
    public void ANameSurvivesARead_AndIsScopedToItsRepository()
    {
        var store = Store(out var root);
        try
        {
            store.Save("repo-a", "agent-1", "gutter work");

            Assert.Equal("gutter work", store.NameFor("repo-a", "agent-1"));

            // The scope is the point: a name given in one repository must not surface in another.
            Assert.Equal("", store.NameFor("repo-b", "agent-1"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ABlankName_ClearsRatherThanStoringEmptiness()
    {
        var store = Store(out var root);
        try
        {
            store.Save("repo-a", "agent-1", "gutter work");

            Assert.Equal("", store.Save("repo-a", "agent-1", "   "));
            Assert.Equal("", store.NameFor("repo-a", "agent-1"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AMultiLineOrOverlongName_IsStoredAsTheOneLineTheRailCanRender()
    {
        // A pasted paragraph must not either push every row down the rail or be clipped to its first
        // line while the store holds something else.
        var cleaned = AgentNameStore.Clean("  rewrite\nthe   diff\tgutter  ");
        Assert.Equal("rewrite the diff gutter", cleaned);

        var capped = AgentNameStore.Clean(new string('x', AgentNameStore.MaxNameLength + 40));
        Assert.Equal(AgentNameStore.MaxNameLength, capped.Length);
    }

    [Fact]
    public void ACorruptFile_ReadsAsNoNames_RatherThanThrowingIntoTheRail()
    {
        var store = Store(out var root);
        try
        {
            var path = store.FilePathFor("repo-a");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");

            // The cost of a corrupt file is the derived name coming back, never a broken surface.
            Assert.Empty(store.Load("repo-a"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Forget_DropsOneName_AndPrune_DropsTheOnesWhoseAgentsAreGone()
    {
        var store = Store(out var root);
        try
        {
            store.Save("repo-a", "agent-1", "one");
            store.Save("repo-a", "agent-2", "two");
            store.Save("repo-a", "agent-3", "three");

            store.Forget("repo-a", "agent-1");
            Assert.Equal("", store.NameFor("repo-a", "agent-1"));
            Assert.Equal("two", store.NameFor("repo-a", "agent-2"));

            // Agents are per-run, so without this sweep the file only ever grows.
            Assert.Equal(1, store.Prune("repo-a", new[] { "agent-2" }));
            Assert.Equal("two", store.NameFor("repo-a", "agent-2"));
            Assert.Equal("", store.NameFor("repo-a", "agent-3"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels.Agents;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <see cref="DockLayoutPersistence"/> shipped complete — round-trip tested, corruption-tolerant — and
/// with <b>no production caller</b>. <c>AgentWorkspaceViewModel</c> built its dock graph fresh in its
/// constructor every time, so rearranging panes was discarded on every close.
///
/// <para>Two halves were missing, and only one of them was obvious. Saving needed a producer for
/// <c>ToolOrder</c> (nothing could report the current pane order). <b>Restoring needed a consumer</b>:
/// <c>WorkspaceDockFactory.CreateLayout</c> derived the arrangement purely from
/// <see cref="WorkspaceLayoutKind"/> and ignored <c>ToolOrder</c> entirely — so even a correctly saved
/// file would have restored nothing. These tests pin both ends.</para>
/// </summary>
public class DockLayoutRestoreTests : IDisposable
{
    // A real directory on a real filesystem, not the system temp RAM disk's business — same shape the
    // shipped persistence uses, just rooted somewhere disposable.
    private readonly string _dir = Path.Combine(
        Path.GetDirectoryName(typeof(DockLayoutRestoreTests).Assembly.Location)!,
        "dock-layout-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER — <c>CurrentToolOrder</c> did not exist, because nothing could report
    /// what the arrangement was.
    /// </summary>
    [Fact]
    public void Workspace_ReportsItsCurrentPaneOrder()
    {
        using var ws = new AgentWorkspaceViewModel("agent-1");

        Assert.Equal(new[] { "terminal", "diff", "staging" }, ws.CurrentToolOrder());
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER, and this is the one that matters: a saved arrangement must come back
    /// on the next open. Before, <c>CreateLayout</c> ignored <c>ToolOrder</c>, so this returned the
    /// built-in order no matter what was on disk.
    /// </summary>
    [Fact]
    public void SavedPaneOrder_IsRestored_OnTheNextOpen()
    {
        var persistence = new DockLayoutPersistence(_dir);
        persistence.Save("claude-code", new DockLayoutState(
            DockLayoutState.CurrentVersion, WorkspaceLayoutKind.FlightDeck,
            new[] { "staging", "terminal", "diff" }));

        using var ws = new AgentWorkspaceViewModel(
            "agent-1", WorkspaceLayoutKind.FlightDeck,
            persistence: persistence, layoutKey: "claude-code");

        Assert.Equal(new[] { "staging", "terminal", "diff" }, ws.CurrentToolOrder());
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER. The workspace writes its arrangement back, so the next session has
    /// something to restore. Closing is the moment that must not be lost — teardown clears the dock
    /// graph, so a save that ran after it would record an empty arrangement over a good one.
    /// </summary>
    [Fact]
    public void ClosingTheWorkspace_PersistsItsArrangement()
    {
        var persistence = new DockLayoutPersistence(_dir);

        var ws = new AgentWorkspaceViewModel(
            "agent-1", WorkspaceLayoutKind.ConversationDeck,
            persistence: persistence, layoutKey: "claude-code");
        ws.Dispose();

        var saved = persistence.Load("claude-code");
        Assert.Equal(new[] { "terminal", "diff", "staging" }, saved.ToolOrder);
        Assert.Equal(WorkspaceLayoutKind.ConversationDeck, saved.Layout);
    }

    /// <summary>
    /// The live deck preference wins over whatever is on disk. Restoring the persisted LAYOUT KIND too
    /// would make the Flight Deck / Conversation Deck toggle snap back on the next open.
    /// </summary>
    [Fact]
    public void PersistedLayoutKind_DoesNotOverrideTheLivePreference()
    {
        var persistence = new DockLayoutPersistence(_dir);
        persistence.Save("claude-code", new DockLayoutState(
            DockLayoutState.CurrentVersion, WorkspaceLayoutKind.ConversationDeck,
            new[] { "terminal", "diff", "staging" }));

        using var ws = new AgentWorkspaceViewModel(
            "agent-1", WorkspaceLayoutKind.FlightDeck,
            persistence: persistence, layoutKey: "claude-code");

        Assert.Equal(WorkspaceLayoutKind.FlightDeck, ws.LayoutKind);
    }

    /// <summary>
    /// A stale or hand-edited file must never lose a pane. Restore is total: unknown ids are ignored and
    /// unmentioned panes keep their built-in relative order.
    /// </summary>
    [Theory]
    [InlineData("diff")]                             // partial
    [InlineData("diff", "ghost", "terminal")]        // unknown id
    [InlineData("staging", "staging", "staging")]    // duplicates
    public void StaleToolOrder_NeverLosesAPane(params string[] order)
    {
        var persistence = new DockLayoutPersistence(_dir);
        persistence.Save("claude-code", new DockLayoutState(
            DockLayoutState.CurrentVersion, WorkspaceLayoutKind.FlightDeck, order));

        using var ws = new AgentWorkspaceViewModel(
            "agent-1", WorkspaceLayoutKind.FlightDeck,
            persistence: persistence, layoutKey: "claude-code");

        var actual = ws.CurrentToolOrder();
        Assert.Equal(3, actual.Count);
        Assert.Equal(
            new[] { "diff", "staging", "terminal" },
            actual.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>No persistence configured (tests, the render harness) keeps the previous behaviour and
    /// writes nothing at all.</summary>
    [Fact]
    public void WithoutPersistence_NothingIsWritten()
    {
        using (var ws = new AgentWorkspaceViewModel("agent-1"))
        {
            Assert.Equal(new[] { "terminal", "diff", "staging" }, ws.CurrentToolOrder());
        }

        Assert.False(Directory.Exists(_dir));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels.Agents;

/// <summary>The two persisted workspace arrangements (P2-13 / <c>UserPreferences.WorkspaceLayout</c>).</summary>
public enum WorkspaceLayoutKind
{
    /// <summary>Terminal fills the left; agent-diff over staging on the right. The default.</summary>
    FlightDeck,
    /// <summary>Terminal spans the top; agent-diff and staging share the bottom row.</summary>
    ConversationDeck,
}

/// <summary>A dock tool whose body is an arbitrary content object (a pane VM), rendered by the
/// <c>AgentWorkspaceView</c> data template. Keeps the Dock model free of view concerns.</summary>
public sealed partial class WorkspaceTool : Tool
{
    [ObservableProperty] private object? _content;
}

/// <summary>
/// Per-agent Dock.Avalonia workspace (P2-13): Terminal + agent-diff + staging as docked panes,
/// arranged by the persisted <see cref="WorkspaceLayoutKind"/>. Owns the teardown discipline the
/// task exists to enforce — <see cref="Dispose"/> closes every floating dock window (the documented
/// Dock.Avalonia leak) and disposes any disposable pane content. Dock.Avalonia lives in the App
/// only; never in Mainguard.Agents.
/// </summary>
public sealed class AgentWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly WorkspaceDockFactory _factory;
    private readonly WorkspaceTool _terminalTool;
    private readonly WorkspaceTool _diffTool;
    private readonly WorkspaceTool _stagingTool;
    private readonly Services.DockLayoutPersistence? _persistence;
    private readonly string? _layoutKey;
    private bool _disposed;

    /// <summary>The agent currently shown in this workspace host.</summary>
    public string AgentId { get; private set; }

    public WorkspaceLayoutKind LayoutKind { get; }

    /// <summary>The Dock root the <c>DockControl</c> binds to.</summary>
    public IRootDock Layout { get; }

    /// <param name="persistence">Where the pane arrangement is remembered between sessions. Null keeps
    /// the previous behaviour (a fresh default layout every time), which is what tests and the render
    /// harness want.</param>
    /// <param name="layoutKey">The bucket the arrangement is remembered under — the agent KIND, not the
    /// agent id. Supplied by the caller because this VM only knows the id (a per-run hex string), and
    /// keying on that would write one file per agent and restore nothing, ever.</param>
    public AgentWorkspaceViewModel(
        string agentId,
        WorkspaceLayoutKind layout = WorkspaceLayoutKind.FlightDeck,
        object? terminal = null,
        object? diff = null,
        object? staging = null,
        Services.DockLayoutPersistence? persistence = null,
        string? layoutKey = null)
    {
        AgentId = agentId;
        LayoutKind = layout;
        _persistence = persistence;
        _layoutKey = string.IsNullOrWhiteSpace(layoutKey) ? null : layoutKey;

        _terminalTool = new WorkspaceTool { Id = "terminal", Title = "Terminal", Content = terminal ?? "Terminal", CanClose = false };
        _diffTool = new WorkspaceTool { Id = "diff", Title = "Agent diff", Content = diff ?? "Agent diff (read-only)", CanClose = false };
        _stagingTool = new WorkspaceTool { Id = "staging", Title = "Staging", Content = staging ?? "Staging", CanClose = false };

        // The remembered pane order. Only ToolOrder is restored: the LAYOUT KIND is a live user
        // preference (the Flight Deck / Conversation Deck toggle) and must win over anything on disk,
        // or switching decks would silently snap back on the next open.
        var restored = _persistence is not null && _layoutKey is not null
            ? _persistence.Load(_layoutKey, layout).ToolOrder
            : null;

        _factory = new WorkspaceDockFactory(_terminalTool, _diffTool, _stagingTool, layout, restored);
        Layout = _factory.CreateLayout();
        _factory.InitLayout(Layout);

        if (_persistence is not null && _layoutKey is not null)
        {
            // Dock raises these when panes are dragged between docks or reordered. Splitter drags are
            // deliberately not among them — proportions are not part of DockLayoutState.
            _factory.DockableMoved += OnDockableMoved;
            _factory.DockableSwapped += OnDockableSwapped;
            _factory.DockableAdded += OnDockableAdded;
            _factory.DockableRemoved += OnDockableRemoved;
        }
    }

    // Dock declares a distinct EventArgs type per event, so these are four one-line adapters onto the
    // same save. All four matter: a drag between docks is Moved/Swapped, a pane torn into a floating
    // window (or docked back) is Removed/Added.
    private void OnDockableMoved(object? s, Dock.Model.Core.Events.DockableMovedEventArgs e) => SaveArrangement();
    private void OnDockableSwapped(object? s, Dock.Model.Core.Events.DockableSwappedEventArgs e) => SaveArrangement();
    private void OnDockableAdded(object? s, Dock.Model.Core.Events.DockableAddedEventArgs e) => SaveArrangement();
    private void OnDockableRemoved(object? s, Dock.Model.Core.Events.DockableRemovedEventArgs e) => SaveArrangement();

    /// <summary>Writes the current pane order back to disk. Best-effort by construction — the
    /// persistence layer swallows IO failures, because a full disk must never break the workspace.</summary>
    private void SaveArrangement()
    {
        if (_disposed || _persistence is null || _layoutKey is null) return;
        _persistence.Save(_layoutKey, new Services.DockLayoutState(
            Services.DockLayoutState.CurrentVersion, LayoutKind, CurrentToolOrder()));
    }

    /// <summary>The pane ids in their current visual order, depth-first through the dock tree. This is
    /// the piece that did not exist: <c>DockLayoutPersistence</c> round-tripped a <c>ToolOrder</c>
    /// nothing could produce and nothing consumed.</summary>
    internal IReadOnlyList<string> CurrentToolOrder()
    {
        var ids = new List<string>();
        Collect(Layout);
        return ids;

        void Collect(IDockable dockable)
        {
            if (dockable is WorkspaceTool tool)
            {
                if (!string.IsNullOrEmpty(tool.Id)) ids.Add(tool.Id!);
                return;
            }

            if (dockable is IDock dock && dock.VisibleDockables is { } children)
                foreach (var child in children)
                    Collect(child);
        }
    }

    /// <summary>
    /// Point this ONE workspace host at a different agent by swapping the three panes' content —
    /// the layout (and its realized Dock controls) is reused, never rebuilt. This is the lightweight
    /// switching path: opening another agent costs three content swaps, not a fresh dock graph, so
    /// the heap stays flat no matter how many agents you cycle through. Disposes replaced content
    /// that owns resources.
    /// </summary>
    public void ShowAgent(string agentId, object? terminal, object? diff, object? staging)
    {
        AgentId = agentId;
        SwapContent(_terminalTool, terminal ?? "Terminal");
        SwapContent(_diffTool, diff ?? "Agent diff (read-only)");
        SwapContent(_stagingTool, staging ?? "Staging");
    }

    private static void SwapContent(WorkspaceTool tool, object? next)
    {
        if (ReferenceEquals(tool.Content, next)) return;
        (tool.Content as IDisposable)?.Dispose();
        tool.Content = next;
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Last write while the graph is still intact — teardown clears VisibleDockables, so a save
        // after this point would record an empty arrangement over a good one.
        SaveArrangement();
        _disposed = true;

        if (_persistence is not null && _layoutKey is not null)
        {
            _factory.DockableMoved -= OnDockableMoved;
            _factory.DockableSwapped -= OnDockableSwapped;
            _factory.DockableAdded -= OnDockableAdded;
            _factory.DockableRemoved -= OnDockableRemoved;
        }

        // Close floating dock windows FIRST — the documented Dock.Avalonia leak this task owns.
        try
        {
            if (Layout.Windows is { } windows)
                foreach (var w in windows.ToList())
                    try { w.Exit(); } catch { /* best effort teardown */ }
        }
        catch { /* ignore */ }

        try
        {
            foreach (var hostWindow in _factory.HostWindows.ToList())
                try { hostWindow.Exit(); } catch { /* best effort teardown */ }
        }
        catch { /* ignore */ }

        try { _factory.CloseAllDockables(Layout); } catch { /* ignore */ }

        foreach (var tool in new[] { _terminalTool, _diffTool, _stagingTool })
        {
            (tool.Content as IDisposable)?.Dispose();
            tool.Content = null;
        }

        // Break the Dock control registries so the DockControl + its visual tree can be collected
        // (the factory otherwise roots them for the process lifetime — the retained-graph leak).
        TryClear(_factory.DockControls);
        TryClear(_factory.HostWindows);
        TryClearDict(_factory.VisibleDockableControls);
        TryClearDict(_factory.TabDockableControls);
        TryClearDict(_factory.PinnedDockableControls);
        try { Layout.VisibleDockables?.Clear(); } catch { /* ignore */ }
    }

    private static void TryClear<T>(System.Collections.Generic.IList<T>? list)
    {
        try { list?.Clear(); } catch { /* ignore */ }
    }

    private static void TryClearDict<TKey, TValue>(System.Collections.Generic.IDictionary<TKey, TValue>? dict)
    {
        try { dict?.Clear(); } catch { /* ignore */ }
    }
}

/// <summary>Builds the two persisted dock arrangements. Internal to the App.</summary>
internal sealed class WorkspaceDockFactory : Factory
{
    private readonly WorkspaceTool _terminal;
    private readonly WorkspaceTool _diff;
    private readonly WorkspaceTool _staging;
    private readonly WorkspaceLayoutKind _kind;
    private readonly IReadOnlyList<string>? _toolOrder;

    /// <param name="toolOrder">The remembered pane order (<c>DockLayoutState.ToolOrder</c>), or null for
    /// the built-in order. Both decks place ONE pane in the primary slot and the other two in the
    /// secondary pair, so an order is honoured by choosing which pane takes which slot — the shapes are
    /// unchanged. Unknown or missing ids are ignored and the remaining panes keep their default relative
    /// order, so a stale file can never lose a pane.</param>
    public WorkspaceDockFactory(
        WorkspaceTool terminal, WorkspaceTool diff, WorkspaceTool staging, WorkspaceLayoutKind kind,
        IReadOnlyList<string>? toolOrder = null)
    {
        _terminal = terminal;
        _diff = diff;
        _staging = staging;
        _kind = kind;
        _toolOrder = toolOrder;
    }

    /// <summary>The three panes in the order the layout should lay them down: the remembered order where
    /// it names known panes, then any pane the file did not mention, in the built-in order. Total by
    /// construction — every pane appears exactly once whatever the file says.</summary>
    internal IReadOnlyList<WorkspaceTool> OrderedTools()
    {
        var defaults = new[] { _terminal, _diff, _staging };
        if (_toolOrder is not { Count: > 0 }) return defaults;

        var ordered = new List<WorkspaceTool>(3);
        foreach (var id in _toolOrder)
        {
            var match = defaults.FirstOrDefault(
                t => string.Equals(t.Id, id, StringComparison.Ordinal) && !ordered.Contains(t));
            if (match is not null) ordered.Add(match);
        }

        foreach (var tool in defaults)
            if (!ordered.Contains(tool))
                ordered.Add(tool);

        return ordered;
    }

    public override IRootDock CreateLayout()
    {
        var tools = OrderedTools();
        var primaryDock = ToolDockFor(tools[0], "PrimaryDock", 0.55);
        var secondaryDock = ToolDockFor(tools[1], "SecondaryDock", 0.6);
        var tertiaryDock = ToolDockFor(tools[2], "TertiaryDock", 0.4);

        IDock main;
        if (_kind == WorkspaceLayoutKind.ConversationDeck)
        {
            // Primary pane spans the top; the other two share the bottom row.
            var bottomRow = new ProportionalDock
            {
                Orientation = Orientation.Horizontal,
                Proportion = 0.4,
                VisibleDockables = CreateList<IDockable>(secondaryDock, new ProportionalDockSplitter(), tertiaryDock),
            };
            primaryDock.Proportion = 0.6;
            main = new ProportionalDock
            {
                Orientation = Orientation.Vertical,
                VisibleDockables = CreateList<IDockable>(primaryDock, new ProportionalDockSplitter(), bottomRow),
            };
        }
        else
        {
            // Flight Deck (default): primary pane on the left; the other two stacked on the right.
            var rightColumn = new ProportionalDock
            {
                Orientation = Orientation.Vertical,
                Proportion = 0.45,
                VisibleDockables = CreateList<IDockable>(secondaryDock, new ProportionalDockSplitter(), tertiaryDock),
            };
            main = new ProportionalDock
            {
                Orientation = Orientation.Horizontal,
                VisibleDockables = CreateList<IDockable>(primaryDock, new ProportionalDockSplitter(), rightColumn),
            };
        }

        var root = CreateRootDock();
        root.Id = "WorkspaceRoot";
        root.Title = "Workspace";
        root.VisibleDockables = CreateList<IDockable>(main);
        root.ActiveDockable = main;
        root.DefaultDockable = main;
        return root;
    }

    private ToolDock ToolDockFor(WorkspaceTool tool, string id, double proportion) => new()
    {
        Id = id,
        Title = tool.Title,
        Proportion = proportion,
        VisibleDockables = CreateList<IDockable>(tool),
        ActiveDockable = tool,
    };
}

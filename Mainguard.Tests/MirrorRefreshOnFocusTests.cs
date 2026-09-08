using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// F67 — "refresh the mirror when you come back to the window" must be attached to the window that is
/// actually the main window.
///
/// <para><b>The defect.</b> <c>ControlCenterViewModel</c> subscribed to
/// <c>desktop.MainWindow.Activated</c> in its constructor. In the shipped Pro app that constructor runs
/// inside <c>MainWindowViewModel</c>'s, which the startup loader invokes through
/// <c>ProComposition.CreateShellWindow</c> — and at that instant <c>desktop.MainWindow</c> is still the
/// <c>StartupWindow</c>, which is reassigned and closed a few lines later. The handler was therefore
/// attached to the loading screen and the real window got nothing: the on-focus nudge was dead for every
/// user, and only the 60-second timer ever ran. That is exactly why it went unnoticed — the rail was stale
/// for up to a minute rather than permanently.</para>
///
/// <para>These assert the SUBSCRIPTION TARGET rather than driving a real platform activation: the fix is
/// about which window the handler is on, and a headless <c>Activated</c> is the platform's business, not
/// this VM's. The event is raised through its backing field, which is the same delegate the platform would
/// invoke.</para>
/// </summary>
public sealed class MirrorRefreshOnFocusTests
{
    /// <summary>
    /// Activates <paramref name="window"/> through <c>WindowBase.HandleActivated</c> — the internal method
    /// Avalonia's own platform backends call when a window takes focus, so the test drives the real path
    /// rather than a stand-in for it. Reflection only because it is not public; nothing about the
    /// behaviour is faked.
    /// </summary>
    private static void RaiseActivated(Window window)
    {
        var handle = typeof(Avalonia.Controls.WindowBase).GetMethod(
            "HandleActivated", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                "WindowBase.HandleActivated is gone — this test's activation mechanism needs updating.");

        handle.Invoke(window, Array.Empty<object>());
    }

    /// <summary>The window swap: after re-binding, focusing the NEW main window refreshes the mirror.
    /// This is the case that never fired in the shipped app.</summary>
    [AvaloniaFact]
    public void FocusingTheSwappedInMainWindow_RefreshesTheMirror()
    {
        var queue = new RecordingQueue();
        using var vm = NewControlCenter(queue);

        var loader = new Window();     // what desktop.MainWindow is while the VM is constructed
        var shell = new Window();      // what the startup loader swaps in a few lines later

        vm.BindMainWindowActivation(loader);
        vm.BindMainWindowActivation(shell);   // StartupWindow.OnCompletedSwap does this

        RaiseActivated(shell);

        Assert.Equal(1, queue.MirrorRefreshes);
    }

    /// <summary>The other half: the window that is no longer the main window is DETACHED, so a closing
    /// loader cannot keep driving refreshes — and handlers do not stack up across re-binds.</summary>
    [AvaloniaFact]
    public void FocusingTheWindowThatWasReplaced_RefreshesNothing()
    {
        var queue = new RecordingQueue();
        using var vm = NewControlCenter(queue);

        var loader = new Window();
        var shell = new Window();
        vm.BindMainWindowActivation(loader);
        vm.BindMainWindowActivation(shell);

        RaiseActivated(loader);

        Assert.Equal(0, queue.MirrorRefreshes);
    }

    /// <summary>Re-binding the SAME window is a no-op, so a repeated call cannot double the refresh — the
    /// swap point runs unconditionally and the constructor may already have bound the same window.</summary>
    [AvaloniaFact]
    public void ReBindingTheSameWindow_DoesNotStackHandlers()
    {
        var queue = new RecordingQueue();
        using var vm = NewControlCenter(queue);

        var shell = new Window();
        vm.BindMainWindowActivation(shell);
        vm.BindMainWindowActivation(shell);
        vm.BindMainWindowActivation(shell);

        RaiseActivated(shell);

        Assert.Equal(1, queue.MirrorRefreshes);
    }

    /// <summary>Disposal detaches, so a torn-down surface cannot keep nudging the daemon from a window
    /// that outlives it.</summary>
    [AvaloniaFact]
    public void AfterDispose_FocusRefreshesNothing()
    {
        var queue = new RecordingQueue();
        var vm = NewControlCenter(queue);

        var shell = new Window();
        vm.BindMainWindowActivation(shell);
        vm.Dispose();

        RaiseActivated(shell);

        Assert.Equal(0, queue.MirrorRefreshes);
    }

    private static ControlCenterViewModel NewControlCenter(RecordingQueue queue)
    {
        var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        return new ControlCenterViewModel(
            new OrchestratorServices(mock, queue, mock, mock, mock, mock, Owner: null));
    }

    /// <summary>Counts the one call the focus nudge makes.</summary>
    private sealed class RecordingQueue : IMergeQueueService
    {
        public int MirrorRefreshes { get; private set; }

        public Task RefreshMirrorMainAsync()
        {
            MirrorRefreshes++;
            return Task.CompletedTask;
        }

        public IReadOnlyList<QueueEntry> GetQueue() => Array.Empty<QueueEntry>();
        public string MainSha => "";
        public DateTimeOffset? MirrorMainRefreshedAt => null;
        public string? MirrorMainRefreshError => null;
        public bool CanMerge(string agentId, out string reason) { reason = ""; return false; }
        public Task<MergeOutcome> ConfirmMergeAsync(string agentId) => throw new NotSupportedException();
        public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId) => Task.CompletedTask;
        public Task<VerificationOutcome> RunVerificationAsync(string agentId) =>
            Task.FromResult(new VerificationOutcome(false, false, ""));
        public Task<VerificationLog> GetVerificationLogAsync(string agentId) =>
            Task.FromResult(new VerificationLog(false, false, "", "", null, "", false, ""));
        public event Action? Changed { add { } remove { } }
        public Task<QueueEntryDiscardOutcome> DiscardEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();
        public Task<QueueEntryRejectOutcome> RejectEntryAsync(string agentId, string reason) =>
            throw new NotSupportedException();
        public Task<QueueEntryResumeOutcome> ResumeEntryAsync(string agentId, string agentKind) =>
            throw new NotSupportedException();
        public Task ResolveConflictWithAgentAsync(string agentId) => throw new NotSupportedException();
        public Task AbortRebaseAsync(string agentId) => throw new NotSupportedException();
        public Task ClearStalledVerificationAsync(string agentId) => throw new NotSupportedException();
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using Mainguard.Agents.Terminal;
using Mainguard.Agents.UI.Controls;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.Controls;
using Mainguard.App.Shell.Services;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.Controls;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-03 §7 — the ViewModel wiring: keystrokes surfaced by the engine (incl. Ctrl+C → 0x03)
/// reach the daemon input stream, daemon output feeds the engine, and layout resizes are debounced
/// before propagating. The VM only ever touches <see cref="ITerminalView"/> / <see cref="ITerminalGateway"/>
/// (the P2-18 swap seam).
/// </summary>
public sealed class TerminalViewModelTests
{
    [Fact]
    public void InputAvailable_CtrlC_ShouldSend0x03ToDaemon()
    {
        var view = new FakeTerminalView();
        var gateway = new FakeTerminalGateway();
        using var vm = new TerminalViewModel(gateway);
        vm.AttachView(view);

        view.RaiseInput(new byte[] { 0x03 }); // Ctrl+C

        Assert.Single(gateway.Inputs);
        Assert.Equal(new byte[] { 0x03 }, gateway.Inputs[0]);
    }

    [Fact]
    public void OutputReceived_ShouldFeedIntoEngine()
    {
        var view = new FakeTerminalView();
        var gateway = new FakeTerminalGateway();
        using var vm = new TerminalViewModel(gateway);
        vm.AttachView(view);

        gateway.PushOutput(new byte[] { (byte)'h', (byte)'i' });

        Assert.Single(view.Fed);
        Assert.Equal(new byte[] { (byte)'h', (byte)'i' }, view.Fed[0]);
    }

    [Fact]
    public async Task OnUserResize_ShouldDebounce_AndPropagateOnce()
    {
        var view = new FakeTerminalView();
        var gateway = new FakeTerminalGateway();
        using var vm = new TerminalViewModel(gateway, resizeDebounce: TimeSpan.FromMilliseconds(30));
        vm.AttachView(view);

        vm.OnUserResize(80, 24);
        vm.OnUserResize(100, 40); // supersedes the first within the debounce window

        await Task.Delay(200);

        Assert.Single(gateway.Resizes);
        Assert.Equal((100, 40), gateway.Resizes[0]);
        Assert.Equal((100, 40), view.LastResize);
    }

    [Fact]
    public void OutputReceived_BeforeAttachView_ShouldBeBufferedThenFlushed()
    {
        // ISSUES-LOG #21 — a rehydrated coordinator's daemon-replayed scrollback can arrive within
        // milliseconds of AttachAsync, before Avalonia's DataContext-changed pass calls AttachView.
        // A fresh spawn's multi-second CLI-startup delay masks this; restart-resume does not.
        var gateway = new FakeTerminalGateway();
        using var vm = new TerminalViewModel(gateway);

        gateway.PushOutput(new byte[] { (byte)'r', (byte)'e', (byte)'p', (byte)'l', (byte)'a', (byte)'y' });

        var view = new FakeTerminalView();
        vm.AttachView(view);

        Assert.Single(view.Fed);
        Assert.Equal("replay"u8.ToArray(), view.Fed[0]);
    }

    [Fact]
    public void OutputReceived_AfterAttachView_ShouldStillFeedDirectly_NotDoubled()
    {
        var view = new FakeTerminalView();
        var gateway = new FakeTerminalGateway();
        using var vm = new TerminalViewModel(gateway);
        vm.AttachView(view);

        gateway.PushOutput(new byte[] { (byte)'l', (byte)'i', (byte)'v', (byte)'e' });

        Assert.Single(view.Fed);
        Assert.Equal("live"u8.ToArray(), view.Fed[0]);
    }

    [Fact]
    public async Task AttachAsync_ShouldSetAgentId_AndAttachGateway()
    {
        var gateway = new FakeTerminalGateway();
        using var vm = new TerminalViewModel(gateway);

        await vm.AttachAsync("agent-xyz", CancellationToken.None);

        Assert.Equal("agent-xyz", vm.AgentId);
        Assert.Equal("agent-xyz", gateway.AttachedAgentId);
    }

    [Theory]
    [InlineData(Key.C, KeyModifiers.Control, new byte[] { 0x03 })] // Ctrl+C
    [InlineData(Key.D, KeyModifiers.Control, new byte[] { 0x04 })] // Ctrl+D
    [InlineData(Key.Enter, KeyModifiers.None, new byte[] { 0x0D })]
    [InlineData(Key.Up, KeyModifiers.None, new byte[] { 0x1B, (byte)'[', (byte)'A' })]
    [InlineData(Key.Left, KeyModifiers.None, new byte[] { 0x1B, (byte)'[', (byte)'D' })]
    [InlineData(Key.F1, KeyModifiers.None, new byte[] { 0x1B, (byte)'O', (byte)'P' })]
    public void MapKey_ShouldEmitVtBytes(Key key, KeyModifiers modifiers, byte[] expected)
        => Assert.Equal(expected, TerminalControl.MapKey(key, modifiers));

    [Fact]
    public void MapKey_UnhandledKey_ShouldReturnNull()
        => Assert.Null(TerminalControl.MapKey(Key.LeftShift, KeyModifiers.None));

    private sealed class FakeTerminalView : ITerminalView
    {
        public List<byte[]> Fed { get; } = new();
        public (int Cols, int Rows) LastResize { get; private set; }

        public event Action<byte[]>? InputAvailable;

        public void RaiseInput(byte[] data) => InputAvailable?.Invoke(data);

        public void FeedOutput(ReadOnlyMemory<byte> data) => Fed.Add(data.ToArray());

        public void Resize(int cols, int rows) => LastResize = (cols, rows);

        public object GetStateSnapshot() => new object();

        public void RestoreState(object snapshot)
        {
        }

        public int ClearCount { get; private set; }

        public void Clear() => ClearCount++;
    }

    private sealed class FakeTerminalGateway : ITerminalGateway
    {
        public List<byte[]> Inputs { get; } = new();
        public List<(int Cols, int Rows)> Resizes { get; } = new();
        public string? AttachedAgentId { get; private set; }

        public event Action<ReadOnlyMemory<byte>>? OutputReceived;

        public void PushOutput(byte[] data) => OutputReceived?.Invoke(data);

        public Task AttachAsync(string agentId, CancellationToken ct)
        {
            AttachedAgentId = agentId;
            return Task.CompletedTask;
        }

        public Task SendInputAsync(ReadOnlyMemory<byte> data)
        {
            Inputs.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public Task SendResizeAsync(int cols, int rows)
        {
            Resizes.Add((cols, rows));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}

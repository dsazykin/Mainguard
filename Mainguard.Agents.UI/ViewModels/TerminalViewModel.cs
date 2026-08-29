using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.Agents.Terminal;
using Mainguard.Agents.UI.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// Drives one attached terminal: it wires the engine (behind <see cref="ITerminalView"/>) to the
/// daemon stream (behind <see cref="ITerminalGateway"/>) and owns nothing about VT parsing or
/// rendering — that all lives in the engine. Because the VM only ever touches the
/// <see cref="ITerminalView"/> interface, P2-18 swaps the engine with no VM change (invariant 3).
///
/// <para>Keystrokes surfaced by the engine (<see cref="ITerminalView.InputAvailable"/>, incl.
/// Ctrl+C → 0x03) are forwarded to the daemon; daemon output is fed back into the engine; and
/// layout-driven resizes are debounced (~50 ms) before both resizing the engine and notifying the
/// daemon (SIGWINCH).</para>
///
/// <para><b>A keystroke that does not arrive is reported</b> (<see cref="InputDeliveryError"/>,
/// rendered as a banner over the pane). The forward is necessarily fire-and-forget — a key event
/// cannot block the UI thread on a network round-trip — and it used to catch only
/// <see cref="OperationCanceledException"/>, so a gateway write failure became an unobserved task
/// and the character was gone with nothing said (stress S1 / G5). Partially delivering an
/// instruction to a jailed CLI and calling it sent is worse than refusing it.</para>
/// </summary>
public sealed partial class TerminalViewModel : ViewModelBase, IDisposable
{
    private readonly ITerminalGateway _gateway;
    private readonly TimeSpan _resizeDebounce;

    private ITerminalView? _view;
    private CancellationTokenSource? _resizeCts;
    private int _pendingCols;
    private int _pendingRows;
    private bool _disposed;

    // Output can arrive (a rehydrated coordinator's full scrollback replays within milliseconds of
    // AttachAsync) before Avalonia's DataContext-changed binding pass calls AttachView — well before a
    // fresh spawn's multi-second CLI-startup delay would cover the same race. Buffer here instead of
    // dropping it; AttachView flushes it into the real view once one exists.
    private readonly object _pendingOutputLock = new();
    private List<byte[]>? _pendingOutput = new();

    [ObservableProperty]
    private string _agentId = string.Empty;

    /// <summary>True once the PTY has streamed its first output frame — the surface's "the CLI is
    /// actually drawing" signal, used to replace a startup loading animation with the live terminal.</summary>
    [ObservableProperty]
    private bool _hasReceivedOutput;

    /// <summary>Why the last keystroke/paste did not reach the agent, or null when input is landing.
    /// Bound to the pane's banner: the operator typed it, so the operator is who must be told.
    /// Cleared by the next input that does get through.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputDeliveryError))]
    private string? _inputDeliveryError;

    /// <summary>Binding convenience for the banner's visibility.</summary>
    public bool HasInputDeliveryError => !string.IsNullOrEmpty(InputDeliveryError);

    public TerminalViewModel(ITerminalGateway gateway, TimeSpan? resizeDebounce = null)
    {
        _gateway = gateway;
        _resizeDebounce = resizeDebounce ?? TimeSpan.FromMilliseconds(50);
        _gateway.OutputReceived += OnOutputReceived;
    }

    /// <summary>Binds the concrete engine control (the View supplies it — the VM keeps only the interface).</summary>
    public void AttachView(ITerminalView view)
    {
        if (_view is not null)
        {
            _view.InputAvailable -= OnInputAvailable;
        }

        _view = view;
        _view.InputAvailable += OnInputAvailable;

        List<byte[]>? pending;
        lock (_pendingOutputLock)
        {
            pending = _pendingOutput;
            _pendingOutput = null;
        }

        if (pending is not null)
        {
            foreach (var chunk in pending) view.FeedOutput(chunk);
        }
    }

    /// <summary>Opens the daemon attach stream for <paramref name="agentId"/>.</summary>
    public Task AttachAsync(string agentId, CancellationToken ct)
    {
        AgentId = agentId;
        return _gateway.AttachAsync(agentId, ct);
    }

    /// <summary>Called by the engine control when its layout resolves a new (cols, rows) size.</summary>
    public void OnUserResize(int cols, int rows)
    {
        _pendingCols = cols;
        _pendingRows = rows;

        _resizeCts?.Cancel();
        _resizeCts?.Dispose();
        var cts = new CancellationTokenSource();
        _resizeCts = cts;
        _ = DebounceResizeAsync(cts.Token);
    }

    private async Task DebounceResizeAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_resizeDebounce, ct);
        }
        catch (OperationCanceledException)
        {
            return; // a newer resize superseded this one
        }

        _view?.Resize(_pendingCols, _pendingRows);
        try
        {
            await _gateway.SendResizeAsync(_pendingCols, _pendingRows);
        }
        catch (Exception)
        {
            // Deliberately NOT surfaced, and deliberately not left unobserved either. A lost SIGWINCH
            // is self-correcting — the next layout change sends the current size again — and it is
            // not something the operator typed, so a banner would be noise about a frame they never
            // authored. A lost keystroke is the opposite on both counts; see ForwardInputAsync.
        }
    }

    /// <summary>Resets the attached engine to a blank screen. Called when the agent behind this
    /// terminal was deliberately stopped, so the dead replay visibly ends instead of lingering as
    /// if the CLI were still there.</summary>
    public void ClearView() => _view?.Clear();

    private void OnInputAvailable(byte[] data) => _ = ForwardInputAsync(data);

    /// <summary>
    /// Forwards one engine input frame and OBSERVES the outcome. Not awaited by the caller (a key
    /// event must not block the UI thread on a round-trip), which is exactly why every failure has
    /// to be caught here — an escaped exception is a character the operator typed, never delivered,
    /// and never reported.
    ///
    /// <para>No <c>ConfigureAwait(false)</c>, on purpose: raised from the UI thread, so the
    /// continuation resumes there and <see cref="InputDeliveryError"/> is set on the thread
    /// Avalonia's bindings require. In tests there is no context and it resumes on the pool, which
    /// is equally fine because nothing is bound.</para>
    /// </summary>
    private async Task ForwardInputAsync(byte[] data)
    {
        try
        {
            await _gateway.SendInputAsync(data);
            if (InputDeliveryError is not null && !_disposed)
            {
                InputDeliveryError = null; // input is landing again — retire the stale banner
            }
        }
        catch (Exception ex) when (IsTeardown(ex) || _disposed)
        {
            // The pane is going away (detach/dispose/cancel). Nothing was lost from where the
            // operator sits, and a banner on a terminal that is closing helps no one.
        }
        catch (Exception ex)
        {
            InputDeliveryError = ex.Message;
        }
    }

    /// <summary>True for the exceptions that mean "this stream is being torn down", including the
    /// transport's own spelling of a cancel and anything the gateway wrapped around one.</summary>
    private static bool IsTeardown(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException or ObjectDisposedException)
            {
                return true;
            }

            if (e is Grpc.Core.RpcException rpc && rpc.StatusCode == Grpc.Core.StatusCode.Cancelled)
            {
                return true;
            }
        }

        return false;
    }

    private void OnOutputReceived(ReadOnlyMemory<byte> data)
    {
        if (!HasReceivedOutput && data.Length > 0)
        {
            HasReceivedOutput = true;
        }

        if (_view is { } view)
        {
            view.FeedOutput(data);
            return;
        }

        lock (_pendingOutputLock)
        {
            if (_pendingOutput is not null)
            {
                _pendingOutput.Add(data.ToArray());
                return;
            }
        }

        // AttachView raced in and flushed everything queued up to that point between the null-check
        // above and this lock; this chunk arrived after, so it goes straight to the now-live view.
        _view?.FeedOutput(data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resizeCts?.Cancel();
        _resizeCts?.Dispose();
        _gateway.OutputReceived -= OnOutputReceived;
        if (_view is not null)
        {
            _view.InputAvailable -= OnInputAvailable;
        }

        lock (_pendingOutputLock)
        {
            _pendingOutput = null;
        }
    }
}

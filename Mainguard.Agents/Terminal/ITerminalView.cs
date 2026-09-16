using System;

namespace Mainguard.Agents.Terminal;

/// <summary>
/// The engine-agnostic seam between the terminal ViewModel and whatever renders/parses VT bytes.
/// The interim engine (a vendored/minimal Avalonia cell-grid renderer) sits behind this today;
/// P2-18 swaps a server-side libvterm grid engine in behind the same interface <b>without any
/// ViewModel change</b> — which is the entire point of the design, so this interface must never
/// leak a renderer/engine type. State is passed as an opaque <see cref="object"/> for exactly that
/// reason (invariant 3).
/// </summary>
public interface ITerminalView
{
    /// <summary>Feeds raw PTY output bytes into the engine for parsing/rendering.</summary>
    void FeedOutput(ReadOnlyMemory<byte> data);

    /// <summary>Raised when the engine has keystrokes/paste to send toward the PTY.</summary>
    event Action<byte[]>? InputAvailable;

    /// <summary>
    /// Notifies the engine of the session's AUTHORITATIVE size (columns × rows) — the geometry the
    /// PTY is actually running at, as reported by the daemon.
    ///
    /// <para>MG-24: this is deliberately not "the size of the pane". Usually they agree — the pane
    /// asks, the daemon grants — but the ask is a request, not a fact, and three things can make the
    /// answer differ: the daemon clamps dimensions, the replay tail is bytes produced at the size
    /// before the resize, and a second pane on the same session can win the last write. An engine
    /// that sizes itself from its pane instead parses the CLI's cursor-addressed redraws — computed
    /// for the real width — against the wrong one, and they land on the wrong rows and overwrite
    /// each other. Fit the pane by scaling what you draw, never by changing the size you parse
    /// at.</para>
    /// </summary>
    void Resize(int cols, int rows);

    /// <summary>Captures the current screen + scrollback as an opaque snapshot (engine detail).</summary>
    object GetStateSnapshot();

    /// <summary>Restores a snapshot previously produced by <see cref="GetStateSnapshot"/>.</summary>
    void RestoreState(object snapshot);

    /// <summary>Resets the engine to its pristine blank state — screen, scrollback, cursor, modes —
    /// as if freshly constructed. Used when a stopped agent's dead replay should visibly end (the
    /// stream is already gone, so no later frame can repaint the stale content).</summary>
    void Clear();
}

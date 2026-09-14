using Mainguard.Agents.Terminal.Vterm;

namespace Mainguard.Server.Terminal;

/// <summary>The terminal target engine behind the P2-18 <c>TerminalEngine=libvterm|interim</c> flag.</summary>
public enum TerminalEngineKind
{
    /// <summary>
    /// P2-03 raw byte streaming; the client parses (VtScreen). The default until P2-04 signs the libvterm
    /// engine off.
    ///
    /// <para><b>This is the default, so its parser's bounds are the ones that ship.</b> Under this engine
    /// the jail's raw bytes are parsed on the CLIENT'S UI THREAD, and every bound on what one control
    /// sequence may cost lives with that parser rather than here — there is deliberately no number in this
    /// file to drift out of step with it. The two that matter are
    /// <c>Mainguard.Agents.UI/Controls/VtScreen.cs</c>: <c>OscCaptureCap</c> (OSC payload) and
    /// <c>CsiParamCapChars</c> (CSI parameters; added after an audit found CSI accumulating without any
    /// bound at all, so <c>ESC [</c> plus megabytes of digits grew a StringBuilder and then a proportional
    /// <c>Split</c>, on the UI thread). Under <see cref="Libvterm"/> none of that applies — the daemon owns
    /// the grid and its parser is bounded there — which is exactly why the interim path had to be bounded
    /// separately.</para>
    /// </summary>
    Interim = 0,

    /// <summary>P2-18 server-side libvterm: the daemon owns the grid and streams GridUpdate diffs
    /// to grid-capable clients; raw streaming remains for everyone else.</summary>
    Libvterm = 1,
}

/// <summary>
/// The daemon's resolved engine selection, registered as a DI singleton.
/// <see cref="Resolve"/> degrades a libvterm request to interim when the native library cannot
/// load here (Windows local-dev — the libvterm engine is daemon/Linux-only by design), so a
/// misconfigured flag can never take terminals down.
/// </summary>
public sealed record TerminalEngineConfig(TerminalEngineKind Engine)
{
    public static readonly TerminalEngineConfig Interim = new(TerminalEngineKind.Interim);

    /// <summary>Parses the flag value ("libvterm" | "interim", case-insensitive; anything else —
    /// including null — is interim) and applies the native-availability degrade.</summary>
    public static TerminalEngineConfig Resolve(string? flagValue)
    {
        var wantsLibvterm = string.Equals(flagValue, "libvterm", System.StringComparison.OrdinalIgnoreCase);
        return wantsLibvterm && VtermSession.IsSupported
            ? new TerminalEngineConfig(TerminalEngineKind.Libvterm)
            : Interim;
    }
}

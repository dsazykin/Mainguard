using System;

namespace Mainguard.Agents.UI.Controls;

/// <summary>
/// Turns a wheel event's <c>Delta.Y</c> into whole terminal lines, for both engines behind
/// <see cref="ITerminalEngineControl"/>. It exists because "how fast does the wheel scroll" is
/// terminal logic, not rendering: it belongs in a pure type the tests drive directly (the same
/// rule that keeps <see cref="GridSelection"/> and <see cref="GridInputEncoder"/> out of the
/// controls), and because both engines got it wrong in the same way.
///
/// <para>Each engine used to treat a wheel event as a fixed three lines —
/// <c>TerminalControl</c> rounded <c>Delta.Y * 3</c> and then floored the step at one line when it
/// rounded to zero, <c>TerminalGridControl</c> ignored the magnitude entirely and stepped ±3. A
/// discrete notch (<c>Delta.Y = ±1.0</c>) is the case that looks right; a precision wheel or a
/// trackpad, which delivers a stream of small fractional deltas, then paid a whole line — or
/// three — per micro-event and the pane flew past whatever you were reading.</para>
///
/// <para>So the delta is scaled by <see cref="LinesPerNotch"/> and <b>accumulated</b>: the whole
/// lines are returned, the fraction is carried into the next event, and a sub-line movement
/// scrolls nothing at all rather than being rounded up to one. Ten 0.1 trackpad ticks move one
/// line, which is what they add up to. A reversal drops the stale carry so a flick up cannot
/// overshoot on the first tick down, and <see cref="Reset"/> clears it wherever the view snaps
/// back to live.</para>
/// </summary>
internal sealed class WheelScrollAccumulator
{
    /// <summary>Lines a full discrete notch (<c>Delta.Y = 1.0</c>) is worth. One, deliberately:
    /// this is the "slow it down" constant, down from the three both engines used to apply.</summary>
    internal const double LinesPerNotch = 1.0;

    /// <summary>Ceiling on a single event, so one absurd delta (a synthetic event, a driver that
    /// reports pixels) cannot teleport the viewport across the ring.</summary>
    internal const int MaxLinesPerEvent = 10;

    /// <summary>Slack for binary floating point: ten 0.1 deltas sum to 0.9999999999999999, which
    /// would otherwise truncate to zero lines and strand a whole line's worth of movement in the
    /// carry forever. Far below any delta a device reports, so it never grants a line that was not
    /// scrolled.</summary>
    private const double Epsilon = 1e-9;

    private double _carry;

    /// <summary>Whole lines this event is worth — positive up (into scrollback), negative down.
    /// Returns 0 for a movement smaller than a line; the remainder is kept for the next event.</summary>
    internal int Accumulate(double deltaY)
    {
        if (deltaY == 0 || double.IsNaN(deltaY) || double.IsInfinity(deltaY))
        {
            return 0;
        }

        if (_carry != 0 && Math.Sign(deltaY) != Math.Sign(_carry))
        {
            _carry = 0;
        }

        _carry += deltaY * LinesPerNotch;

        var whole = Math.Truncate(_carry + Math.CopySign(Epsilon, _carry));
        if (whole == 0)
        {
            return 0;
        }

        _carry -= whole;
        if (Math.Abs(_carry) < Epsilon)
        {
            _carry = 0;
        }

        return (int)Math.Clamp(whole, -MaxLinesPerEvent, MaxLinesPerEvent);
    }

    /// <summary>Forgets the carried fraction — called wherever the view snaps back to live, so a
    /// half-line left over from an old gesture never lands on the next one.</summary>
    internal void Reset() => _carry = 0;
}

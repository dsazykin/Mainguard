using Mainguard.Agents.UI.Controls;
using Xunit;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// The wheel→lines conversion both terminal engines now share. The behaviour under test is the
/// one the field reported: the terminal scrolled far too fast, because a wheel event was worth a
/// fixed three lines (or, on the interim engine, at least one) no matter how small its delta was —
/// so a trackpad's stream of fractional deltas ran away. Here a notch is one line and fractions
/// add up instead of each being rounded up.
/// </summary>
public sealed class WheelScrollAccumulatorTests
{
    [Fact]
    public void ADiscreteNotch_IsOneLine_EitherDirection()
    {
        var up = new WheelScrollAccumulator();
        Assert.Equal(1, up.Accumulate(1.0));
        Assert.Equal(1, up.Accumulate(1.0));

        var down = new WheelScrollAccumulator();
        Assert.Equal(-1, down.Accumulate(-1.0));
        Assert.Equal(-1, down.Accumulate(-1.0));
    }

    // The regression this change exists for: ten 0.1 trackpad ticks are one line's worth of
    // movement, so they move one line. The old interim engine moved 10 (each tick floored to a
    // line); the old grid engine moved 30 (a flat ±3 per event, delta ignored).
    [Fact]
    public void TenTrackpadMicroTicks_MoveExactlyOneLine()
    {
        var wheel = new WheelScrollAccumulator();

        var total = 0;
        for (var i = 0; i < 10; i++)
        {
            total += wheel.Accumulate(0.1);
        }

        Assert.Equal(1, total);
    }

    [Fact]
    public void ASubLineMovement_ScrollsNothing_AndIsCarried_NotDoubleCounted()
    {
        var wheel = new WheelScrollAccumulator();

        Assert.Equal(0, wheel.Accumulate(0.6));  // 0.6 carried
        Assert.Equal(1, wheel.Accumulate(0.6));  // 1.2 → one line, 0.2 carried
        Assert.Equal(0, wheel.Accumulate(0.6));  // 0.8 carried — the line is not paid twice
        Assert.Equal(1, wheel.Accumulate(0.6));  // 1.4 → one line
    }

    [Fact]
    public void ReversingDirection_DropsTheStaleCarry()
    {
        var wheel = new WheelScrollAccumulator();

        Assert.Equal(0, wheel.Accumulate(0.9)); // 0.9 up, carried

        // Without the reversal rule the -0.2 would land on +0.9 and still read as scrolling UP.
        Assert.Equal(0, wheel.Accumulate(-0.2));
        Assert.Equal(-1, wheel.Accumulate(-0.9));
    }

    [Fact]
    public void Reset_ForgetsTheCarry()
    {
        var wheel = new WheelScrollAccumulator();

        Assert.Equal(0, wheel.Accumulate(0.9));
        wheel.Reset();
        Assert.Equal(0, wheel.Accumulate(0.9)); // a fresh 0.9, not 1.8
        Assert.Equal(1, wheel.Accumulate(0.2));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ADeltaThatIsNoMovement_ScrollsNothing(double delta)
    {
        var wheel = new WheelScrollAccumulator();
        Assert.Equal(0, wheel.Accumulate(delta));

        // …and it did not poison the carry either.
        Assert.Equal(1, wheel.Accumulate(1.0));
    }

    [Fact]
    public void ABigDelta_ScalesLinearly_UpToThePerEventCeiling()
    {
        Assert.Equal(3, new WheelScrollAccumulator().Accumulate(3.0));
        Assert.Equal(-3, new WheelScrollAccumulator().Accumulate(-3.0));

        // A driver reporting pixels, or a synthetic event, cannot teleport the viewport.
        Assert.Equal(WheelScrollAccumulator.MaxLinesPerEvent, new WheelScrollAccumulator().Accumulate(500.0));
        Assert.Equal(-WheelScrollAccumulator.MaxLinesPerEvent, new WheelScrollAccumulator().Accumulate(-500.0));
    }
}

using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Mainguard.UI.Converters;

/// <summary>
/// Multiplies a measured length by the fraction in <c>ConverterParameter</c>, so a pane can be given a
/// share of its host instead of a constant.
///
/// <para><b>Why this exists.</b> A hard-coded <c>MaxHeight</c> on a region that must coexist with another
/// one is a bug that only shows up on someone else's screen: the cap does not move when the window does,
/// so making the window bigger buys the capped region nothing and its content stays cut off at exactly the
/// same pixel. That is the shape of the coordinator plan gate's defect — the human's Approve/Reject row
/// sits at the bottom of every card, so a cap that clips the bottom clips the decision, and it clipped it
/// identically at 1296x759 and at 1700x1050.</para>
///
/// <para><b>Bind it to a length that does not depend on the capped child</b> (a host panel's
/// <c>Bounds.Height</c>, decided by the window), never to something the child's own size feeds back into
/// — otherwise the cap and the measure chase each other.</para>
///
/// <para>A missing/unparsable parameter is treated as 1.0 (no cap change) rather than throwing: a layout
/// converter that throws takes the whole surface down, and the honest degraded answer here is "don't
/// clamp".</para>
/// </summary>
public sealed class FractionConverter : IValueConverter
{
    public static readonly FractionConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double length || double.IsNaN(length) || double.IsInfinity(length))
        {
            // Unknown length: impose no cap at all. Returning 0 would collapse the region that the
            // caller is trying to give room to.
            return double.PositiveInfinity;
        }

        var fraction = parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            _ => 1.0,
        };

        if (fraction <= 0 || double.IsNaN(fraction))
        {
            return double.PositiveInfinity;
        }

        return Math.Max(0, length) * fraction;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("FractionConverter is one-way — it caps a layout, it never writes one back.");
}

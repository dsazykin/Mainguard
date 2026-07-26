using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-14/MG-15: the SemVer precedence primitive both update paths gate on. Every "should this update
/// apply?" decision in the app reduces to <see cref="UpdateVersion.IsUpgrade"/>, so the ordering here
/// is the whole substance of the downgrade guards — a wrong answer in this table is a silent
/// downgrade in production.
/// </summary>
public class UpdateVersionTests
{
    [Theory]
    // ---- plain core ordering ----
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.1.0", "1.0.9", 1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    // Numeric, not lexicographic. Every one of these is the WRONG way round under string comparison,
    // which is exactly what both call sites used to do.
    [InlineData("10.0.0", "9.0.0", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("0.20.1", "0.9.0", 1)]
    [InlineData("2.1.218", "2.1.99", 1)]
    // ---- leniency: short and four-part cores ----
    [InlineData("1", "1.0.0", 0)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1.2.3", "1.2.3.0", 0)]
    [InlineData("1.2.3.4", "1.2.3", 1)]
    // ---- build metadata is NOT part of precedence (SemVer §10) ----
    [InlineData("1.0.0+abc", "1.0.0+def", 0)]
    [InlineData("1.0.0+abc", "1.0.0", 0)]
    [InlineData("1.0.1+abc", "1.0.0+zzz", 1)]
    // ---- prerelease precedence (SemVer §11.3/11.4) ----
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]           // release outranks its prerelease
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1", 1)]      // numeric identifiers compare numerically…
    [InlineData("1.0.0-rc.10", "1.0.0-rc.9", 1)]     // …so rc.10 > rc.9, not "<" as text
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]    // alphanumerics compare ordinally
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha", 1)]  // more identifiers wins a shared prefix
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)] // numeric ranks below alphanumeric
    [InlineData("1.0.0-rc.1+build9", "1.0.0-rc.1+build1", 0)] // metadata ignored on prereleases too
    public void Compare_FollowsSemVerPrecedence(string left, string right, int expected)
    {
        Assert.Equal(expected, UpdateVersion.TryCompare(left, right));
        Assert.Equal(-expected, UpdateVersion.TryCompare(right, left)); // antisymmetric
        Assert.Equal(expected > 0, UpdateVersion.IsUpgrade(left, right));
        Assert.Equal(expected < 0, UpdateVersion.IsDowngrade(left, right));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("^1.2.3")]
    [InlineData("~1.2.3")]
    [InlineData(">=1.2.3")]
    [InlineData("1.x")]
    [InlineData("1.2.3.4.5")]     // more components than any real version
    [InlineData("1.-2.3")]        // a signed component is not a version component
    [InlineData("1.+2.3")]
    [InlineData("1. 2.3")]        // whitespace inside is not a version
    [InlineData("v1.2.3")]        // a 'v' prefix is not part of SemVer
    [InlineData("1.0.0-")]        // a prerelease marker with no identifiers
    [InlineData("1.0.0-a..b")]    // an empty prerelease identifier
    [InlineData("1.0.0-a_b")]     // '_' is not a legal prerelease character
    public void Parse_RefusesAnythingItCannotOrder(string? raw)
    {
        Assert.False(UpdateVersion.TryParse(raw, out _));

        // An unparseable version must never be reported as equal, an upgrade, or a downgrade: every
        // caller reads "null" as "cannot establish that this moves forward" and refuses. Answering
        // 0 here would make a crafted version string a way back to the old, direction-free behaviour.
        Assert.Null(UpdateVersion.TryCompare(raw, "1.0.0"));
        Assert.Null(UpdateVersion.TryCompare("1.0.0", raw));
        Assert.False(UpdateVersion.IsUpgrade(raw, "1.0.0"));
        Assert.False(UpdateVersion.IsDowngrade(raw, "1.0.0"));
        Assert.False(UpdateVersion.IsUpgrade("1.0.0", raw));
        Assert.False(UpdateVersion.IsDowngrade("1.0.0", raw));
    }

    [Fact]
    public void Parse_AcceptsTheShapesTheShippedManifestAndAssembliesActuallyUse()
    {
        // The pinned CLI versions in adapters.starter.json…
        Assert.True(UpdateVersion.TryParse("2.1.218", out _));
        Assert.True(UpdateVersion.TryParse("0.145.0", out _));
        Assert.True(UpdateVersion.TryParse("1.18.4", out _));
        // …and the AssemblyInformationalVersion shape the daemon skew check compares.
        Assert.True(UpdateVersion.TryParse("0.2.0+2f791ad", out _));
        Assert.True(UpdateVersion.TryParse("1.0.0-preview.3+2f791ad", out _));
    }

    [Fact]
    public void EqualityAndOperators_AgreeWithCompare()
    {
        Assert.True(UpdateVersion.TryParse("1.2.3+aaa", out var a));
        Assert.True(UpdateVersion.TryParse("1.2.3+bbb", out var b));
        Assert.True(UpdateVersion.TryParse("1.2.4", out var newer));

        Assert.True(a == b);                       // build metadata is not identity for precedence
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(newer > a);
        Assert.True(a < newer);
        Assert.True(a <= b && a >= b);
        Assert.False(a.IsPrerelease);

        Assert.True(UpdateVersion.TryParse("1.2.3-rc.1", out var rc));
        Assert.True(rc.IsPrerelease);
        Assert.True(rc < a);
    }
}

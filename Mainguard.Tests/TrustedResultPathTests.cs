using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// <b>Audit F58 — the elevated helper's <c>--result</c> path is validated, like <c>--resume-target</c>
/// already was.</b>
///
/// <para>Both arguments arrive on the same argv across the same UAC boundary; only one was checked. The
/// unchecked one is OPENED FOR WRITING while elevated, with content that echoes the caller's own
/// <c>--resume-target</c> string back into an error field — so a same-user process that got the user
/// through the consent dialog had an administrator-level create-or-overwrite at any path, with partially
/// chosen content. These cases are the rules stated as facts.</para>
///
/// <para>The data root is injected rather than read from the machine so every case runs identically on
/// Linux CI, a Mac and Windows — the same reason <see cref="TrustedExecutablePath"/> is a pure syntactic
/// gate.</para>
/// </summary>
public class TrustedResultPathTests
{
    private const string WindowsRoot = @"C:\Users\dev\AppData\Local\Mainguard";
    private const string PosixRoot = "/home/dev/.mainguard";

    [Theory]
    [InlineData(@"C:\Users\dev\AppData\Local\Mainguard\elevated-result.json")]
    [InlineData(@"C:\Users\dev\AppData\Local\Mainguard\sub\elevated-result.json")]
    public void TheRealResultPath_IsAccepted(string candidate)
    {
        Assert.True(TrustedResultPath.TryValidate(candidate, WindowsRoot, out var canonical, out var refusal),
            refusal);
        Assert.Equal(candidate, canonical);
    }

    [Fact]
    public void ADifferentUsersDataRoot_IsAccepted_BecauseElevationMayRunAsAnotherAccount()
    {
        // Over-the-shoulder elevation: the helper's OWN %LocalAppData% is not the one the unelevated
        // OOBE reads the result back from. Requiring containment under this process's root would break
        // that install shape and buy nothing — the file NAME rule below is what removes the primitive.
        Assert.True(TrustedResultPath.TryValidate(
            @"C:\Users\someone-else\AppData\Local\Mainguard\elevated-result.json",
            WindowsRoot, out _, out var refusal), refusal);

        Assert.True(TrustedResultPath.TryValidate(
            "/home/other/.mainguard/elevated-result.json", PosixRoot, out _, out refusal), refusal);
    }

    [Theory]
    // The escalation the finding is about: an arbitrary absolute path as administrator.
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", "does not name the helper's result file")]
    [InlineData(@"C:\Windows\System32\elevated-result.json", "not inside a Mainguard data root")]
    [InlineData("/etc/systemd/system/elevated-result.json", "not inside a Mainguard data root")]
    // Right directory, wrong file: the helper writes exactly one name and needs no latitude.
    [InlineData(@"C:\Users\dev\AppData\Local\Mainguard\Mainguard.exe", "does not name the helper's result file")]
    [InlineData(@"C:\Users\dev\AppData\Local\Mainguard\daemon.token", "does not name the helper's result file")]
    // Traversal out of a legitimate-looking root.
    [InlineData(@"C:\Users\dev\AppData\Local\Mainguard\..\..\..\..\Windows\elevated-result.json", "not a usable absolute file path")]
    // Not absolute — resolved against a working directory the caller may control.
    [InlineData(@"elevated-result.json", "not a usable absolute file path")]
    [InlineData(@"..\elevated-result.json", "not a usable absolute file path")]
    // UNC and device forms reach storage we do not control and dodge containment entirely.
    [InlineData(@"\\attacker\share\elevated-result.json", "not a usable absolute file path")]
    [InlineData(@"\\?\C:\Windows\elevated-result.json", "not a usable absolute file path")]
    // An NTFS alternate data stream: a second stream on a file whose visible content looks untouched.
    [InlineData(@"C:\Users\dev\AppData\Local\Mainguard\elevated-result.json:payload", "not a usable absolute file path")]
    // A directory, not a file.
    [InlineData(@"C:\Users\dev\AppData\Local\Mainguard\", "does not name the helper's result file")]
    public void EverythingElse_IsRefused(string candidate, string expectedInRefusal)
    {
        Assert.False(TrustedResultPath.TryValidate(candidate, WindowsRoot, out var canonical, out var refusal));
        Assert.Equal(string.Empty, canonical);
        Assert.Contains(expectedInRefusal, refusal);
    }

    [Fact]
    public void NullAndEmpty_AreRefused_NotTreatedAsAbsent()
    {
        Assert.False(TrustedResultPath.TryValidate(null, WindowsRoot, out _, out _));
        Assert.False(TrustedResultPath.TryValidate("", WindowsRoot, out _, out _));
        Assert.False(TrustedResultPath.TryValidate("   ", WindowsRoot, out _, out _));
    }

    [Fact]
    public void QuoteAndWildcardMetacharacters_AreRefused()
    {
        // The same rule TrustedExecutablePath enforces, reused rather than reimplemented: a quote
        // escapes the argument strings schtasks and runas are built from, and a wildcard makes one
        // registration match many files.
        Assert.False(TrustedResultPath.TryValidate(
            "C:\\Users\\dev\\AppData\\Local\\Mainguard\\a\"b\\elevated-result.json",
            WindowsRoot, out _, out _));
        Assert.False(TrustedResultPath.TryValidate(
            @"C:\Users\dev\AppData\Local\Mainguard\*\elevated-result.json", WindowsRoot, out _, out _));
    }

    [Fact]
    public void TheThrowingForm_NamesTheRefusal()
    {
        var ex = Assert.Throws<System.ArgumentException>(
            () => TrustedResultPath.Require(@"C:\Windows\System32\elevated-result.json", WindowsRoot));
        Assert.Contains("not inside a Mainguard data root", ex.Message);
    }
}

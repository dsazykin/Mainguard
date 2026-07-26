using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// MG-9: the gate every executable path crosses before it reaches <c>schtasks /RL HIGHEST</c> or a
/// <c>runas</c> launch. These rules are deliberately syntactic and platform-independent so the WINDOWS
/// cases — the ones that actually matter, since both facilities are Windows-only — are exercised on the
/// Linux CI leg too. A rule that only runs on a developer's Windows box is a rule that rots.
///
/// <para>Read the negative cases as "an ARBITRARY executable is refused". None of this detects a
/// REPLACED executable at the legitimate path; see <see cref="TrustedExecutablePath"/>.</para>
/// </summary>
public class TrustedExecutablePathTests
{
    private const string Root = @"C:\Program Files\Mainguard";

    [Theory]
    [InlineData(@"C:\Program Files\Mainguard\Mainguard.Pro.App.exe")]
    [InlineData(@"C:\Program Files\Mainguard\sub\Mainguard.Installer.Elevated.exe")]
    // A forward slash is a legal Windows separator and normalises to the canonical backslash form.
    [InlineData("C:/Program Files/Mainguard/Mainguard.Pro.App.exe")]
    // The drive letter is case-insensitive, as Windows treats it.
    [InlineData(@"c:\Program Files\Mainguard\Mainguard.Pro.App.exe")]
    public void Accepts_ExecutablesInsideTheInstallRoot(string candidate)
    {
        Assert.True(TrustedExecutablePath.TryValidate(candidate, Root, out var canonical, out var refusal));
        Assert.Equal(string.Empty, refusal);
        Assert.StartsWith(@"C:\Program Files\Mainguard\", canonical);
        Assert.DoesNotContain('/', canonical); // one canonical spelling, always
    }

    [Theory]
    // ---- outside the install root: the whole "arbitrary exe" class ----
    [InlineData(@"C:\Users\victim\Downloads\evil.exe", "outside this installation")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "outside this installation")]
    // A sibling directory sharing a name prefix must NOT pass — a naive StartsWith would let it.
    [InlineData(@"C:\Program Files\Mainguard-evil\x.exe", "outside this installation")]
    // A different drive is a different volume entirely.
    [InlineData(@"D:\Program Files\Mainguard\x.exe", "outside this installation")]
    // ---- traversal ----
    [InlineData(@"C:\Program Files\Mainguard\..\..\Windows\System32\cmd.exe", "traversal")]
    [InlineData(@"C:\Program Files\Mainguard\.\x.exe", "traversal")]
    // ---- not fully qualified: resolved against a working directory an attacker may control ----
    [InlineData("evil.exe", "not an absolute path")]
    [InlineData(@"..\evil.exe", "not an absolute path")]
    [InlineData(@"Mainguard\x.exe", "not an absolute path")]
    // ---- UNC / device paths: storage we do not control ----
    [InlineData(@"\\attacker\share\evil.exe", "not an absolute path")]
    [InlineData(@"\\?\C:\Program Files\Mainguard\x.exe", "not an absolute path")]
    // ---- NTFS alternate data stream: executes, while the visible file looks untouched ----
    [InlineData(@"C:\Program Files\Mainguard\app.exe:evil.exe", "alternate data stream")]
    // ---- argument-quoting break-out of the schtasks /TR and runas argument strings ----
    [InlineData("C:\\Program Files\\Mainguard\\x.exe\" & calc.exe & \"", "break the quoted argument")]
    [InlineData(@"C:\Program Files\Mainguard\*.exe", "break the quoted argument")]
    [InlineData(@"C:\Program Files\Mainguard\x.exe|calc", "break the quoted argument")]
    // ---- Windows silently strips a trailing dot/space, so one file would have several spellings ----
    [InlineData(@"C:\Program Files\Mainguard\x.exe.", "silently strips")]
    [InlineData(@"C:\Program Files\Mainguard\x.exe ", "silently strips")]
    [InlineData(@"C:\Program Files\Mainguard\ x.exe", "silently strips")]
    // ---- the root itself is a directory, not a program ----
    [InlineData(@"C:\Program Files\Mainguard", "outside this installation")]
    [InlineData(@"C:\Program Files\Mainguard\", "outside this installation")]
    // ---- doubled separators ----
    [InlineData(@"C:\Program Files\Mainguard\\x.exe", "empty path segment")]
    public void Refuses_EverythingThatIsNotAPlainExecutableInsideTheRoot(string candidate, string expectedReason)
    {
        Assert.False(TrustedExecutablePath.TryValidate(candidate, Root, out var canonical, out var refusal));
        Assert.Equal(string.Empty, canonical);
        Assert.Contains(expectedReason, refusal);
    }

    [Fact]
    public void Refuses_NullAndEmptyCandidates()
    {
        Assert.False(TrustedExecutablePath.TryValidate(null, Root, out _, out _));
        Assert.False(TrustedExecutablePath.TryValidate("", Root, out _, out _));
        Assert.False(TrustedExecutablePath.TryValidate("   ", Root, out _, out _));
    }

    [Fact]
    public void Refuses_WhenTheInstallRootItselfIsUnusable()
    {
        // Nothing can be checked "against" a relative or absent root, so nothing is accepted against one.
        Assert.False(TrustedExecutablePath.TryValidate(@"C:\a\x.exe", null, out _, out var r1));
        Assert.Contains("not an absolute path", r1);
        Assert.False(TrustedExecutablePath.TryValidate(@"C:\a\x.exe", "relative\\root", out _, out var r2));
        Assert.Contains("not an absolute path", r2);
    }

    [Fact]
    public void Refuses_MixingPathStyles()
    {
        // A POSIX candidate against a Windows root (or vice versa) is nonsense, and silently coercing
        // one to the other is how a containment check gets talked around.
        Assert.False(TrustedExecutablePath.TryValidate("/usr/bin/evil", Root, out _, out var refusal));
        Assert.Contains("not the same kind of path", refusal);
    }

    [Theory]
    [InlineData("/opt/mainguard/mainguardd", "/opt/mainguard", true)]
    [InlineData("/opt/mainguard/bin/mainguardd", "/opt/mainguard", true)]
    [InlineData("/opt/mainguard-evil/x", "/opt/mainguard", false)]
    [InlineData("/opt/mainguard/../etc/passwd", "/opt/mainguard", false)]
    [InlineData("/opt/mainguard", "/opt/mainguard", false)]
    public void Handles_PosixRootsToo(string candidate, string root, bool accepted)
        => Assert.Equal(accepted, TrustedExecutablePath.TryValidate(candidate, root, out _, out _));

    [Fact]
    public void Require_ThrowsWithAnActionableMessage()
    {
        var ex = Assert.Throws<System.ArgumentException>(
            () => TrustedExecutablePath.Require(@"C:\Users\victim\Downloads\evil.exe", Root, "resume target"));
        Assert.Contains("resume target", ex.Message);
        Assert.Contains("outside this installation", ex.Message);

        // …and returns the canonical form on the happy path.
        Assert.Equal(
            @"C:\Program Files\Mainguard\x.exe",
            TrustedExecutablePath.Require("C:/Program Files/Mainguard/x.exe", Root, "resume target"));
    }

    [Fact]
    public void DirectoryOf_UsesOurOwnNormalisation_NotTheHostsPathRules()
    {
        // Path.GetDirectoryName answers "" for this perfectly valid Windows path when running on Linux,
        // which would turn every containment check on the CI leg into a check against nothing.
        Assert.Equal(
            @"C:\Program Files\Mainguard",
            TrustedExecutablePath.DirectoryOf(@"C:\Program Files\Mainguard\Mainguard.Pro.App.exe"));
        Assert.Equal("/opt/mainguard", TrustedExecutablePath.DirectoryOf("/opt/mainguard/mainguardd"));
        Assert.Null(TrustedExecutablePath.DirectoryOf("relative.exe"));
        Assert.Null(TrustedExecutablePath.DirectoryOf(null));
    }

    [Fact]
    public void IsSameExecutable_ComparesCanonicalForms_NotRawStrings()
    {
        const string target = @"C:\Program Files\Mainguard\Mainguard.Pro.App.exe";

        // Same file, different spellings — the old raw string compare said "different" and deleted a
        // legitimate registration; worse, the same weakness let a foreign one be spelled past it.
        Assert.True(TrustedExecutablePath.IsSameExecutable(
            "C:/Program Files/Mainguard/Mainguard.Pro.App.exe", target, Root));
        Assert.True(TrustedExecutablePath.IsSameExecutable(
            @"C:\PROGRAM FILES\MAINGUARD\MAINGUARD.PRO.APP.EXE", target, Root));

        // Different file → not the same, and a traversal that RESOLVES to the same file is still
        // refused rather than resolved (both sides must be valid before they are compared at all).
        Assert.False(TrustedExecutablePath.IsSameExecutable(
            @"C:\Program Files\Mainguard\Other.exe", target, Root));
        Assert.False(TrustedExecutablePath.IsSameExecutable(
            @"C:\Program Files\Mainguard\sub\..\Mainguard.Pro.App.exe", target, Root));
        Assert.False(TrustedExecutablePath.IsSameExecutable(
            @"C:\Users\victim\Downloads\Mainguard.Pro.App.exe", target, Root));
        Assert.False(TrustedExecutablePath.IsSameExecutable(null, target, Root));
    }
}

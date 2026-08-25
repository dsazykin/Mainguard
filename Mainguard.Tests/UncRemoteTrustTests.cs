using System;
using System.Collections.Generic;
using System.IO;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Pins the git-config mechanics behind the Windows/WSL2 "dubious ownership" fix (walkthrough W4).
///
/// <para><b>What these can and cannot cover.</b> The failure itself needs a real UNC path into a real
/// WSL2 VM whose files carry a foreign owner SID — not reproducible in a portable unit test, and not
/// reproducible on the Linux CI agents at all. What IS portable, and what actually broke, is the
/// <i>string</i> half: the trust entry has to survive a round-trip through a git config FILE and come
/// back byte-identical to the path git will compare against. The original manual workaround failed for
/// exactly that reason — the value reached <c>.gitconfig</c> under-escaped and read back with one
/// leading backslash instead of two, so it never matched while looking identical to the naked eye.
/// These tests drive real <c>git</c> and assert the round-trip, so that specific regression cannot
/// return silently. The end-to-end fetch was verified by hand against the live MainguardEnv mirror.</para>
/// </summary>
public sealed class UncRemoteTrustTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    private string NewDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "mainguard-unctrust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _dirs.Add(path);
        return path;
    }

    private const string Unc =
        @"\\wsl.localhost\MainguardEnv\home\mainguard\mainguard\repos\77f80fe89b950af89787344789bcd19ef5c3108036bd7bd52108d4e0e06a2c1d.git";

    /// <summary>
    /// THE regression. A UNC path written into a config file and read back by real git must be the
    /// SAME string — git compares <c>safe.directory</c> against the repository path with a plain
    /// string compare, so a single dropped backslash silently disarms the whole exception.
    /// </summary>
    [Fact]
    public void TrustedPath_SurvivesTheConfigFileRoundTrip_ByteIdentical()
    {
        var dir = NewDir();
        var shim = Path.Combine(dir, "shim.gitconfig");
        File.WriteAllText(shim, UncRemoteTrust.BuildShimContent(null, Unc));

        var (code, output, err) = GitService.RunGit(
            dir, new Dictionary<string, string> { ["GIT_CONFIG_SYSTEM"] = shim }, default,
            "config", "--system", "--get", "safe.directory");

        Assert.True(code == 0, $"git could not read the generated shim: {err}");
        Assert.Equal(Unc, output.Trim());
        Assert.StartsWith(@"\\", output.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The escaping is not decorative: an unescaped value does NOT come back as itself. This pins the
    /// actual mechanism that broke the hand-written workaround, so nobody "simplifies"
    /// <see cref="UncRemoteTrust.EscapeConfigValue"/> away.
    /// </summary>
    [Fact]
    public void AnUnescapedTrustedPath_DoesNotRoundTrip()
    {
        var dir = NewDir();
        var shim = Path.Combine(dir, "shim.gitconfig");
        File.WriteAllText(shim, "[safe]\n\tdirectory = " + Unc + "\n");

        var (code, output, _) = GitService.RunGit(
            dir, new Dictionary<string, string> { ["GIT_CONFIG_SYSTEM"] = shim }, default,
            "config", "--system", "--get", "safe.directory");

        // Either git rejects the value outright or it returns a mangled one; what it must never do is
        // hand back the path unchanged, which is what the naive spelling looks like it will do.
        Assert.False(code == 0 && output.Trim() == Unc,
            "an unescaped UNC path round-tripped — the escaping this fix depends on is no longer needed, "
            + "or the test no longer exercises it");
    }

    /// <summary>The shim must ADD trust, never cost the user their real system configuration.</summary>
    [Fact]
    public void Shim_IncludesTheRealSystemConfig_SoNothingConfiguredIsLost()
    {
        var dir = NewDir();
        var real = Path.Combine(dir, "real.gitconfig");
        File.WriteAllText(real, "[mainguard]\n\tprobe = preserved\n");

        var shim = Path.Combine(dir, "shim.gitconfig");
        File.WriteAllText(shim, UncRemoteTrust.BuildShimContent(real, Unc));

        var env = new Dictionary<string, string> { ["GIT_CONFIG_SYSTEM"] = shim };

        // mainguard.probe only reaches the shim through its [include], and --system --get does NOT
        // follow includes (measured: it reads only the target file's own directly-defined entries) —
        // so this one needs the ordinary merged resolution, unscoped.
        var (probeCode, probe, _) = GitService.RunGit(dir, env, default, "config", "--get", "mainguard.probe");
        Assert.Equal(0, probeCode);
        Assert.Equal("preserved", probe.Trim());

        // safe.directory IS defined directly in the shim, so --system (as the sibling round-trip test
        // above uses) is both correct and necessary here: an unscoped --get resolves across every
        // config scope, and a CI runner's own global safe.directory=* (actions/checkout trusts the
        // whole workspace that way) outranks GIT_CONFIG_SYSTEM and would mask the shim's value with
        // the runner's, not this fix's.
        var (trustCode, trust, _) = GitService.RunGit(dir, env, default, "config", "--system", "--get", "safe.directory");
        Assert.Equal(0, trustCode);
        Assert.Equal(Unc, trust.Trim());
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\MainguardEnv\home\mainguard\repos\a.git", @"\\wsl.localhost\MainguardEnv\home\mainguard\repos\a.git")]
    [InlineData(@"\\wsl.localhost\MainguardEnv\repos\a.git\", @"\\wsl.localhost\MainguardEnv\repos\a.git")]
    [InlineData("  \\\\wsl.localhost\\Env\\a.git  ", @"\\wsl.localhost\Env\a.git")]
    public void NormalizeUncPath_KeepsTheLiteralWindowsSpelling(string input, string expected)
        => Assert.Equal(expected, UncRemoteTrust.NormalizeUncPath(input));

    [Theory]
    [InlineData("/home/mainguard/repos/a.git")]                 // the macOS substrate's local mirror
    [InlineData("https://github.com/dsazykin/Mainguard.git")]   // an ordinary host remote
    [InlineData(@"C:\Users\yikes\code\Mainguard")]              // a plain Windows path
    [InlineData(@"\\wsl.localhost")]                            // a host, not a repository
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeUncPath_IgnoresEverythingThatIsNotAUncRepository(string? input)
        => Assert.Null(UncRemoteTrust.NormalizeUncPath(input));

    /// <summary>
    /// Forward slashes are NOT interchangeable here. Measured against git 2.45.1.windows.1:
    /// <c>//wsl.localhost/...</c> does not match a repository git names as <c>\\wsl.localhost\...</c>.
    /// The helper must therefore never "tidy" the separators.
    /// </summary>
    [Fact]
    public void EscapeConfigValue_DoublesBackslashes_AndNeverConvertsThemToForwardSlashes()
    {
        var escaped = UncRemoteTrust.EscapeConfigValue(@"\\host\share\x.git");
        Assert.Equal(@"\\\\host\\share\\x.git", escaped);
        Assert.DoesNotContain('/', escaped);
    }
}

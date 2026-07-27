using System;
using System.IO;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that <b>skips</b> (visibly) when git-lfs is not available in this
/// environment, instead of the old <c>throw Xunit.Sdk.SkipException.ForSkip(...)</c> pattern used by
/// the tests in <c>GitServiceLfsTests</c>.
///
/// <para><c>SkipException.ForSkip</c> / dynamic skipping is an xUnit v3 feature. This project pins
/// <c>xunit</c> 2.9.3 (v2 core) with <c>xunit.runner.visualstudio</c> 3.1.4 — the v2 core does not
/// interpret the marker, so throwing <c>SkipException</c> reported these tests as <b>Failed</b>, with
/// the raw <c>$XunitDynamicSkip$…</c> sentinel leaking into the failure message, on every machine
/// without git-lfs installed. Nine permanently-failing tests trains everyone to ignore a red suite,
/// which is exactly how a real failure gets waved through. Setting <see cref="FactAttribute.Skip"/> in
/// the constructor is honest: the runner reports a genuine Skipped, and nobody mistakes red for
/// nothing. Mirrors <see cref="Terminal.RequiresLibvtermFactAttribute"/>.</para>
///
/// <para>A <see cref="FactAttribute"/> is constructed at discovery time — no test-class instance and
/// no fixture exist yet — so it cannot call the instance method
/// <see cref="Mainguard.Git.Services.LfsService.IsAvailable"/>, which needs a repo path. Instead
/// <see cref="GitLfsAvailability"/> probes the exact same real condition
/// (<c>git lfs version</c> exit code) directly, fixture-free, and caches the result for the run.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresGitLfsFactAttribute : FactAttribute
{
    public RequiresGitLfsFactAttribute()
    {
        if (!GitLfsAvailability.IsSupported)
        {
            Skip = GitLfsAvailability.SkipReason;
        }
    }
}

/// <summary>
/// Fixture-free git-lfs availability probe for <see cref="RequiresGitLfsFactAttribute"/>.
///
/// <para>Runs the identical check <see cref="Mainguard.Git.Services.LfsService.IsAvailable"/> makes
/// (<c>git lfs version</c> — success means the CLI understands the <c>lfs</c> subcommand) once per
/// test run and caches the result in a <see cref="Lazy{T}"/>. <c>LfsService.IsAvailable</c> takes a
/// repo path only because <c>ProcessStartInfo</c> requires a working directory, not because git-lfs
/// needs an actual repository to answer <c>version</c> — so a scratch temp directory stands in for the
/// per-repo path without weakening the check.</para>
/// </summary>
internal static class GitLfsAvailability
{
    private static readonly Lazy<bool> LazyIsSupported = new(Probe);

    internal static bool IsSupported => LazyIsSupported.Value;

    internal const string SkipReason =
        "git-lfs is not available in this environment (`git lfs version` failed) — install git-lfs " +
        "alongside git so the LFS-gated tests can actually run.";

    private static bool Probe()
    {
        var (code, _, _) = GitService.RunGit(Path.GetTempPath(), "lfs", "version");
        return code == 0;
    }
}

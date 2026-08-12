using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Mainguard.Git.Review;
using Xunit;
using Xunit.Abstractions;

namespace Mainguard.Tests;

/// <summary>
/// What the P2-11 §3.6 lockfile review costs, measured — because it runs on the <b>merge path</b>
/// (<c>MergeQueueProvisioner.ArmFlaggedChangeReview</c>, at every verification), and "it is probably fine"
/// is not a budget.
///
/// <para><b>Measured</b> on the development machine (WSL2, .NET 10, <i>Debug</i>): a <b>5,000-package
/// package-lock.json</b> — 556 KB per side, roughly a large real-world lock — parses BOTH sides and diffs
/// them in <b>~75 ms</b>. That sits against a verification which is already spawning a test suite inside a
/// container, so the review is not a term in that sum. The read that precedes it is two <c>git show</c>s
/// against a local mirror, and it happens only for paths <see cref="LockfileReview.KindFor"/> recognises —
/// a branch touching no manifest pays nothing at all.</para>
///
/// <para><b>The assertion is a shape check, not the measurement.</b> Wall-clock bounds are the classic
/// flaky test, so the bound here is ~40x the measured figure: it cannot fail on a slow CI box, and it
/// cannot pass if someone turns the name join into an O(n²) scan (which at this size would take minutes,
/// on the merge path, holding a verification open). The number a reader should trust is the one printed to
/// the test output.</para>
/// </summary>
public sealed class LockfileReviewCostTests
{
    private readonly ITestOutputHelper _output;

    public LockfileReviewCostTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ALargeLockfile_IsReviewedInTimeThatDoesNotMatterOnTheMergePath()
    {
        const int packages = 5_000;
        var baseText = NpmLock(packages, bumpEvery: 0);
        var branchText = NpmLock(packages, bumpEvery: 50); // ~100 changed rows, the realistic shape of a bump

        var snapshot = OsvSnapshot.FromEntries(new[]
        {
            ("CVE-COST-1", "pkg-0000", (IReadOnlyList<string>)new[] { "2.0.0" }),
        });

        // One untimed pass so JIT and the regex caches are not charged to the measurement.
        _ = LockfileReview.Review("package-lock.json", LockfileKind.NpmPackageLock, baseText, branchText, snapshot);

        var sw = Stopwatch.StartNew();
        var items = LockfileReview.Review("package-lock.json", LockfileKind.NpmPackageLock, baseText, branchText, snapshot);
        sw.Stop();

        _output.WriteLine(
            $"{packages} packages, {baseText.Length / 1024} KB per side: "
            + $"{sw.Elapsed.TotalMilliseconds:F1} ms, {items.Count} flagged item(s)");

        // It did real work — a bound on a review that silently parsed nothing would measure nothing.
        Assert.Contains(items, i => i.Kind == FlaggedKind.LockfileCve);
        Assert.DoesNotContain(items, i => i.Kind == FlaggedKind.LockfileAdvisoryUnknown);

        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(3),
            $"reviewing a {packages}-package lockfile took {sw.Elapsed.TotalMilliseconds:F0} ms — this runs "
            + "on the merge path, so an order-of-magnitude regression here stalls every verification");
    }

    /// <summary>
    /// The size bound is a refusal that SAYS SO, not a skip. A manifest too large to parse is the same
    /// class of fact as a missing advisory database: nothing was established, and silence would render that
    /// as "reviewed and clean".
    /// </summary>
    [Fact]
    public void AManifestOverTheSizeBound_IsRefusedAsUnknown_NotSkipped()
    {
        var oversize = new string('x', LockfileReview.MaxManifestBytes + 1);

        var items = LockfileReview.Review("package-lock.json", LockfileKind.NpmPackageLock, "{}", oversize);

        var unknown = Assert.Single(items);
        Assert.Equal(FlaggedKind.LockfileAdvisoryUnknown, unknown.Kind);
        Assert.Contains("larger than", unknown.Detail, StringComparison.Ordinal);
    }

    // A package-lock.json v3 with `packages` entries: pkg-0000 … pkg-NNNN, every `bumpEvery`-th one at a
    // different version on the branch side so the diff has real work to do.
    private static string NpmLock(int count, int bumpEvery)
    {
        var sb = new StringBuilder(count * 200);
        sb.Append("{\n  \"name\": \"app\",\n  \"lockfileVersion\": 3,\n  \"packages\": {\n");
        sb.Append("    \"\": { \"name\": \"app\", \"version\": \"1.0.0\" }");

        for (var i = 0; i < count; i++)
        {
            var bumped = bumpEvery > 0 && i % bumpEvery == 0;
            sb.Append(",\n    \"node_modules/pkg-").Append(i.ToString("D4")).Append("\": { \"version\": \"")
              .Append(bumped ? "2.0.0" : "1.0.0")
              .Append("\", \"resolved\": \"https://registry.npmjs.org/pkg-").Append(i.ToString("D4"))
              .Append("/-/pkg.tgz\" }");
        }

        sb.Append("\n  }\n}\n");
        return sb.ToString();
    }
}

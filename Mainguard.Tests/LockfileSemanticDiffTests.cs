using System.Linq;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Review;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-11 test 6 (LockfileSemanticDiff_Fixtures): per-format delta extraction plus the install-scripts
/// flag and the <b>offline</b> OSV CVE flag (a review-time network call is a rejection trigger — CVE hits
/// come only from the shipped <see cref="OsvSnapshot"/>).
/// </summary>
public class LockfileSemanticDiffTests
{
    [Fact]
    public void Npm_ExtractsAddedUpdated_WithInstallScriptsAndCve()
    {
        const string oldLock = """
        {
          "lockfileVersion": 3,
          "packages": {
            "": { "name": "root" },
            "node_modules/lodash": { "version": "4.17.20", "resolved": "https://registry.npmjs.org/lodash/-/lodash-4.17.20.tgz" }
          }
        }
        """;
        const string newLock = """
        {
          "lockfileVersion": 3,
          "packages": {
            "": { "name": "root" },
            "node_modules/lodash": { "version": "4.17.15", "resolved": "https://registry.npmjs.org/lodash/-/lodash-4.17.15.tgz" },
            "node_modules/node-sass": { "version": "7.0.0", "hasInstallScript": true },
            "node_modules/left-pad-evil": { "version": "1.0.0" }
          }
        }
        """;

        var deltas = LockfileSemanticDiff.Parse(oldLock, newLock, LockfileKind.NpmPackageLock);

        var lodash = deltas.Single(d => d.Name == "lodash");
        Assert.Equal(DependencyDeltaKind.Updated, lodash.Kind);
        Assert.Contains("CVE-2020-8203", lodash.CveIds);

        var nodeSass = deltas.Single(d => d.Name == "node-sass");
        Assert.Equal(DependencyDeltaKind.Added, nodeSass.Kind);
        Assert.True(nodeSass.InstallScripts);

        var evil = deltas.Single(d => d.Name == "left-pad-evil");
        Assert.NotEmpty(evil.CveIds);

        // The script-bearing and CVE-hit rows feed the flagged gate.
        var flagged = FlaggedChangeDetector.FromLockfileDeltas("package-lock.json", deltas);
        Assert.Contains(flagged, f => f.Kind == FlaggedKind.LockfileScript);
        Assert.Contains(flagged, f => f.Kind == FlaggedKind.LockfileCve);
    }

    [Fact]
    public void Csproj_ExtractsMajorJumpAndAdd()
    {
        const string oldProj = """
        <Project>
          <ItemGroup>
            <PackageReference Include="Newtonsoft.Json" Version="12.0.0" />
          </ItemGroup>
        </Project>
        """;
        const string newProj = """
        <Project>
          <ItemGroup>
            <PackageReference Include="Newtonsoft.Json" Version="13.0.0" />
            <PackageReference Include="Serilog" Version="3.0.0" />
          </ItemGroup>
        </Project>
        """;

        var deltas = LockfileSemanticDiff.Parse(oldProj, newProj, LockfileKind.CsprojPackageReference);

        var json = deltas.Single(d => d.Name == "Newtonsoft.Json");
        Assert.Equal(DependencyDeltaKind.Updated, json.Kind);
        Assert.True(json.MajorJump);

        Assert.Contains(deltas, d => d.Name == "Serilog" && d.Kind == DependencyDeltaKind.Added);
    }

    [Fact]
    public void Pnpm_ExtractsInstallScriptedAdd_WithCve()
    {
        const string oldLock = """
        lockfileVersion: '6.0'
        packages:
        """;
        const string newLock = """
        lockfileVersion: '6.0'
        packages:
          /left-pad-evil@1.0.0:
            resolution: {integrity: sha512-abc}
            requiresBuild: true
            dev: false
        """;

        var deltas = LockfileSemanticDiff.Parse(oldLock, newLock, LockfileKind.PnpmLock);
        var evil = deltas.Single(d => d.Name == "left-pad-evil");
        Assert.Equal(DependencyDeltaKind.Added, evil.Kind);
        Assert.True(evil.InstallScripts);
        Assert.NotEmpty(evil.CveIds);
    }

    [Fact]
    public void Poetry_ExtractsUpdatedAndAdded()
    {
        const string oldLock = """
        [[package]]
        name = "requests"
        version = "2.28.0"
        """;
        const string newLock = """
        [[package]]
        name = "requests"
        version = "2.31.0"

        [[package]]
        name = "urllib3"
        version = "2.0.0"
        """;

        var deltas = LockfileSemanticDiff.Parse(oldLock, newLock, LockfileKind.PoetryLock);
        Assert.Contains(deltas, d => d.Name == "requests" && d.Kind == DependencyDeltaKind.Updated);
        Assert.Contains(deltas, d => d.Name == "urllib3" && d.Kind == DependencyDeltaKind.Added);
    }

    [Fact]
    public void OsvSnapshot_IsOffline_AndInjectable()
    {
        Assert.NotEmpty(OsvSnapshot.Default.Lookup("lodash", "4.17.15"));
        Assert.Empty(OsvSnapshot.Default.Lookup("lodash", "0.0.0-not-real"));

        var custom = OsvSnapshot.FromEntries(new[] { ("CVE-TEST-1", "my-pkg", (System.Collections.Generic.IReadOnlyList<string>)new[] { "1.2.3" }) });
        const string oldLock = "{ \"packages\": { \"\": {} } }";
        const string newLock = "{ \"packages\": { \"\": {}, \"node_modules/my-pkg\": { \"version\": \"1.2.3\" } } }";
        var deltas = LockfileSemanticDiff.Parse(oldLock, newLock, LockfileKind.NpmPackageLock, custom);
        Assert.Contains("CVE-TEST-1", deltas.Single(d => d.Name == "my-pkg").CveIds);

        // ...and a hand-built snapshot is dated TODAY by default, so a test that means to exercise a
        // healthy snapshot does not accidentally exercise a stale one.
        Assert.True(custom.CanAnswer(out _));
        Assert.True(deltas.Single(d => d.Name == "my-pkg").AdvisoriesChecked);
    }

    /// <summary>
    /// The shipped snapshot is <b>bundled</b>, so it is refreshed by shipping a build and nothing else —
    /// which means it ages silently unless something says so. This is that something: it fails when
    /// <c>OsvSnapshot.json</c> has drifted past <see cref="OsvSnapshot.MaxAge"/>, and the fix is to refresh
    /// the file and bump its <c>snapshotDate</c>.
    ///
    /// <para>A deliberate maintenance alarm rather than a time bomb: without it the product would keep
    /// answering CVE questions from a database nobody had looked at in a year, and the only visible symptom
    /// would be every lockfile review quietly reporting "not checked" to users while CI stayed green.</para>
    /// </summary>
    [Fact]
    public void TheShippedOsvSnapshot_IsLoadable_AndStillWithinItsMaxAge()
    {
        Assert.NotNull(OsvSnapshot.Default.CapturedOn);

        Assert.True(
            OsvSnapshot.Default.CanAnswer(out var why),
            $"the shipped OsvSnapshot.json can no longer answer advisory questions ({why}). "
            + "Refresh Mainguard.Git/Review/OsvSnapshot.json and bump its snapshotDate.");
    }

    /// <summary>
    /// An unusable snapshot must not launder into "no known CVEs". This is the parser-level half of the
    /// promise; the cockpit-level half is
    /// <c>Mainguard.Server.Tests.LockfileAdvisoryCockpitTests</c>.
    /// </summary>
    [Fact]
    public void AnUnusableSnapshot_MarksIntroducedRowsUnchecked_RatherThanClean()
    {
        const string oldLock = "{ \"packages\": { \"\": {}, \"node_modules/keep\": { \"version\": \"1.0.0\" } } }";
        const string newLock = "{ \"packages\": { \"\": {}, \"node_modules/added\": { \"version\": \"2.0.0\" } } }";

        var missing = LockfileSemanticDiff.Parse(oldLock, newLock, LockfileKind.NpmPackageLock, OsvSnapshot.Unavailable());

        var added = missing.Single(d => d.Name == "added");
        Assert.Empty(added.CveIds);            // nothing was found...
        Assert.False(added.AdvisoriesChecked); // ...because nothing was looked at.
        Assert.True(added.AdvisoryStatusUnknown);

        // A removal introduces no bytes, so there is nothing about it to be unsure of.
        var removed = missing.Single(d => d.Name == "keep");
        Assert.Equal(DependencyDeltaKind.Removed, removed.Kind);
        Assert.False(removed.AdvisoryStatusUnknown);

        // A stale snapshot is the same verdict by a different route — and its hits, if any, still stand.
        var stale = OsvSnapshot.FromEntries(
            System.Array.Empty<(string, string, System.Collections.Generic.IReadOnlyList<string>)>(),
            capturedOn: System.DateOnly.FromDateTime(System.DateTime.UtcNow).AddDays(-400));
        Assert.Equal(OsvSnapshotState.Stale, stale.State);
        Assert.False(
            LockfileSemanticDiff.Parse(oldLock, newLock, LockfileKind.NpmPackageLock, stale)
                .Single(d => d.Name == "added").AdvisoriesChecked);
    }
}

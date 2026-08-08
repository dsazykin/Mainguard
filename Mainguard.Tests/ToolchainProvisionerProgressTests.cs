using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The per-repo toolchain layer is the one step of a coordinator launch that runs for MINUTES — a
/// <c>dotnet-10</c> layer pulls ~2.9 GB — and it runs inside the spawn call. Until now it said so only
/// in the daemon log, so the surface had nothing to show for the wait and, after 45 s, told the user
/// "the coordinator isn't responding … use Stop to cancel and try again" — advice that killed the very
/// build about to unblock it (the same shape as the sandbox-image loop closed in PR #300, where the
/// offered recovery guaranteed the failure repeated).
///
/// <para>These pin the daemon end of the progress channel: <b>that</b> a user-facing line is reported,
/// <b>when</b> (before the build, not after — a line that arrives when the wait is over is not
/// progress), and <b>only</b> when a build actually happens. A cache hit takes milliseconds; announcing
/// "this takes several minutes" for one would train people to ignore the message.</para>
/// </summary>
public class ToolchainProvisionerProgressTests
{
    private const string BaseDigest =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static readonly ToolchainDeclaration Dotnet = ToolchainDeclarationResolver.Parse("dotnet-10");

    [Fact]
    public async Task ABuild_ReportsTheUserFacingLine_BeforeItStarts_AndTheReadyLineAfter()
    {
        var builder = new FakeBuilder();
        var reported = new List<string>();
        var buildsAtReport = new List<int>();
        var sink = new Collector(m => { reported.Add(m); buildsAtReport.Add(builder.Builds); });

        var result = await new ToolchainProvisioner(builder, log: null, progress: sink)
            .EnsureAsync("repo-1", Dotnet, BaseDigest, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, builder.Builds);

        // Ordering is the property, not merely presence: the line has to be on the wire while the user
        // is waiting. Reporting it after BuildAsync returned would satisfy a "was it reported?" test and
        // would be worthless.
        Assert.Equal(2, reported.Count);
        Assert.Equal(ToolchainProvisioner.BuildingMessage(Dotnet), reported[0]);
        Assert.Equal(ToolchainProvisioner.BuiltMessage, reported[1]);
        Assert.Equal(0, buildsAtReport[0]); // the "building" line went out BEFORE the build ran
        Assert.Equal(1, buildsAtReport[1]); // the "ready" line only after it finished

        // Written for the person waiting: it names what is being built and says not to interrupt it.
        Assert.Contains("dotnet-10", reported[0], StringComparison.Ordinal);
        Assert.Contains("several minutes", reported[0], StringComparison.Ordinal);
        Assert.Contains("leave Mainguard running", reported[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACacheHit_ReportsNothing_BecauseThereIsNoWaitToExplain()
    {
        var builder = new FakeBuilder();
        var provisioner = new ToolchainProvisioner(builder);

        // Populate the cache through the real path, so the second call hits exactly what a second spawn
        // would hit rather than a hand-placed fixture.
        await provisioner.EnsureAsync("repo-1", Dotnet, BaseDigest, CancellationToken.None);
        Assert.Equal(1, builder.Builds);

        var reported = new List<string>();
        var result = await new ToolchainProvisioner(builder, log: null, progress: new Collector(reported.Add))
            .EnsureAsync("repo-1", Dotnet, BaseDigest, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, builder.Builds); // still one — the second call was a cache hit
        Assert.Empty(reported);
    }

    /// <summary>The sink is optional, and every existing caller passes no progress at all. A null sink
    /// must not change a single thing about the build.</summary>
    [Fact]
    public async Task WithNoProgressSink_TheBuildIsUnchanged()
    {
        var builder = new FakeBuilder();
        var result = await new ToolchainProvisioner(builder).EnsureAsync(
            "repo-1", Dotnet, BaseDigest, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, builder.Builds);
    }

    private sealed class Collector(Action<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink(value);
    }

    /// <summary>An in-memory image store: <see cref="BuildAsync"/> materialises the tag with the labels
    /// it was given and a layer chain that extends the base's, which is what the provisioner's
    /// inheritance proof reads.</summary>
    private sealed class FakeBuilder : IToolchainImageBuilder
    {
        private static readonly string[] BaseLayers = { "sha256:layer-base-a", "sha256:layer-base-b" };

        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _labels = new(StringComparer.Ordinal);

        public int Builds { get; private set; }

        public Task<IReadOnlyDictionary<string, string>?> InspectLabelsAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult(_labels.TryGetValue(imageRef, out var l) ? l : null);

        public Task<string?> ResolveDigestAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult<string?>(imageRef.StartsWith("sha256:", StringComparison.Ordinal) ? imageRef : imageRef);

        public Task<IReadOnlyList<string>?> RootFsLayersAsync(string imageRef, CancellationToken ct = default)
        {
            if (string.Equals(imageRef, BaseDigest, StringComparison.Ordinal))
                return Task.FromResult<IReadOnlyList<string>?>(BaseLayers.ToArray());

            return Task.FromResult<IReadOnlyList<string>?>(
                _labels.ContainsKey(imageRef)
                    ? BaseLayers.Append("sha256:layer-toolchain").ToArray()
                    : null);
        }

        public Task BuildAsync(
            string imageRef, string dockerfile, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
        {
            Builds++;
            _labels[imageRef] = new Dictionary<string, string>(labels, StringComparer.Ordinal);
            return Task.CompletedTask;
        }
    }
}

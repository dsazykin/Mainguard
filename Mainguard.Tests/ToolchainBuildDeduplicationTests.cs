using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// One toolchain image build at a time, ACROSS SPAWNS.
///
/// <para><b>The defect these pin.</b> <see cref="ToolchainProvisioner"/> serialised builds on a
/// <c>SemaphoreSlim</c> field, and its comment claimed two agents spawning into the same repo could not
/// race two identical multi-minute builds. The comment described a guarantee the code could not give:
/// <c>SandboxAgentLauncher.EnsureToolchainAsync</c> constructs a NEW provisioner per spawn, so every
/// spawn arrived with its own uncontended semaphore. Two spawns therefore both missed the cache, both
/// issued <c>docker build</c> for the same tag, and the engine ran both — gigabytes of duplicate
/// download, each build making the other slower, and the slowness then read as a hung coordinator.</para>
///
/// <para>So the property is deliberately asserted through <b>two separate provisioner objects</b>, which
/// is the shape production has. A test that reused one provisioner would have passed against the broken
/// code, which is exactly how the claim survived this long.</para>
/// </summary>
public class ToolchainBuildDeduplicationTests
{
    private static readonly ToolchainDeclaration Dotnet = ToolchainDeclarationResolver.Parse("dotnet-10");
    private static readonly ToolchainDeclaration Rust = ToolchainDeclarationResolver.Parse("rust-stable");

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>A unique base digest per test, so a shared process-wide gate can be exercised without two
    /// tests contending on one key.</summary>
    private static string UniqueBase() =>
        "sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task TwoSpawnsOfTheSameImage_ProduceExactlyOneBuild_AcrossSeparateProvisioners()
    {
        var digest = UniqueBase();
        var builder = new BlockingBuilder(digest);
        var gate = new ToolchainBuildGate();

        // Spawn A gets into the build and stays there, exactly as a real 2.9 GB layer would.
        var first = new ToolchainProvisioner(builder, buildGate: gate)
            .EnsureAsync("repo-1", Dotnet, digest, CancellationToken.None);
        Assert.True(await builder.BuildEntered.Task.WaitAsync(Patience));

        // Spawn B — a DIFFERENT provisioner, which is what the launcher makes — asks for the same layer.
        var second = new ToolchainProvisioner(builder, buildGate: gate)
            .EnsureAsync("repo-1", Dotnet, digest, CancellationToken.None);

        // It must be waiting, not building. Given long enough to have started a rival build if it were
        // going to: with the per-instance gate this is where Builds becomes 2.
        await Task.Delay(150);
        Assert.Equal(1, builder.Builds);
        Assert.False(second.IsCompleted);

        builder.Release();
        var a = await first.WaitAsync(Patience);
        var b = await second.WaitAsync(Patience);

        Assert.Equal(1, builder.Builds);       // ONE build served both spawns
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.ImageRef, b!.ImageRef); // and both jail from the same layer
    }

    /// <summary>
    /// The same property with NO gate passed in — which is the production wiring, since
    /// <c>SandboxAgentLauncher</c> builds its provisioner with the default. An injected gate proves the
    /// mechanism; only the default proves the shipped spawn path uses it. The key is a fresh digest, so
    /// sharing one process-wide gate with the rest of the suite is safe.
    /// </summary>
    [Fact]
    public async Task TwoSpawnsOnTheDefaultGate_AlsoProduceExactlyOneBuild()
    {
        var digest = UniqueBase();
        var builder = new BlockingBuilder(digest);

        var first = new ToolchainProvisioner(builder).EnsureAsync("repo-1", Dotnet, digest, CancellationToken.None);
        Assert.True(await builder.BuildEntered.Task.WaitAsync(Patience));

        var second = new ToolchainProvisioner(builder).EnsureAsync("repo-1", Dotnet, digest, CancellationToken.None);
        await Task.Delay(150);
        Assert.Equal(1, builder.Builds);

        builder.Release();
        await first.WaitAsync(Patience);
        await second.WaitAsync(Patience);
        Assert.Equal(1, builder.Builds);
    }

    /// <summary>
    /// The other half, without which the test above would be satisfied by a single global lock: two
    /// spawns wanting DIFFERENT layers must not queue behind each other. The gate key is the
    /// content-addressed image tag precisely so that "same artefact" and "same wait" are the same
    /// question.
    /// </summary>
    [Fact]
    public async Task TwoSpawnsOfDifferentImages_DoNotBlockEachOther()
    {
        var digest = UniqueBase();
        var builder = new BlockingBuilder(digest);
        var gate = new ToolchainBuildGate();

        var dotnet = new ToolchainProvisioner(builder, buildGate: gate)
            .EnsureAsync("repo-1", Dotnet, digest, CancellationToken.None);
        Assert.True(await builder.BuildEntered.Task.WaitAsync(Patience));

        var rust = new ToolchainProvisioner(builder, buildGate: gate)
            .EnsureAsync("repo-2", Rust, digest, CancellationToken.None);

        // The second build must reach the engine while the first is still in it — different tag,
        // different key, no queueing.
        await WaitUntilAsync(() => builder.Builds == 2, Patience);
        Assert.Equal(2, builder.Builds);

        builder.Release();
        Assert.NotNull(await dotnet.WaitAsync(Patience));
        Assert.NotNull(await rust.WaitAsync(Patience));
    }

    /// <summary>
    /// A waiting spawn must SAY it is waiting. This is the same failure the whole change is about, one
    /// level in: a spawn that sits silent behind another spawn's build for four minutes looks exactly
    /// like a hang to the client's spawn watchdog and to the control center's connect watchdog, and both
    /// would report a healthy wait as an unresponsive daemon.
    /// </summary>
    [Fact]
    public async Task AWaitingSpawn_ReportsThatItIsWaiting_ForSomeoneElsesBuild()
    {
        var digest = UniqueBase();
        var builder = new BlockingBuilder(digest);
        var gate = new ToolchainBuildGate();
        var reported = new ConcurrentQueue<string>();

        var first = new ToolchainProvisioner(builder, buildGate: gate)
            .EnsureAsync("repo-1", Dotnet, digest, CancellationToken.None);
        Assert.True(await builder.BuildEntered.Task.WaitAsync(Patience));

        var second = new ToolchainProvisioner(
                builder, log: null, progress: new Collector(reported.Enqueue), buildGate: gate,
                heartbeatInterval: TimeSpan.FromMilliseconds(20))
            .EnsureAsync("repo-1", Dotnet, digest, CancellationToken.None);

        await WaitUntilAsync(() => reported.Any(m => m.StartsWith("Waiting for", StringComparison.Ordinal)), Patience);

        builder.Release();
        await first.WaitAsync(Patience);
        await second.WaitAsync(Patience);

        var waiting = reported.First(m => m.StartsWith("Waiting for", StringComparison.Ordinal));
        Assert.Contains("dotnet-10", waiting, StringComparison.Ordinal);
        Assert.Contains("another agent is already building it", waiting, StringComparison.Ordinal);
        Assert.Contains("Leave Mainguard running", waiting, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first spawn being cancelled (the user pressed Stop) must free the gate, and the second spawn
    /// must then build for itself. This is why the gate serialises rather than handing the second caller
    /// the first one's task: joined callers share a fate, and one user's Stop would silently cancel
    /// another spawn's build.
    /// </summary>
    [Fact]
    public async Task AFirstSpawnCancelled_LeavesTheGateUsable_AndTheSecondBuildsItself()
    {
        var digest = UniqueBase();
        var builder = new BlockingBuilder(digest);
        var gate = new ToolchainBuildGate();
        using var cts = new CancellationTokenSource();

        var first = new ToolchainProvisioner(builder, buildGate: gate)
            .EnsureAsync("repo-1", Dotnet, digest, cts.Token);
        Assert.True(await builder.BuildEntered.Task.WaitAsync(Patience));

        var second = new ToolchainProvisioner(builder, buildGate: gate)
            .EnsureAsync("repo-1", Dotnet, digest, CancellationToken.None);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // The second spawn was NOT cancelled with it, and finds the gate free.
        builder.Release();
        Assert.NotNull(await second.WaitAsync(Patience));
        Assert.Equal(2, builder.Builds); // the cancelled build produced nothing to reuse
    }

    /// <summary>A daemon runs for weeks. A gate that kept one dead semaphore per distinct declaration
    /// would be a slow leak, so the entry goes when the last user of the key does.</summary>
    [Fact]
    public async Task TheGate_KeepsNoEntryOnceEveryCallerIsDone()
    {
        var digest = UniqueBase();
        var builder = new BlockingBuilder(digest);
        var gate = new ToolchainBuildGate();

        var build = new ToolchainProvisioner(builder, buildGate: gate)
            .EnsureAsync("repo-1", Dotnet, digest, CancellationToken.None);
        Assert.True(await builder.BuildEntered.Task.WaitAsync(Patience));
        Assert.Equal(1, gate.TrackedKeys);

        builder.Release();
        await build.WaitAsync(Patience);
        Assert.Equal(0, gate.TrackedKeys);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(15);
        }
    }

    private sealed class Collector(Action<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink(value);
    }

    /// <summary>An in-memory image store whose build BLOCKS until released — the only way to observe
    /// what a second spawn does while a first one is mid-build.</summary>
    private sealed class BlockingBuilder(string baseDigest) : IToolchainImageBuilder
    {
        private static readonly string[] BaseLayers = { "sha256:layer-base-a", "sha256:layer-base-b" };

        private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _labels =
            new(StringComparer.Ordinal);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _builds;

        public TaskCompletionSource<bool> BuildEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Builds => Volatile.Read(ref _builds);

        public void Release() => _release.TrySetResult();

        public Task<IReadOnlyDictionary<string, string>?> InspectLabelsAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult(_labels.TryGetValue(imageRef, out var l) ? l : null);

        public Task<string?> ResolveDigestAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult<string?>(imageRef);

        public Task<IReadOnlyList<string>?> RootFsLayersAsync(string imageRef, CancellationToken ct = default)
        {
            if (string.Equals(imageRef, baseDigest, StringComparison.Ordinal))
            {
                return Task.FromResult<IReadOnlyList<string>?>(BaseLayers.ToArray());
            }

            return Task.FromResult<IReadOnlyList<string>?>(
                _labels.ContainsKey(imageRef) ? BaseLayers.Append("sha256:layer-toolchain").ToArray() : null);
        }

        public async Task BuildAsync(
            string imageRef, string dockerfile, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _builds);
            BuildEntered.TrySetResult(true);
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            _labels[imageRef] = new Dictionary<string, string>(labels, StringComparer.Ordinal);
        }
    }
}

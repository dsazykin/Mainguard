using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// A running toolchain build has to keep saying so.
///
/// <para><b>Why one announcement is not enough.</b> Everything upstream decides "alive or wedged?" from
/// what it heard RECENTLY — the client's spawn watchdog cancels on silence, and the control center's
/// connect watchdog re-arms on each new line. A single "building…" at the start goes stale within the
/// budget, and the surface is back to guessing at exactly the moment the build is longest. It guessed
/// wrong before, in the direction that killed the build.</para>
///
/// <para><b>And why the beat must not depend on the engine talking.</b> The <c>dotnet-10</c> recipe's
/// slowest step is <c>curl -fsSL … | tar -xzf</c>: silent by construction, for minutes. A heartbeat
/// gated on engine output would go quiet during the healthiest, longest part of the build — the original
/// bug in a new costume — so it beats on its own and quotes the engine when the engine has said
/// something.</para>
/// </summary>
public class ToolchainBuildHeartbeatTests
{
    private const string BaseDigest =
        "sha256:3333333333333333333333333333333333333333333333333333333333333333";

    private static readonly ToolchainDeclaration Dotnet = ToolchainDeclarationResolver.Parse("dotnet-10");
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ASilentBuild_StillReportsEveryInterval()
    {
        var reported = new List<string>();
        var ticks = Channel.CreateUnbounded<TimeSpan>();
        var clock = new ManualClock();

        var heartbeat = new ToolchainBuildHeartbeat(
            new Collector(reported.Add),
            (elapsed, line, idle) => $"still building, {ToolchainBuildHeartbeat.Describe(elapsed)}"
                                     + (line is null ? " (engine quiet)" : $" — {line}"),
            interval: TimeSpan.FromSeconds(20),
            delay: async (requested, ct) =>
            {
                var step = await ticks.Reader.ReadAsync(ct);
                clock.Advance(step);
            },
            elapsed: () => clock.Now);

        // Three intervals with the engine saying absolutely nothing — the shape of the tarball step.
        for (var i = 0; i < 3; i++)
        {
            await ticks.Writer.WriteAsync(TimeSpan.FromSeconds(20));
            await WaitUntilAsync(() => heartbeat.Reported > i, Patience);
        }

        await heartbeat.DisposeAsync();

        Assert.Equal(3, reported.Count);
        Assert.Equal("still building, 20s (engine quiet)", reported[0]);
        Assert.Equal("still building, 40s (engine quiet)", reported[1]);
        Assert.Equal("still building, 1m00s (engine quiet)", reported[2]);
    }

    [Fact]
    public async Task WhenTheEngineDoesSpeak_TheLineQuotesItsLatestOutput()
    {
        var reported = new List<string>();
        var ticks = Channel.CreateUnbounded<TimeSpan>();
        var clock = new ManualClock();

        var heartbeat = new ToolchainBuildHeartbeat(
            new Collector(reported.Add),
            (elapsed, line, idle) => $"{ToolchainBuildHeartbeat.Describe(elapsed)}|{line}|"
                                     + (idle is { } i ? ToolchainBuildHeartbeat.Describe(i) : "-"),
            interval: TimeSpan.FromSeconds(20),
            delay: async (requested, ct) =>
            {
                var step = await ticks.Reader.ReadAsync(ct);
                clock.Advance(step);
            },
            elapsed: () => clock.Now);

        // Multi-line chunks are what a build stream actually delivers; the newest line is the useful one.
        heartbeat.Report("Step 2/4 : RUN apt-get update\nGet:1 http://deb.debian.org bookworm InRelease\n");
        await ticks.Writer.WriteAsync(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => heartbeat.Reported > 0, Patience);
        await heartbeat.DisposeAsync();

        Assert.Single(reported);
        Assert.Equal("20s|Get:1 http://deb.debian.org bookworm InRelease|20s", reported[0]);
    }

    /// <summary>A line that arrives after the wait it describes has ended is worse than no line: it
    /// re-arms watchdogs for a build that is over and can outlive the spawn it belonged to.</summary>
    [Fact]
    public async Task AfterDispose_NothingIsReported()
    {
        var reported = new ConcurrentQueue<string>();
        var ticks = Channel.CreateUnbounded<TimeSpan>();
        var clock = new ManualClock();

        var heartbeat = new ToolchainBuildHeartbeat(
            new Collector(reported.Enqueue),
            (elapsed, line, idle) => "tick",
            interval: TimeSpan.FromSeconds(20),
            delay: async (requested, ct) =>
            {
                var step = await ticks.Reader.ReadAsync(ct);
                clock.Advance(step);
            },
            elapsed: () => clock.Now);

        await heartbeat.DisposeAsync();

        // The pump is gone: releasing a tick after disposal must produce nothing at all.
        await ticks.Writer.WriteAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(80);
        Assert.Empty(reported);
    }

    /// <summary>
    /// The property as the spawn path actually assembles it: a build that runs long keeps producing
    /// user-facing lines through the provisioner's own progress sink, and each one is DISTINCT (an
    /// identical repeat is dropped by the daemon's session store, so a constant line would reach nobody).
    /// </summary>
    [Fact]
    public async Task ALongBuild_KeepsTheSpawnsProgressSinkTalking()
    {
        var reported = new ConcurrentQueue<string>();
        var twoBeats = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new SlowBuilder(() =>
        {
            if (reported.Count(m => m.StartsWith("Still building", StringComparison.Ordinal)) >= 2)
            {
                twoBeats.TrySetResult();
            }
        });

        var provisioner = new ToolchainProvisioner(
            builder, log: null, progress: new Collector(reported.Enqueue),
            buildGate: new ToolchainBuildGate(), heartbeatInterval: TimeSpan.FromMilliseconds(25));

        var build = provisioner.EnsureAsync("repo-1", Dotnet, BaseDigest, CancellationToken.None);
        await twoBeats.Task.WaitAsync(Patience);
        builder.Release();
        Assert.NotNull(await build.WaitAsync(Patience));

        var lines = reported.ToArray();
        Assert.Equal(ToolchainProvisioner.BuildingMessage(Dotnet), lines[0]);
        Assert.Equal(ToolchainProvisioner.BuiltMessage, lines[^1]);

        var beats = lines.Where(m => m.StartsWith("Still building", StringComparison.Ordinal)).ToArray();
        Assert.True(beats.Length >= 2, $"expected repeated progress, got {beats.Length}");
        Assert.Contains("dotnet-10", beats[0], StringComparison.Ordinal);
        Assert.Contains("Leave Mainguard running", beats[0], StringComparison.Ordinal);

        // No heartbeat after the build ended: the last line is the ready one, not a beat.
        Assert.DoesNotContain(
            lines.SkipWhile(m => m != ToolchainProvisioner.BuiltMessage),
            m => m.StartsWith("Still building", StringComparison.Ordinal));
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

            await Task.Delay(10);
        }

        throw new TimeoutException("the condition never became true");
    }

    private sealed class ManualClock
    {
        private long _ticks;

        public TimeSpan Now => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class Collector(Action<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink(value);
    }

    /// <summary>A build that runs until released, and calls back after every progress line so the test
    /// can end it the moment it has seen what it came for (no sleeping on wall-clock guesses).</summary>
    private sealed class SlowBuilder(Action onProgressObserved) : IToolchainImageBuilder
    {
        private static readonly string[] BaseLayers = { "sha256:layer-base-a", "sha256:layer-base-b" };

        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _labels = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public Task<IReadOnlyDictionary<string, string>?> InspectLabelsAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult(_labels.TryGetValue(imageRef, out var l) ? l : null);

        public Task<string?> ResolveDigestAsync(string imageRef, CancellationToken ct = default) =>
            Task.FromResult<string?>(imageRef);

        public Task<IReadOnlyList<string>?> RootFsLayersAsync(string imageRef, CancellationToken ct = default)
        {
            if (string.Equals(imageRef, BaseDigest, StringComparison.Ordinal))
                return Task.FromResult<IReadOnlyList<string>?>(BaseLayers.ToArray());

            return Task.FromResult<IReadOnlyList<string>?>(
                _labels.ContainsKey(imageRef) ? BaseLayers.Append("sha256:layer-toolchain").ToArray() : null);
        }

        public Task BuildAsync(
            string imageRef, string dockerfile, IReadOnlyDictionary<string, string> labels, CancellationToken ct = default)
            => BuildAsync(imageRef, dockerfile, labels, engineOutput: null, ct);

        public async Task BuildAsync(
            string imageRef, string dockerfile, IReadOnlyDictionary<string, string> labels,
            IProgress<string>? engineOutput, CancellationToken ct = default)
        {
            // Poll the observer while the build runs — this stands in for "the engine is working".
            while (!_release.Task.IsCompleted)
            {
                onProgressObserved();
                await Task.Delay(10, ct).ConfigureAwait(false);
            }

            _labels[imageRef] = new Dictionary<string, string>(labels, StringComparer.Ordinal);
        }
    }
}

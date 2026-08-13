using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// The process-wide "one build per toolchain image, across every spawn" gate.
///
/// <para><b>Why it is not a field on the provisioner.</b> <see cref="ToolchainProvisioner"/> held its
/// own <see cref="SemaphoreSlim"/> and its comment claimed two agents spawning into the same repo could
/// not race two identical multi-minute builds. That was false for the only shape the race actually
/// takes: <c>SandboxAgentLauncher.EnsureToolchainAsync</c> constructs a NEW provisioner on every spawn,
/// so each spawn brought its own uncontended gate. Two spawns therefore both read "no cached image",
/// both issued <c>docker build</c> for the same tag, and the engine ran both — several gigabytes of
/// duplicate download racing each other, each one making the other slower and more likely to be
/// declared unresponsive. A gate that lives on a per-spawn object cannot serialise across spawns; that
/// is not a tuning detail, it is the wrong lifetime.</para>
///
/// <para><b>The key is the image tag, not the repo.</b> The tag is already a content address over the
/// base digest, the declaration and the rendered Dockerfile (see
/// <see cref="ToolchainProvisioner.ImageTagFor(string,ToolchainDeclaration)"/>), which is exactly the
/// set of inputs that decide whether two builds would produce the same layer. Keying by repo would be
/// both too narrow (two repos declaring <c>dotnet-10</c> off the same base build the identical layer and
/// must not race) and too wide (one repo whose declaration changed mid-flight is a different artefact
/// and must not be serialised behind a build that cannot satisfy it).</para>
///
/// <para><b>Serialise, do not join.</b> The second caller waits and then finds the cache hit the first
/// caller's build produced — one build, both spawns satisfied. Handing caller two the first caller's
/// <see cref="Task"/> instead (the way <see cref="Bootstrap.SandboxImageProvisioningTracker"/> joins
/// its single global run) would tie the two spawns' fates together: the user pressing Stop on the first
/// coordinator would cancel the second spawn's build too, and the second caller would inherit the
/// first's cancellation token, progress sink and log sink. Waiting keeps each spawn's cancellation its
/// own, and a first caller that IS cancelled simply releases the gate for the second to build.</para>
/// </summary>
public sealed class ToolchainBuildGate
{
    /// <summary>
    /// The one gate the shipped spawn path shares — the whole point of the type. Static rather than
    /// injected, matching <see cref="Bootstrap.SandboxImageProvisioningTracker.Shared"/> and the app's
    /// no-DI convention: what has to be true is that two provisioners built by two unrelated spawns
    /// contend on the same object, and only a process-wide instance makes that true.
    /// </summary>
    public static ToolchainBuildGate Shared { get; } = new();

    /// <summary>How often a caller that is WAITING on someone else's build is told so. Twenty seconds is
    /// short enough that a waiting spawn keeps re-arming the client's stall watchdogs and long enough
    /// that the stream is not flooded.</summary>
    public static readonly TimeSpan DefaultWaitHeartbeat = TimeSpan.FromSeconds(20);

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>The number of keys currently held or waited on. Diagnostics/tests only — a gate that
    /// leaked an entry per spawn would be a slow memory leak in a daemon that never restarts.</summary>
    internal int TrackedKeys
    {
        get { lock (_sync) return _entries.Count; }
    }

    /// <summary>
    /// Runs <paramref name="work"/> with no other caller running work for the same <paramref name="key"/>
    /// anywhere in this process.
    /// </summary>
    /// <param name="key">The content-addressed identity of the artefact being produced (the image tag).</param>
    /// <param name="work">The build-or-reuse body. Receives the caller's own cancellation token.</param>
    /// <param name="onWaiting">Invoked with the elapsed wait each <paramref name="waitHeartbeat"/> while
    /// this caller is queued behind someone else's build — never invoked when the gate is free. A wait
    /// that says nothing looks exactly like a hang to everything upstream, which is the failure this
    /// whole change exists to remove.</param>
    /// <param name="waitHeartbeat">How often <paramref name="onWaiting"/> fires (default
    /// <see cref="DefaultWaitHeartbeat"/>).</param>
    public async Task<T> RunExclusiveAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> work,
        Action<TimeSpan>? onWaiting = null,
        TimeSpan? waitHeartbeat = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(work);

        var entry = Rent(key);
        try
        {
            var beat = waitHeartbeat is { } h && h > TimeSpan.Zero ? h : DefaultWaitHeartbeat;
            var waited = Stopwatch.StartNew();
            // WaitAsync(timeout) returns false on expiry rather than throwing, so the loop is "take the
            // gate, or say we are still waiting and try again". The uncontended case takes the very
            // first attempt and reports nothing at all.
            while (!await entry.Gate.WaitAsync(beat, ct).ConfigureAwait(false))
            {
                onWaiting?.Invoke(waited.Elapsed);
            }

            try
            {
                return await work(ct).ConfigureAwait(false);
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        finally
        {
            Return(key, entry);
        }
    }

    private Entry Rent(string key)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Users++;
            return entry;
        }
    }

    private void Return(string key, Entry entry)
    {
        lock (_sync)
        {
            // Ref-counted so the map does not grow one dead semaphore per distinct declaration for the
            // lifetime of a daemon that is expected to run for weeks.
            if (--entry.Users <= 0 && _entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Users { get; set; }
    }
}

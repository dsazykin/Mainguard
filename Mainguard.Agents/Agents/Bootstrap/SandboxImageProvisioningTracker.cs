using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Process-wide "is a sandbox-image provisioning run happening right now?" tracker, and the gate that
/// keeps two of them from ever running at once.
///
/// <para><b>Why this exists (field failure 2026-08-05).</b> Provisioning a stale jail image is a
/// <c>docker build</c> that takes MINUTES (the agent-base image's <c>nix profile install</c> layer pulls
/// a toolchain from cache.nixos.org — measured at ~4 minutes on a warm network, and it is a single
/// layer, so a kill mid-download throws away all of it). Two things then conspired to make the
/// automatic repair impossible:</para>
///
/// <list type="number">
/// <item>The startup auto-provision (<c>KickSandboxImageBuild</c>) and the Tools → Rebuild repair were
/// each a discarded <c>Task.Run</c> with nothing tracking them, so the user could — and did — start a
/// second <c>docker build</c> of the SAME tag on top of the first. <c>ProvisionAsync</c> serializes
/// images WITHIN one run; it could not serialize two runs.</item>
/// <item>Nothing connected that in-flight build to app exit, so the shutdown sequence's
/// <c>StopVmOnExit</c> leg ran <c>wsl --terminate MainguardEnv</c> straight through it — killing the
/// build ~10ms after the terminate, every single time. The images could never become current, so the
/// coordinator refused to start, so the user restarted to "wait for the notice" (exactly what the old
/// preflight banner told them to do), which killed the next build too.</item>
/// </list>
///
/// <para>So: one run at a time (a concurrent request JOINS the run already in flight rather than
/// starting a rival build), and <see cref="IsProvisioning"/> is the signal
/// <see cref="AppShutdownSequence"/> consults so exit never terminates the distro out from under a
/// build. This type owns no docker knowledge — it is purely the in-flight bookkeeping.</para>
/// </summary>
public sealed class SandboxImageProvisioningTracker
{
    /// <summary>The one tracker the app's provisioning entry points share. A static instance (rather
    /// than an injected one) matches the no-DI convention and is what makes "the startup kick and the
    /// Tools rebuild cannot race" true across two unrelated call sites.</summary>
    public static SandboxImageProvisioningTracker Shared { get; } = new();

    private readonly object _gate = new();
    private Task? _inFlight;

    /// <summary>True while a provisioning run is executing. Read by the shutdown sequence (to skip the
    /// VM terminate) and by any UI that wants to say "still updating".</summary>
    public bool IsProvisioning
    {
        get
        {
            lock (_gate)
            {
                return _inFlight is { IsCompleted: false };
            }
        }
    }

    /// <summary>The in-flight run, or an already-completed task when idle — for a caller that wants to
    /// await the current run without being able to start one.</summary>
    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _inFlight is { IsCompleted: false } t ? t : Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> unless a run is already in flight, in which case the in-flight run
    /// is returned and <paramref name="work"/> is never started — the caller awaits the same outcome
    /// instead of racing a second <c>docker build</c> onto the same tag.
    ///
    /// <para>Joining (rather than queueing) is deliberate: both entry points provision the SAME set of
    /// images from the SAME bundled sources, so the run already going is exactly the work the second
    /// caller wanted. A forced Tools rebuild that arrives mid-auto-provision is therefore satisfied by
    /// the build already running, which is what the user asked for and one build cheaper.</para>
    /// </summary>
    /// <param name="work">The provisioning run to start when idle.</param>
    /// <param name="onJoinedExisting">Invoked (for a log breadcrumb) when the request joined a run
    /// already in flight instead of starting one.</param>
    public Task RunExclusiveAsync(Func<Task> work, Action? onJoinedExisting = null)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false } existing)
            {
                onJoinedExisting?.Invoke();
                return existing;
            }

            // Start inside the lock so two callers cannot both observe "idle" and both start a build.
            // Task.Run keeps the (minutes-long) build off the caller's thread, matching the previous
            // fire-and-forget shape — the difference is that the task is now RETAINED, not discarded.
            var started = Task.Run(work);
            _inFlight = started;
            return started;
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Git.Exceptions;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The in-flight bookkeeping that makes the sandbox-image auto-repair survivable (field failure
/// 2026-08-05): one provisioning run at a time, and a truthful <see cref="SandboxImageProvisioningTracker.IsProvisioning"/>
/// for the shutdown sequence to veto the VM terminate on. Instances are constructed directly here —
/// the static <c>Shared</c> is the app's wiring, never the unit under test.
/// </summary>
public class SandboxImageProvisioningTrackerTests
{
    [Fact]
    public void Idle_tracker_reports_not_provisioning()
    {
        var tracker = new SandboxImageProvisioningTracker();

        Assert.False(tracker.IsProvisioning);
        Assert.True(tracker.Completion.IsCompleted);
    }

    [Fact]
    public async Task IsProvisioning_is_true_while_the_run_is_in_flight_and_false_after()
    {
        var tracker = new SandboxImageProvisioningTracker();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();

        var run = tracker.RunExclusiveAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
        });

        await entered.Task;
        Assert.True(tracker.IsProvisioning);

        release.SetResult();
        await run;

        Assert.False(tracker.IsProvisioning);
    }

    /// <summary>
    /// The concurrent-build bug, verbatim from the owner's oobe.log: the Tools → Rebuild repair fired
    /// while the startup auto-provision was still building mainguard-agent-base:latest, producing two
    /// docker builds of the same tag. The second request must JOIN the first, not start a rival run.
    /// </summary>
    [Fact]
    public async Task Second_request_joins_the_in_flight_run_instead_of_starting_a_second()
    {
        var tracker = new SandboxImageProvisioningTracker();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var starts = 0;
        var joined = 0;

        Task Work()
        {
            Interlocked.Increment(ref starts);
            entered.TrySetResult();
            return release.Task;
        }

        var first = tracker.RunExclusiveAsync(Work);
        await entered.Task;

        var second = tracker.RunExclusiveAsync(Work, onJoinedExisting: () => Interlocked.Increment(ref joined));

        Assert.Same(first, second);
        Assert.Equal(1, joined);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, starts); // the rival build never happened
    }

    [Fact]
    public async Task A_request_after_the_previous_run_finished_starts_a_new_run()
    {
        var tracker = new SandboxImageProvisioningTracker();
        var starts = 0;

        await tracker.RunExclusiveAsync(() => { Interlocked.Increment(ref starts); return Task.CompletedTask; });
        await tracker.RunExclusiveAsync(() => { Interlocked.Increment(ref starts); return Task.CompletedTask; });

        Assert.Equal(2, starts);
    }

    /// <summary>A faulted run must not wedge the tracker into a permanent "provisioning" state — that
    /// would suppress StopVmOnExit forever.</summary>
    [Fact]
    public async Task A_faulted_run_clears_the_in_flight_state()
    {
        var tracker = new SandboxImageProvisioningTracker();

        var run = tracker.RunExclusiveAsync(() => Task.FromException(new InvalidOperationException("boom")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run);

        Assert.False(tracker.IsProvisioning);
    }
}

/// <summary>
/// The spawn-preflight banner is the last thing a blocked user reads, so it must name only what was
/// actually checked and must not send them down a path that cannot work. Three clauses of the previous
/// wording each did exactly that (see <see cref="SandboxImageMissingException"/>'s remarks).
/// </summary>
public class SandboxImageMissingMessageTests
{
    private static string Message(params SandboxImagePreflightProblem[] problems) =>
        new SandboxImageMissingException(problems).Message;

    [Fact]
    public void Names_the_images_and_whether_each_is_missing_or_outdated()
    {
        var msg = Message(
            new SandboxImagePreflightProblem("mainguard-agent-base:latest", Stale: true),
            new SandboxImagePreflightProblem("mainguard-egress-proxy:latest", Stale: false));

        Assert.Contains("outdated: mainguard-agent-base:latest", msg);
        Assert.Contains("missing: mainguard-egress-proxy:latest", msg);
    }

    /// <summary>
    /// The manual fallback must be gone. It named a path that does not exist inside the distro
    /// (<c>/opt/mainguard/payload/images/</c> — verified absent on the owner's VM) and omitted
    /// <c>--label mainguard.image.version=…</c>, so an image built by that command is re-rejected as
    /// stale by the very next probe. A command that cannot succeed is worse than none.
    /// </summary>
    [Fact]
    public void Offers_no_manual_docker_build_command()
    {
        var msg = Message(new SandboxImagePreflightProblem("mainguard-agent-base:latest", Stale: true));

        Assert.DoesNotContain("docker build", msg);
        Assert.DoesNotContain("payload/images", msg);
        Assert.DoesNotContain("wsl -d", msg);
    }

    /// <summary>
    /// "restart Mainguard and wait" was the single most costly sentence: restarting CANCELS the
    /// in-flight build, which is why the images stayed stale across every one of the owner's attempts.
    /// The message must tell them to leave it running instead.
    /// </summary>
    [Fact]
    public void Tells_the_user_to_leave_mainguard_running_not_to_restart_it()
    {
        var msg = Message(new SandboxImagePreflightProblem("mainguard-agent-base:latest", Stale: true));

        Assert.DoesNotContain("restart Mainguard", msg);
        Assert.Contains("Leave Mainguard running", msg);
        Assert.Contains("starts over from the beginning", msg);
    }

    /// <summary>A stale image's toast reads "updated", never "installed" — the old banner told the user
    /// to wait for a notice that could not appear for their case.</summary>
    [Fact]
    public void Does_not_promise_a_notice_that_cannot_appear()
    {
        var msg = Message(new SandboxImagePreflightProblem("mainguard-agent-base:latest", Stale: true));

        Assert.DoesNotContain("'Sandbox images installed' notice", msg);
        Assert.Contains("installed/updated", msg);
    }

    [Fact]
    public void Points_at_the_repairs_that_actually_run()
    {
        var msg = Message(new SandboxImagePreflightProblem("mainguard-agent-base:latest", Stale: true));

        Assert.Contains("Tools → Rebuild sandbox images", msg);
        Assert.Contains("oobe.log", msg);
    }
}

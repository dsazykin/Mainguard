using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The two ref-watcher subscribers — the automatic verification trigger and the branch-tip invalidator —
/// are actually RUNNING in the daemon, over the daemon's own ref watcher.
///
/// <para>The trigger has no RPC, no client and no other consumer: a DI singleton nobody resolves is never
/// constructed, so it would never subscribe and never sweep, and every unit test of its rules would still
/// be green. That is the same shape as MG-10's empty registry and as the verification path that had no
/// caller at all — the defect this change exists to close, one level up. So the composition root is asserted
/// here rather than trusted.</para>
///
/// <para>Two facts, and they are not the same fact: the trigger is constructed at boot (a hosted service
/// resolves it), and it observes <b>the very watcher instance</b> the daemon's worktree manager sweeps with.
/// A trigger subscribed to a second, unwatched <see cref="AgentRefWatcher"/> would pass every other test in
/// this repository and never fire once in production.</para>
/// </summary>
public sealed class WorkerReadinessTriggerWiringTests
{
    [Fact]
    public void TheDaemonBuildsTheTrigger_OverItsOwnRefWatcher()
    {
        using var vmRoot = new TempVmRoot();
        using var host = NewHost(vmRoot.Path);

        var trigger = host.Services.GetService<WorkerReadinessTrigger>();
        Assert.NotNull(trigger);

        var worktrees = Assert.IsType<WorktreeManager>(
            host.Services.GetRequiredService<IAgentEnvironment>().Worktrees);
        Assert.Same(worktrees.RefWatcher, trigger!.Source);
    }

    /// <summary>
    /// …and something resolves it at boot. Without this slot the registration above is a factory nobody
    /// calls: <c>Assert.NotNull(GetService&lt;WorkerReadinessTrigger&gt;())</c> would still pass — the test
    /// itself would be the only thing that ever constructed it.
    /// </summary>
    [Fact]
    public void AHostedServiceResolvesTheTriggerAtBoot()
    {
        using var vmRoot = new TempVmRoot();
        using var host = NewHost(vmRoot.Path);

        Assert.Contains(
            host.Services.GetServices<IHostedService>(),
            s => s is Mainguard.Server.Runtime.WorkerReadinessHostedService);
    }

    /// <summary>
    /// The OTHER subscriber on that same watcher, asserted for the same reason and against the same
    /// instance: <see cref="BranchTipInvalidator"/> is what walks a <c>Verified</c> entry back to
    /// <c>Working</c> when its branch moves past the tip it was verified on.
    ///
    /// <para>It is a stricter requirement than the trigger's, not a parallel one. A trigger that is
    /// registered and never resolved costs automation — a human can still press Verify. An invalidator
    /// that is registered and never resolved costs correctness: the entry stays green, the cockpit says
    /// "ready to merge", and Merge stays enabled for a tip nothing ever ran against. That is the defect
    /// this type exists to end, and the way it comes back is silently, in this file's absence.</para>
    /// </summary>
    [Fact]
    public void TheDaemonBuildsTheBranchTipInvalidator_OverThatSameRefWatcher()
    {
        using var vmRoot = new TempVmRoot();
        using var host = NewHost(vmRoot.Path);

        var invalidator = host.Services.GetService<BranchTipInvalidator>();
        Assert.NotNull(invalidator);

        var worktrees = Assert.IsType<WorktreeManager>(
            host.Services.GetRequiredService<IAgentEnvironment>().Worktrees);
        Assert.Same(worktrees.RefWatcher, invalidator!.Source);

        // …and the same watcher the trigger rides, which is what makes "one observation, two listeners"
        // a fact rather than a diagram.
        Assert.Same(
            host.Services.GetRequiredService<WorkerReadinessTrigger>().Source, invalidator.Source);
    }

    /// <summary>
    /// …and the daemon really CONSTRUCTED both of them at boot, asserted without this test resolving
    /// either one first.
    ///
    /// <para>This is the assertion the two tests above structurally cannot make. A DI singleton is built on
    /// first resolve, so <c>Assert.NotNull(GetService&lt;T&gt;())</c> passes even when the hosted service
    /// that was supposed to construct it does not — the test itself becomes the only thing that ever built
    /// the object, and "registered, never running" goes green. Mutating
    /// <c>WorkerReadinessHostedService</c> to resolve nothing left every other test in this file passing.
    /// </para>
    ///
    /// <para>So the evidence is the WATCHER's subscriber list, read straight off the event's backing field
    /// before anything here asks the container for a trigger or an invalidator. Both types subscribe in
    /// their constructors — that is the whole reason they do — so a subscriber of each type on the daemon's
    /// own watcher is proof that boot built them. Reaching for a private field is deliberate and is cheaper
    /// than the alternative: the only other honest evidence is a real ref advance, which needs a provisioned
    /// repo and an agent worktree, and this file's job is the composition root rather than the behaviour.</para>
    /// </summary>
    [Fact]
    public void BothSubscribersAreConstructedAtBoot_NotMerelyRegistered()
    {
        using var vmRoot = new TempVmRoot();
        using var host = NewHost(vmRoot.Path);

        var worktrees = Assert.IsType<WorktreeManager>(
            host.Services.GetRequiredService<IAgentEnvironment>().Worktrees);
        var subscribers = AdvancedSubscribers(worktrees.RefWatcher);

        Assert.Contains(subscribers, s => s is WorkerReadinessTrigger);
        Assert.Contains(subscribers, s => s is BranchTipInvalidator);
    }

    /// <summary>The objects currently subscribed to <c>AgentRefWatcher.Advanced</c>.</summary>
    private static object[] AdvancedSubscribers(AgentRefWatcher watcher)
    {
        var field = typeof(AgentRefWatcher).GetField(
            "Advanced",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "AgentRefWatcher.Advanced has no backing field — this assertion needs rewriting, not deleting.");

        return field.GetValue(watcher) is not Delegate handler
            ? Array.Empty<object>()
            : handler.GetInvocationList()
                .Select(d => d.Target!)
                .Where(t => t is not null)
                .ToArray();
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> NewHost(string vmRoot)
        => new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(new Wsl2AgentEnvironment(vmRoot: vmRoot))));

    private sealed class TempVmRoot : IDisposable
    {
        public TempVmRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mg-trigger-wiring-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Mainguard.Agents.UI.Services;
using Xunit;
using Proto = Mainguard.Protos.V1;

namespace Mainguard.Tests;

/// <summary>
/// The repo-open provisioning path used to fail SILENTLY: a throwaway client with a 5-second budget
/// whose every fault was swallowed to null, so agents simply looked dead on any repo whose bare
/// clone outlived 5 seconds — and, worse, a failed provision left the orchestrator pointed at the
/// PREVIOUSLY opened repo, so the merge rail showed (and a Merge would have fast-forwarded) the
/// wrong repository. These tests pin the honest replacements: a failure comes back as a REASON the
/// shell surfaces, and switching repos detaches the queue projection before provisioning.
/// </summary>
public sealed class RepoProvisioningHonestyTests
{
    // Never contacted for the projection-level tests; connection-refused fast for the failure test.
    private static DaemonClient UnreachableClient() =>
        new(() => GrpcChannel.ForAddress("http://127.0.0.1:1"), () => "token");

    private static Proto.QueueUpdate OneEntryUpdate(string agentId)
    {
        var update = new Proto.QueueUpdate { MainSha = "abc123" };
        update.Entries.Add(new Proto.QueueEntry
        {
            AgentId = agentId,
            State = "Working",
            CanMerge = false,
            GateReason = "not verified",
        });
        return update;
    }

    [Fact]
    public void ClearActiveRepo_EmptiesTheQueueProjection_AndRaisesChanged()
    {
        using var orchestrator = new DaemonBackedOrchestrator(UnreachableClient());
        orchestrator.ApplyQueueUpdate(OneEntryUpdate("agent-a"));
        Assert.Single(orchestrator.GetQueue());

        var changedRaised = false;
        orchestrator.Changed += () => changedRaised = true;

        orchestrator.ClearActiveRepo();

        Assert.Empty(orchestrator.GetQueue());
        Assert.True(changedRaised, "the rail redraws off Changed — clearing silently would leave stale rows painted");
    }

    [Fact]
    public async Task ClearActiveRepo_MakesTheAdapterRefuseToMerge_RatherThanTouchTheOldCheckout()
    {
        using var orchestrator = new DaemonBackedOrchestrator(UnreachableClient());
        orchestrator.SetActiveRepo("handle-a", localRepoPath: "/tmp/repo-a", syncRemoteName: "mainguard-local");
        orchestrator.ClearActiveRepo();

        // The merge precondition is the repo binding; after a clear it must be gone. ConfirmMergeAsync's
        // first refusal is "no repository is active for agents" — assert via the public surface.
        var ex = await Record.ExceptionAsync(() => orchestrator.ConfirmMergeAsync("agent-a"));
        Assert.NotNull(ex);
        Assert.Contains("no repository is active", ex!.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unreachable daemon comes back as a REASON (the shell toasts it + offers retry),
    /// never as the old swallowed null that made the platform look dead with no explanation.</summary>
    [Fact]
    public async Task ProvisionFailure_SurfacesAReason_NotASilentNull()
    {
        // The VM owns (and disposes) the orchestrator through the services bundle — no `using` here.
        var orchestrator = new DaemonBackedOrchestrator(UnreachableClient());
        var services = Mainguard.Agents.Agents.OrchestratorServices.FromSingle(orchestrator);
        using var vm = new Mainguard.Agents.UI.ViewModels.ControlCenterViewModel(services);

        var outcome = await ((Mainguard.UI.Editions.IAgentPlatformSurface)vm)
            .ProvisionRepoAsync("/tmp/nonexistent-repo");

        Assert.NotNull(outcome);
        Assert.Null(outcome!.Binding);
        Assert.False(string.IsNullOrWhiteSpace(outcome.FailureReason));
    }

    /// <summary>The primitive a redundant re-open (repo picker double-click, command palette's "Open
    /// &lt;repo&gt;", "Reopen Last Repository?") skips a needless clear+reprovision round trip on top of
    /// — found live 2026-08-20 (ISSUES-LOG #11): re-opening the already-active repo unconditionally tore
    /// down a working queue pump and re-provisioned from scratch, with no UI indication a background
    /// reprovision was in flight, blanking the rail to "Nothing queued" for as long as that RPC took
    /// (up to its 5-minute deadline).</summary>
    [Fact]
    public void IsBoundTo_TrueOnlyWhileThatHandlesQueuePumpIsAlive()
    {
        using var orchestrator = new DaemonBackedOrchestrator(UnreachableClient());

        Assert.False(orchestrator.IsBoundTo("handle-a"), "nothing bound yet");

        orchestrator.SetActiveRepo("handle-a", localRepoPath: "/tmp/repo-a", syncRemoteName: "mainguard-local");
        Assert.True(orchestrator.IsBoundTo("handle-a"));
        Assert.False(orchestrator.IsBoundTo("handle-b"), "a different handle must never read as bound");

        orchestrator.ClearActiveRepo();
        Assert.False(orchestrator.IsBoundTo("handle-a"), "a cleared adapter is bound to nothing");
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER — the ISSUES-LOG #11 regression proper.
    ///
    /// <para>The shell fires the repo-open provisioning path fire-and-forget, so two opens in quick
    /// succession used to run CONCURRENTLY. Both read <c>_lastProvisioned</c> before either wrote it, so
    /// the same-repo short-circuit could not fire for either, and one caller's <c>ClearActiveRepo</c>
    /// landed AFTER the other had already bound — killing a live queue pump that nothing then restarted
    /// (the daemon log's signature: a StreamQueue opened and cancelled 32 ms later, and never retried).
    /// With the calls serialized, the second one sees the first's finished binding and takes the no-op
    /// path, so the adapter is still bound when both have returned.</para>
    /// </summary>
    [Fact]
    public async Task ConcurrentRepoOpens_AreSerialized_SoTheSecondSeesTheFirstsBinding()
    {
        // The VM owns (and disposes) the orchestrator through the services bundle — no `using` here.
        var orchestrator = new DaemonBackedOrchestrator(UnreachableClient());
        var services = Mainguard.Agents.Agents.OrchestratorServices.FromSingle(orchestrator);
        using var vm = new Mainguard.Agents.UI.ViewModels.ControlCenterViewModel(services);
        var surface = (Mainguard.UI.Editions.IAgentPlatformSurface)vm;

        // The repo has been provisioned before but its pump is down — the state #11's session was found in.
        vm.SeedLastProvisionedForTest("handle-a", "/tmp/repo-a", "mainguard-local", "/mirror/handle-a.git");
        Assert.False(orchestrator.IsBoundTo("handle-a"));

        // Two opens of that SAME repo race, exactly as the shell's fire-and-forget
        // `_ = TryRegisterSyncRemoteAsync(...)` lets them.
        var outcomes = await Task.WhenAll(
            Task.Run(() => surface.ProvisionRepoAsync("/tmp/repo-a")),
            Task.Run(() => surface.ProvisionRepoAsync("/tmp/repo-a")));

        // Serialized, this is deterministic regardless of which task wins the gate: the first takes the
        // re-provision path (and fails, unreachable daemon) while restoring the binding, and the second
        // then finds a bound adapter and takes the short-circuit. Concurrent, BOTH see an unbound adapter,
        // BOTH clear, and both come back as failures — which is the interleave that stranded the pump.
        Assert.Equal(1, outcomes.Count(o => o!.FailureReason is not null));
        Assert.Equal(1, outcomes.Count(o => o!.Binding is not null));

        Assert.True(
            orchestrator.IsBoundTo("handle-a"),
            "two concurrent re-opens of the already-open repo detached the queue pump and never rebound it — "
            + "the rail reads 'Nothing queued' for the rest of the session with no error anywhere");
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER. A failed RE-provision of the repo that is already open must not be
    /// worse than not having tried: the clear has already torn the pump down and nothing downstream ever
    /// calls SetActiveRepo again on this path, so a transient daemon hiccup permanently blanked a working
    /// merge queue (restart-only recovery — ISSUES-LOG #11's confirmed practical impact).
    /// </summary>
    [Fact]
    public async Task FailedReprovisionOfTheSameRepo_RestoresTheBinding_RatherThanStrandingTheQueue()
    {
        var orchestrator = new DaemonBackedOrchestrator(UnreachableClient());
        var services = Mainguard.Agents.Agents.OrchestratorServices.FromSingle(orchestrator);
        using var vm = new Mainguard.Agents.UI.ViewModels.ControlCenterViewModel(services);

        // Bound and pumping, but IsBoundTo is deliberately made false first (the pump already died) so the
        // short-circuit cannot mask the failure path this test is about.
        vm.SeedLastProvisionedForTest("handle-a", "/tmp/repo-a", "mainguard-local", "/mirror/handle-a.git");
        Assert.False(orchestrator.IsBoundTo("handle-a"), "no pump yet — the re-provision path must be taken");

        var outcome = await ((Mainguard.UI.Editions.IAgentPlatformSurface)vm)
            .ProvisionRepoAsync("/tmp/repo-a"); // unreachable daemon ⇒ the provision throws

        Assert.NotNull(outcome!.FailureReason);
        Assert.True(
            orchestrator.IsBoundTo("handle-a"),
            "a failed re-provision of the ALREADY-OPEN repo left the adapter unbound, so the queue pump "
            + "was never restarted and its reconnect-forever loop never got a chance to run");
    }

    /// <summary>Switching to a repo whose provision fails leaves the queue EMPTY — not repo A's rows
    /// under repo B's window. This is the B4 regression.</summary>
    [Fact]
    public async Task FailedProvisionOnSwitch_LeavesNoStaleQueueFromThePreviousRepo()
    {
        // The VM owns (and disposes) the orchestrator through the services bundle — no `using` here.
        var orchestrator = new DaemonBackedOrchestrator(UnreachableClient());
        var services = Mainguard.Agents.Agents.OrchestratorServices.FromSingle(orchestrator);
        using var vm = new Mainguard.Agents.UI.ViewModels.ControlCenterViewModel(services);

        // Repo A was active with a populated queue.
        orchestrator.SetActiveRepo("handle-a", "/tmp/repo-a", "mainguard-local");
        orchestrator.ApplyQueueUpdate(OneEntryUpdate("agent-of-repo-a"));
        Assert.Single(orchestrator.GetQueue());

        // Opening repo B fails to provision (unreachable daemon).
        var outcome = await ((Mainguard.UI.Editions.IAgentPlatformSurface)vm)
            .ProvisionRepoAsync("/tmp/repo-b");

        Assert.NotNull(outcome!.FailureReason);
        Assert.Empty(orchestrator.GetQueue());
    }
}

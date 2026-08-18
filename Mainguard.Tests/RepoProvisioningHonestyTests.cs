using System;
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

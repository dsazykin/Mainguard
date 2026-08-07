using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The regression suite for the defect that <b>nothing in the product could trigger a verification</b>.
///
/// <para>Every piece of the mechanism existed and was tested — the <c>MergeQueue</c> state machine, the
/// <c>RunVerification</c> RPC, <c>DaemonClient.RunVerificationAsync</c> — but no production code path
/// reached any of it, so a queued entry sat at <c>not verified yet</c> forever. The existing tests all
/// passed because they called <c>MergeQueue.RunVerificationAsync</c> directly, stepping over the exact
/// rung that was missing.</para>
///
/// <para><b>So these tests never call that method.</b> They press the button: they invoke the
/// <see cref="QueueEntryViewModel.VerifyCommand"/> that the Verify control on the merge-queue rail is
/// bound to, and assert the verification actually ran on the daemon. The whole chain is real — VM command
/// → <see cref="IMergeQueueService"/> → the shipped <see cref="DaemonBackedOrchestrator"/> → the shipped
/// <see cref="DaemonClient"/> → the in-proc daemon's <c>RunVerification</c> RPC → the real
/// <see cref="MergeQueue"/>. If any link is missing, these go red.</para>
/// </summary>
public sealed class VerificationTriggerTests
{
    private const string RepoHandle = "repo-handle-verify";
    private const string MainSha = "main-000042";
    private const string AgentId = "loom-verify-1";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The decisive test: a human pressing Verify on a queued entry takes it from "not verified yet" to
    /// Verified, and the verification really executed through the daemon's runner.
    /// </summary>
    [Fact]
    public async Task VerifyCommand_OnQueueRow_RunsVerificationOnTheDaemon_AndClearsNotVerifiedYet()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token; // force a single synchronous host build before the pumps race on it
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        // The daemon's verification runner. In production this launches the repo's test command inside
        // the AGENT'S OWN JAIL and reads the container-observed exit code; here it records that it was
        // reached, which is the fact under test — the trigger has to arrive at the real runner.
        var runsRequested = 0;
        var queue = new MergeQueue(
            repoHash: "verify-h1",
            currentMainSha: MainSha,
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            runVerification: (agentId, ct) =>
            {
                Interlocked.Increment(ref runsRequested);
                return Task.FromResult(new Mainguard.Agents.Agents.Orchestrator.VerificationRecord(
                    agentId, MainSha, Passed: true, LogArtifactPath: "",
                    ResolvedCommand: "dotnet test", ConfigHash: "cfg", When: DateTimeOffset.UtcNow));
            });

        // A queued entry that has never been verified — exactly the owner's situation.
        queue.EnsureEntry(AgentId, MergeEntryOrigin.Local);
        Assert.Equal(WorkerMergeState.Working, queue.GetState(AgentId));

        var registry = (MergeQueueRegistry)daemon.Services.GetRequiredService<IMergeQueueRegistry>();
        registry.Register(RepoHandle, new MergeQueueContext(
            queue, daemon.Services.GetRequiredService<IMergeLeaseStore>()));
        adapter.SetActiveRepo(RepoHandle);

        Assert.True(
            await WaitUntilAsync(() => adapter.GetQueue().Any(e => e.AgentId == AgentId)),
            "the adapter never projected the queued entry — the test cannot press a button that isn't there");

        // The rail the shipped Control Center hosts, over the shipped adapter.
        var rail = new QueueRailViewModel(adapter, _ => { });
        var row = rail.Entries.Single(e => e.AgentId == AgentId);

        // Precondition: this is the stuck state the owner reported.
        Assert.False(adapter.CanMerge(AgentId, out var before));
        Assert.Equal("not verified yet", before);
        Assert.True(row.CanVerify, "the queue row offered no way to ask for a verification");

        // ---- the trigger under test: the button, and nothing but the button ----
        await row.VerifyCommand.ExecuteAsync(null);

        // The verification really ran, on the daemon, through its own runner.
        Assert.Equal(1, Volatile.Read(ref runsRequested));
        Assert.Equal(WorkerMergeState.Verified, queue.GetState(AgentId));

        // ...and the gate that was blocking now agrees, without having been weakened.
        Assert.True(queue.CanMerge(AgentId, out var after), $"still gated after a passing verification: {after}");
        Assert.Contains("main@", row.VerifyMessage);
    }

    /// <summary>
    /// The trigger must not become a second merge path. A passing verification moves the branch to
    /// Verified and stops there — the human merge is still a separate, gated action.
    /// </summary>
    [Fact]
    public async Task VerifyCommand_DoesNotMerge_AndLeavesTheFlaggedChangeGateStanding()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token;
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        // A gate that always refuses — standing in for the flagged-change / changed-test-command gates.
        var queue = new MergeQueue(
            repoHash: "verify-h2",
            currentMainSha: MainSha,
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            runVerification: (agentId, ct) => Task.FromResult(new Mainguard.Agents.Agents.Orchestrator.VerificationRecord(
                agentId, MainSha, Passed: true, LogArtifactPath: "",
                ResolvedCommand: "dotnet test", ConfigHash: "cfg", When: DateTimeOffset.UtcNow)),
            gates: new IMergeGate[] { new AlwaysRefusingGate() });

        queue.EnsureEntry(AgentId, MergeEntryOrigin.Local);
        var registry = (MergeQueueRegistry)daemon.Services.GetRequiredService<IMergeQueueRegistry>();
        registry.Register(RepoHandle, new MergeQueueContext(
            queue, daemon.Services.GetRequiredService<IMergeLeaseStore>()));
        adapter.SetActiveRepo(RepoHandle);

        Assert.True(await WaitUntilAsync(() => adapter.GetQueue().Any(e => e.AgentId == AgentId)));

        var rail = new QueueRailViewModel(adapter, _ => { });
        await rail.Entries.Single(e => e.AgentId == AgentId).VerifyCommand.ExecuteAsync(null);

        // Verified — but NOT merged, and the gate still refuses.
        Assert.Equal(WorkerMergeState.Verified, queue.GetState(AgentId));
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState(AgentId));
        Assert.False(queue.CanMerge(AgentId, out var reason), "the verification trigger weakened a merge gate");
        Assert.Equal("a gate says no", reason);
    }

    /// <summary>
    /// A verification the daemon refuses to even start — no live jail, no configured test command —
    /// must surface as a quotable refusal, not a silent no-op that leaves the row looking untouched.
    /// That silence is what made the original defect invisible.
    /// </summary>
    [Fact]
    public async Task VerifyCommand_WhenTheDaemonRefuses_SurfacesTheReason_AndDoesNotVerify()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token;
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        var queue = new MergeQueue(
            repoHash: "verify-h3",
            currentMainSha: MainSha,
            store: new InMemoryMergeQueueStore(),
            verifications: new InMemoryVerificationStore(),
            runVerification: (agentId, ct) => throw new NoVerificationCommandException(
                "this repository configures no verification command"));

        queue.EnsureEntry(AgentId, MergeEntryOrigin.Local);
        var registry = (MergeQueueRegistry)daemon.Services.GetRequiredService<IMergeQueueRegistry>();
        registry.Register(RepoHandle, new MergeQueueContext(
            queue, daemon.Services.GetRequiredService<IMergeLeaseStore>()));
        adapter.SetActiveRepo(RepoHandle);

        Assert.True(await WaitUntilAsync(() => adapter.GetQueue().Any(e => e.AgentId == AgentId)));

        var rail = new QueueRailViewModel(adapter, _ => { });
        var row = rail.Entries.Single(e => e.AgentId == AgentId);
        await row.VerifyCommand.ExecuteAsync(null);

        Assert.Contains("no verification command", row.VerifyMessage);
        Assert.StartsWith("Can't verify", row.VerifyMessage);
        // A refused run surfaces the branch back to Working — never a phantom Verified.
        Assert.Equal(WorkerMergeState.Working, queue.GetState(AgentId));

        // ...and the button is still pressable. A refusal that left the control disabled would strand
        // the human with no way forward — the same dead end this whole control exists to remove.
        Assert.True(row.CanVerify, "a refused verification left the Verify button permanently disabled");
    }

    /// <summary>
    /// The re-arm case with no daemon round-trip at all: the request is refused locally, moves no queue
    /// state, and therefore produces NO stream update to re-enable the button. It must re-arm itself.
    /// </summary>
    [Fact]
    public async Task VerifyCommand_WithNoActiveRepo_LeavesTheButtonPressable()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token;
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        // Never SetActiveRepo — the adapter refuses without ever reaching the daemon.
        var row = new QueueEntryViewModel("orphan", _ => { }, adapter);
        Assert.True(row.CanVerify);

        await row.VerifyCommand.ExecuteAsync(null);

        Assert.Contains("no repository is active", row.VerifyMessage);
        Assert.False(row.IsVerifyRequestInFlight);
        Assert.True(row.CanVerify, "the Verify button never re-armed after a local refusal");
    }

    /// <summary>
    /// With no repo bound, the button explains itself instead of throwing into the UI thread.
    /// </summary>
    [Fact]
    public async Task RunVerification_WithNoActiveRepo_RefusesWithAReason()
    {
        using var daemon = new DaemonFixture();
        _ = daemon.Token;
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        var outcome = await ((IMergeQueueService)adapter).RunVerificationAsync(AgentId);

        Assert.False(outcome.Ran);
        Assert.False(outcome.Passed);
        Assert.Contains("no repository is active", outcome.Reason);
    }

    private sealed class AlwaysRefusingGate : IMergeGate
    {
        public bool Allows(string agentId, out string reason)
        {
            reason = "a gate says no";
            return false;
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }

        return predicate();
    }
}

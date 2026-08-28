using System;
using System.Linq;
using Mainguard.Agents.Agents.Ipc;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The instructions a jailed CLI is handed at spawn, held to the surface the daemon actually serves.
///
/// <para>Phase 3 §1.2 found the coordinator's boundary text was never delivered. Delivering it creates a
/// second failure mode immediately: text that describes a surface the daemon no longer has. That is the
/// MG-12 shape this codebase keeps re-finding — a description outliving the thing it described — and it
/// is worse than silence here, because a CLI told to run a command that does not exist will burn its
/// turns discovering that. So the coordinator text is pinned against
/// <see cref="AgentIpcRequest.CoordinatorOps"/> in both directions.</para>
/// </summary>
public class AgentOperatingInstructionsTests
{
    /// <summary>Stand-ins for "what this daemon has installed" — deliberately not the shipped ids, so a
    /// test that passes only because it recognised a real adapter name cannot exist.</summary>
    private static readonly string[] Kinds = { "alpha-cli", "beta-cli" };

    private static string Coordinator() =>
        AgentOperatingInstructions.For(
            AgentIpcEndpointRole.Coordinator,
            AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Coordinator),
            Kinds);

    private static string Worker() =>
        AgentOperatingInstructions.For(
            AgentIpcEndpointRole.Worker,
            AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Worker));

    /// <summary>
    /// Every op the daemon serves a coordinator is named in what the coordinator is told. A tool the
    /// contract grants but the instructions omit is a capability the agent will never use — the role
    /// lock's own §7 question ("is the four-tool surface sufficient?") answered accidentally in the
    /// negative by an editing slip.
    /// </summary>
    [Fact]
    public void TheCoordinatorIsToldAboutEveryOpTheDaemonServesIt()
    {
        var text = Coordinator();
        foreach (var op in AgentIpcRequest.CoordinatorOps)
        {
            Assert.True(
                text.Contains($" {op} ", StringComparison.Ordinal) || text.Contains($" {op}\n", StringComparison.Ordinal),
                $"the coordinator is served '{op}' but its instructions never mention it — it will never use "
                + "a tool it was not told about. See AgentOperatingInstructions.");
        }
    }

    /// <summary>
    /// And nothing else. An instruction naming a command the daemon refuses sends the agent to spend
    /// turns on a wall, then improvise around it — which is exactly the behaviour the role lock exists
    /// to remove.
    /// </summary>
    [Fact]
    public void TheCoordinatorIsNotToldAboutAnythingTheDaemonRefuses()
    {
        var text = Coordinator();
        var served = AgentIpcRequest.CoordinatorOps;

        foreach (var workerOnly in new[]
                 {
                     AgentIpcRequest.PresentPlanOp,
                     AgentIpcRequest.RevisePlanOp,
                     AgentIpcRequest.AwaitDecisionOp,
                 })
        {
            if (served.Contains(workerOnly))
            {
                continue; // the contract grew; the positive test above covers it
            }

            Assert.DoesNotContain($"mainguard-agent {workerOnly}", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The one sentence a worker cannot be allowed to miss. A worker that starts guessing at work before
    /// approval is precisely what the plan gate was built to make impossible — and it would be refused,
    /// so the only thing it can produce is wasted budget and a confusing transcript.
    /// </summary>
    [Fact]
    public void TheWorkerIsToldThatNoTaskExistsUntilItsPlanIsApproved()
    {
        // Whitespace-normalised: these assertions are about what the worker is told, not about where the
        // prose happens to wrap. Matching the raw literal makes a reflow look like a policy change.
        var text = Flatten(Worker());

        Assert.Contains("do not start work until", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("withholds", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AgentIpcPaths.PlanShimFileName, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other sentence a worker cannot be allowed to miss — the one that was missing. In the first
    /// end-to-end run a worker did the approved work and stopped with an UNCOMMITTED diff; its worktree
    /// went with the jail, its branch carried no commit, and the readiness trigger — which fires on that
    /// branch advancing and then going quiet — had nothing to observe. These instructions had never
    /// mentioned committing at all.
    /// </summary>
    [Fact]
    public void TheWorkerIsToldToCommit_AndThatUncommittedWorkIsLost()
    {
        var text = Flatten(Worker());

        // The command, spelled with the shim path this role's jail actually has.
        Assert.Contains(
            AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Worker) + " commit",
            text, StringComparison.Ordinal);

        // …and WHY, which is the half that makes an agent act on it rather than note it.
        Assert.Contains("lost", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A worker's plan file must not land in the tree its own commit records. That commit is
    /// <c>git add -A</c> over <c>/workspace</c>, and the shim takes a path — so an unqualified "write the
    /// plan to a JSON file" puts it in the working directory, which IS the repository.
    /// </summary>
    [Fact]
    public void TheWorkerIsToldToWriteItsPlanOutsideTheRepository()
    {
        var shim = AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Worker);

        Assert.Contains("/tmp/plan.json", Flatten(Worker()), StringComparison.Ordinal);
        Assert.Contains("/tmp/plan.json", Flatten(AgentKickoffPrompt.Worker(shim)), StringComparison.Ordinal);
    }

    private static string Flatten(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Each role is told about its OWN shim and not the other's. The shims are staged per-role for least
    /// privilege (one endpoint publishes exactly one shim), so naming the wrong one sends the agent to a
    /// file that is not in its jail.
    /// </summary>
    [Fact]
    public void EachRoleIsPointedAtTheShimItsJailActuallyHas()
    {
        Assert.Contains(AgentIpcPaths.SpawnShimFileName, Coordinator(), StringComparison.Ordinal);
        Assert.DoesNotContain(AgentIpcPaths.PlanShimFileName, Coordinator(), StringComparison.Ordinal);

        Assert.Contains(AgentIpcPaths.PlanShimFileName, Worker(), StringComparison.Ordinal);
        Assert.DoesNotContain(AgentIpcPaths.SpawnShimFileName, Worker(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The coordinator must be told it has no repository. Phase 3 removed the worktree; an agent that
    /// does not know that spends its turns looking for code, concludes the jail is broken, and says so
    /// to the operator.
    /// </summary>
    [Fact]
    public void TheCoordinatorIsToldItHasNoRepository()
    {
        var text = Coordinator();
        Assert.Contains("no repository", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deliberate", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Defect D1's other half.</b> A coordinator is told which agent kinds exist. It was not, and its
    /// natural first move — <c>spawn coder</c>, a plausible reading of <c>spawn &lt;agent-kind&gt;</c> —
    /// produced a jail with no CLI in it that reported success.
    ///
    /// <para>The list is rendered from what the daemon has installed, never written into the prose: a
    /// hardcoded list stops describing the machine the first time a CLI is added or removed, which is the
    /// MG-12 shape this file's header is already about.</para>
    /// </summary>
    [Fact]
    public void TheCoordinatorIsToldWhichAgentKindsExist()
    {
        var text = Coordinator();

        foreach (var kind in Kinds)
        {
            Assert.Contains(kind, text, StringComparison.Ordinal);
        }

        // And the list is the ONE the caller supplied — not a shipped set that happens to be around.
        Assert.DoesNotContain("claude-code", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The permissive box states itself rather than rendering an empty list. "One of: " followed by
    /// nothing reads to an agent as a broken prompt, which is the failure mode this whole file exists to
    /// avoid; a dev box with no CLIs installed is a real state, and spawns there are deliberately not
    /// refused.
    /// </summary>
    [Fact]
    public void ACoordinatorOnABoxWithNothingInstalled_IsToldThat_NotAnEmptyList()
    {
        var text = AgentOperatingInstructions.For(
            AgentIpcEndpointRole.Coordinator,
            AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Coordinator),
            Array.Empty<string>());

        Assert.Contains("none installed", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A worker has no <c>spawn</c>, so nothing about what is installed belongs in its text —
    /// and the kinds argument must not leak into it by being threaded through one shared renderer.</summary>
    [Fact]
    public void TheWorkerTextDoesNotDependOnWhatIsInstalled()
    {
        Assert.Equal(
            Worker(),
            AgentOperatingInstructions.For(
                AgentIpcEndpointRole.Worker,
                AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Worker),
                Kinds));
    }

    /// <summary>An unknown role gets the text that cannot start work without a human.</summary>
    [Fact]
    public void AnUnknownRoleFallsBackToTheWorkerText()
        => Assert.Equal(Worker(), AgentOperatingInstructions.For((AgentIpcEndpointRole)999, AgentIpcPaths.SandboxShimPath(AgentIpcEndpointRole.Worker)));
}

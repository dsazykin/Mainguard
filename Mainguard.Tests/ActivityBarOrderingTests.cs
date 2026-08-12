using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.ViewModels.Agents;
using Xunit;

namespace Mainguard.Tests;

// P2-13 test 2 (§5) / TI-P2-13.2: the rail agent list is LIFO — newest spawn first — and removing
// any agent leaves the relative order of the rest intact. Exercises the exact production ordering
// helper the rail uses (AgentListProjection), not a re-implementation.
public class ActivityBarOrderingTests
{
    private static AgentInfo Agent(string id, DateTimeOffset spawnedAt) =>
        new(id, id.ToUpperInvariant(), $"agent/{id}", AgentLifecycleState.Working, "working", spawnedAt);

    [Fact]
    public void ActivityBar_LifoOrdering()
    {
        var t0 = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var a = Agent("a", t0);
        var b = Agent("b", t0.AddSeconds(1));
        var c = Agent("c", t0.AddSeconds(2));

        // Spawned A, then B, then C → the rail shows C, B, A.
        var order = AgentListProjection.LifoOrder(new[] { a, b, c }).Select(x => x.AgentId).ToArray();
        Assert.Equal(new[] { "c", "b", "a" }, order);

        // Removing the middle agent keeps the remaining order (C then A).
        var afterRemoval = AgentListProjection.LifoOrder(new[] { a, c }).Select(x => x.AgentId).ToArray();
        Assert.Equal(new[] { "c", "a" }, afterRemoval);
    }

    [Fact]
    public void LifoOrdering_IsStableRegardlessOfInputOrder()
    {
        var t0 = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var a = Agent("a", t0);
        var b = Agent("b", t0.AddSeconds(1));
        var c = Agent("c", t0.AddSeconds(2));

        var fromScrambled = AgentListProjection.LifoOrder(new[] { b, a, c }).Select(x => x.AgentId);
        Assert.Equal(new[] { "c", "b", "a" }, fromScrambled);
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER. <c>OrderByDescending</c> is <i>stable</i>, not total: with equal
    /// spawn times it falls through to the INPUT order, and the rail's input is
    /// <c>IAgentService.ListAgents()</c> — dictionary enumeration order. Two agents spawned in the same
    /// tick could therefore swap places between two refreshes with no state change at all. The helper
    /// now tie-breaks on agent id, so the ordering depends on nothing but the elements.
    /// </summary>
    [Fact]
    public void LifoOrdering_SameSpawnTime_IsTotal_NotInputDependent()
    {
        var t0 = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var a = Agent("a", t0);
        var b = Agent("b", t0); // identical spawn instant — the tie the rail has to break

        Assert.Equal(
            AgentListProjection.LifoOrder(new[] { a, b }).Select(x => x.AgentId),
            AgentListProjection.LifoOrder(new[] { b, a }).Select(x => x.AgentId));
    }
}

/// <summary>
/// The claim on <see cref="AgentListProjection"/> — "both the live rail (<c>ControlCenterViewModel</c>)
/// and <c>ActivityBarOrderingTests</c> exercise the exact same ordering — the ordering is not
/// re-implemented in the test" — was false: the rail spelled
/// <c>OrderByDescending(a =&gt; a.SpawnedAt)</c> inline in three places and never called the helper. So the
/// suite above proved a property of a function the app did not use.
///
/// <para>This test closes that gap through the rail's own public projection, which is the only way the
/// delegation is observable: it feeds a tie the helper breaks and the old inline expression did not.</para>
/// </summary>
public class ActivityBarRailUsesProjectionTests
{
    /// <summary>FAILS BEFORE / PASSES AFTER — before, the rail echoed the fake's list order, because the
    /// inline expression's tie fell through to it.</summary>
    [AvaloniaFact]
    public void Rail_OrdersThroughTheSharedProjection_NotAnInlineReimplementation()
    {
        var t0 = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        // Same spawn instant, handed over in the order the helper does NOT produce.
        var agents = new[] { Agent("agent-a", t0), Agent("agent-b", t0) };

        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var fake = new FixedAgentList(agents);
        using var vm = new ControlCenterViewModel(
            new OrchestratorServices(fake, mock, mock, mock, mock, mock, Owner: null));

        Assert.Equal(
            AgentListProjection.LifoOrder(agents).Select(a => a.AgentId),
            vm.Agents.Select(r => r.AgentId));
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER, and this one is the user-visible half. Delegating to the helper
    /// surfaced that the rail's reconcile loop did <c>Agents.Insert(0, …)</c> per new row — correct only
    /// when rows arrive one at a time. Handed a BATCH it laid them down in reverse, so the rail rendered
    /// <b>oldest agent first</b>, the exact opposite of the LIFO it advertises. The batch case is not
    /// exotic: it is opening the surface with agents already running, and every re-subscribe after the
    /// agent-event stream drops.
    /// </summary>
    [AvaloniaFact]
    public void Rail_BulkSnapshot_RendersNewestFirst_NotReversed()
    {
        var t0 = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var agents = new[]
        {
            Agent("oldest", t0),
            Agent("middle", t0.AddSeconds(1)),
            Agent("newest", t0.AddSeconds(2)),
        };

        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = new ControlCenterViewModel(
            new OrchestratorServices(new FixedAgentList(agents), mock, mock, mock, mock, mock, Owner: null));

        Assert.Equal(new[] { "newest", "middle", "oldest" }, vm.Agents.Select(r => r.AgentId));
    }

    private static AgentInfo Agent(string id, DateTimeOffset spawnedAt) =>
        new(id, id.ToUpperInvariant(), $"agent/{id}", AgentLifecycleState.Working, "working", spawnedAt);

    /// <summary>An agent seam that hands back a FIXED list in a FIXED order, so the rail's ordering is
    /// the only variable in the assertion.</summary>
    private sealed class FixedAgentList : IAgentService
    {
        private readonly AgentInfo[] _agents;

        public FixedAgentList(AgentInfo[] agents) => _agents = agents;

        public IReadOnlyList<AgentInfo> ListAgents() => _agents;

        public event Action<AgentEvent>? EventReceived { add { } remove { } }

        public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;

        public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();

        public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;

        public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();

        public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId)
            => Array.Empty<(string, bool)>();

        public Task PauseAgentAsync(string agentId) => Task.CompletedTask;

        public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;

        public Task EndAgentAsync(string agentId) => Task.CompletedTask;
    }
}

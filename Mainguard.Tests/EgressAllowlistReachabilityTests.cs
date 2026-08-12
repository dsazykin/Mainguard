using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The egress allowlist editor shipped complete and unreachable: <c>EgressAllowlistView</c> was
/// constructed by a render harness and by nothing else, and the ONLY implementation of
/// <see cref="IEgressAllowlistGateway"/> was the hardcoded in-memory seed. So the sandbox egress policy
/// was enforced but neither inspectable nor editable, and an agent whose CLI the proxy killed had one
/// affordance — the block prompt's "Unblock and retry", which can add exactly the one host the detector
/// managed to parse out of a death message.
/// </summary>
public class EgressAllowlistReachabilityTests
{
    /// <summary>
    /// FAILS BEFORE / PASSES AFTER (it did not compile before — there was no async seam and no daemon
    /// gateway). The editor must reflect the gateway's live contents, not a construction-time snapshot
    /// of a list it happens to own.
    /// </summary>
    [Fact]
    public async Task Editor_ReadsTheGateway_AndWritesBackThroughIt()
    {
        var gateway = new RecordingGateway();
        var vm = new EgressAllowlistViewModel(gateway);

        // Before the load the grid is empty — the list is the gateway's, not the ViewModel's.
        Assert.Empty(vm.Entries);

        await vm.InitializeAsync();
        Assert.Contains(vm.Entries, e => e.HostPattern == "api.anthropic.com");

        vm.NewName = "Docs";
        vm.NewHostPattern = "docs.example.com";
        await vm.AddCommand.ExecuteAsync(null);

        Assert.Contains(("Docs", "docs.example.com", "Custom"), gateway.Added);
        Assert.Contains(vm.Entries, e => e.HostPattern == "docs.example.com");

        var row = vm.Entries.Single(e => e.HostPattern == "docs.example.com");
        await row.RemoveCommand.ExecuteAsync(null);

        Assert.Contains("docs.example.com", gateway.Removed);
        Assert.DoesNotContain(vm.Entries, e => e.HostPattern == "docs.example.com");
    }

    /// <summary>
    /// A daemon that cannot be reached must SAY so. An empty grid would read as "nothing is allowed",
    /// which is a very different and much more alarming claim than "we could not ask".
    /// </summary>
    [Fact]
    public async Task Editor_UnreachableDaemon_ReportsIt_RatherThanRenderingAnEmptyPolicy()
    {
        var vm = new EgressAllowlistViewModel(new ThrowingGateway());

        await vm.InitializeAsync();

        Assert.Empty(vm.Entries);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.Contains("daemon", vm.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER. The surface must offer a route to the editor at rest — before
    /// something has been blocked, not only after. Asserted on the command's existence on the live
    /// surface ViewModel, which is what the toolbar button binds to.
    /// </summary>
    [AvaloniaFact]
    public void ControlCenter_ExposesAStandingRouteToTheAllowlistEditor()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        using var vm = new ControlCenterViewModel(OrchestratorServices.FromSingle(mock));

        Assert.NotNull(vm.OpenEgressAllowlistCommand);
    }

    /// <summary>
    /// FAILS BEFORE / PASSES AFTER. The block prompt gets a third option, because Unblock can only ever
    /// add the single parsed host — a CLI that needs two, or a host the detector got slightly wrong,
    /// previously left the operator with nowhere to go.
    /// </summary>
    [Fact]
    public async Task BlockPrompt_OffersTheEditor_AndDismissesItselfWhenOpeningIt()
    {
        var dismissed = false;
        var opened = false;
        var prompt = new EgressBlockPromptViewModel(
            "api.example.com", "claude-code",
            unblock: _ => Task.CompletedTask,
            dismiss: () => dismissed = true,
            manageAllowlist: () => { opened = true; return Task.CompletedTask; });

        Assert.True(prompt.CanManageAllowlist);

        await prompt.ManageAllowlistCommand.ExecuteAsync(null);

        Assert.True(opened);
        Assert.True(dismissed, "the prompt stayed up behind the editor — two surfaces on one decision");
    }

    /// <summary>Without a manage callback the affordance is hidden rather than dead — the mock/design
    /// path has no editor to open.</summary>
    [Fact]
    public void BlockPrompt_WithoutAnEditor_HidesTheAffordance()
    {
        var prompt = new EgressBlockPromptViewModel(
            "api.example.com", "claude-code", _ => Task.CompletedTask, () => { });

        Assert.False(prompt.CanManageAllowlist);
    }

    /// <summary>
    /// The shipped gateway is the daemon one. Guards the regression that made this surface unreachable
    /// in the first place: a production path that resolves to the in-memory seed would render a
    /// convincing, editable allowlist that has nothing to do with what the proxy enforces.
    /// </summary>
    [Fact]
    public void DaemonBackedOrchestrator_HandsOutTheDaemonGateway_NotTheInMemorySeed()
    {
        var factory = typeof(DaemonBackedOrchestrator)
            .GetMethod(nameof(DaemonBackedOrchestrator.CreateEgressAllowlistGateway),
                BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(factory);
        Assert.Equal(typeof(IEgressAllowlistGateway), factory!.ReturnType);
    }

    private sealed class RecordingGateway : IEgressAllowlistGateway
    {
        private readonly List<EgressAllowlistItem> _items = new()
        {
            new EgressAllowlistItem("Anthropic API", "api.anthropic.com", "ModelApi", false),
        };

        public List<(string Name, string Host, string Kind)> Added { get; } = new();
        public List<string> Removed { get; } = new();

        public Task<IReadOnlyList<EgressAllowlistItem>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EgressAllowlistItem>>(_items.ToArray());

        public Task AddAsync(string name, string hostPattern, string kind, CancellationToken ct = default)
        {
            Added.Add((name, hostPattern, kind));
            _items.Add(new EgressAllowlistItem(name, hostPattern, kind, false));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string hostPattern, CancellationToken ct = default)
        {
            Removed.Add(hostPattern);
            _items.RemoveAll(i => i.HostPattern == hostPattern);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingGateway : IEgressAllowlistGateway
    {
        public Task<IReadOnlyList<EgressAllowlistItem>> ListAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("connection refused");

        public Task AddAsync(string name, string hostPattern, string kind, CancellationToken ct = default)
            => throw new InvalidOperationException("connection refused");

        public Task RemoveAsync(string hostPattern, CancellationToken ct = default)
            => throw new InvalidOperationException("connection refused");
    }
}

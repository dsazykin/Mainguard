using System;
using System.Linq;
using Avalonia.Headless.XUnit;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.Editions;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.Editions;
using Mainguard.App.Shell.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// FAILS BEFORE / PASSES AFTER — the section-rail half of "the coordinator stays highlighted when you
/// switch to an agent".
///
/// <para><c>ShowAgent</c> called <c>ActivateSection("Coordinator")</c>, which both routes the content
/// AND lights the matching row. Routing there is correct — an agent's workspace is hosted inside the
/// Coordinator section — but lighting it is not: the coordinator is not what the human is looking at,
/// and the agent's own row in the rail below is. The two were the same call, so they could not
/// disagree, and the highlight never moved.</para>
///
/// <para>The companion half (agent rows gaining a highlight at all) is
/// <see cref="AgentRailSelectionTests"/>.</para>
/// </summary>
public class MainWindowSectionHighlightTests
{
    /// <summary>
    /// Runs <paramref name="check"/> against a shell view model built under the PRO manifest. The
    /// Client manifest omits the Coordinator section entirely, so there is no row there to light or
    /// release and the defect cannot exist in that edition.
    /// </summary>
    private static void UnderPro(Action<MainWindowViewModel> check)
    {
        var originalEdition = Mainguard.App.Shell.App.Edition;
        var originalFactory = ProComposition.OrchestratorServicesFactory;
        try
        {
            ProComposition.OrchestratorServicesFactory =
                () => OrchestratorServices.FromSingle(new MockOrchestrator());
            Mainguard.App.Shell.App.Edition = new ProManifest();

            using var vm = new MainWindowViewModel(null);
            check(vm);
        }
        finally
        {
            ProComposition.OrchestratorServicesFactory = originalFactory;
            Mainguard.App.Shell.App.Edition = originalEdition;
        }
    }

    [AvaloniaFact]
    public void ShowingAnAgent_LeavesNoSectionRowLit() => UnderPro(vm =>
    {

        vm.ShowCoordinatorSectionCommand.Execute(null);
        Assert.True(Coordinator(vm).IsActive);

        vm.ShowAgentCommand.Execute("agent-a");

        // The row that used to stay lit.
        Assert.False(Coordinator(vm).IsActive);

        // And nothing else took the highlight instead — "none of these" is the honest state for the
        // section rail while an agent is being viewed.
        Assert.All(vm.RailSections, section => Assert.False(section.IsActive));
    });

    [AvaloniaFact]
    public void ShowingAnAgent_StillRoutesContentToTheCoordinatorSection() => UnderPro(vm =>
    {
        // The other half of the contract: not lighting the row must not break the routing that puts the
        // agent's workspace on screen at all.
        vm.ShowAgentCommand.Execute("agent-a");

        Assert.Equal("Coordinator", vm.SelectedSectionId);
    });

    [AvaloniaFact]
    public void GoingBackToTheCoordinator_LightsItAgain() => UnderPro(vm =>
    {
        vm.ShowAgentCommand.Execute("agent-a");
        vm.ShowCoordinatorSectionCommand.Execute(null);

        Assert.True(Coordinator(vm).IsActive);
    });

    private static RailSectionViewModel Coordinator(MainWindowViewModel vm) =>
        vm.RailSections.Single(s => s.Id == "Coordinator");
}

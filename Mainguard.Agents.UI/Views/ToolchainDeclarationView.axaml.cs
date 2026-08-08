using Avalonia.Controls;

namespace Mainguard.Agents.UI.Views;

/// <summary>Settings → Toolchains: the four-step "declare a toolchain in this repository" flow. The
/// initial measurement is kicked by <c>ToolchainSettingsViewModel.OnActivated</c> (which refreshes its
/// <c>Declaration</c> alongside the toolchain list), not here.</summary>
public partial class ToolchainDeclarationView : UserControl
{
    public ToolchainDeclarationView()
    {
        InitializeComponent();
    }
}

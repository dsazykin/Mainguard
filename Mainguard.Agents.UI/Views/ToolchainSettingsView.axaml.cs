using Avalonia.Controls;

namespace Mainguard.Agents.UI.Views;

/// <summary>Settings → Toolchains: the curated language-toolchain install/remove page. The initial
/// probe pass is kicked by <c>ToolchainSettingsViewModel.OnActivated</c> (ISettingsPage), not here.</summary>
public partial class ToolchainSettingsView : UserControl
{
    public ToolchainSettingsView()
    {
        InitializeComponent();
    }
}

using Avalonia.Controls;

namespace Mainguard.Agents.UI.Views;

/// <summary>
/// Settings → PR Intake. A <see cref="UserControl"/>, not a <see cref="Window"/>: it is a page in the
/// shell's Settings window, dropped into its content slot by <c>SettingsViewModel</c> and resolved by
/// <c>ViewLocator</c>, exactly like AI Providers / Agent CLIs / Toolchains / Daemon Logs.
///
/// <para>It was a top-level Window with a Close button, which is why it was unreachable: a Window can
/// only appear via <c>new</c> + <c>Show</c>, no such call existed anywhere in the app, and Avalonia
/// refuses to host a Window as a <c>ContentControl.Content</c> — so it could not have been dropped into
/// the Settings rail either. The codebase had already moved every other agent-platform settings surface
/// off dialogs and onto that rail; this one was left behind.</para>
/// </summary>
public partial class PrIntakeSettingsView : UserControl
{
    public PrIntakeSettingsView()
    {
        InitializeComponent();
    }
}

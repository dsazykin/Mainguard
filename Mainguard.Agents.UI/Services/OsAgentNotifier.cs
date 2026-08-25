using Mainguard.Agents.UI.Editions;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The shipped <see cref="IAgentNotifier"/>: a Notification Center banner on macOS (real OS
/// attribution only exists inside the .app bundle — an unbundled process has no notification
/// center and the call reports false), falling back to the shell toast everywhere else. The
/// fallback is also what harnesses hit, where no shell is present and the toast seam no-ops.
/// </summary>
public sealed class OsAgentNotifier : IAgentNotifier
{
    public void Notify(string title, string body)
    {
        if (Mainguard.UI.Platform.MacNative.TryPostNotification(title, body))
            return;

        ProComposition.ShowShellToast($"{title} — {body}", false);
    }
}

using System;
using System.IO;
using System.Text.Json;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// The macos-host first-run marker — the mac analogue of the WSL OOBE's staged state machine,
/// deliberately simpler: this substrate has no reboot-resume, no elevation and no VM import, so
/// "completed once" is the only stage worth persisting. Deleting the file re-runs the flow
/// (the documented reset, same spirit as re-running the Windows OOBE).
/// </summary>
public static class MacOobeState
{
    private static string MarkerPath() =>
        Path.Combine(Mainguard.Git.MainguardPaths.DataRoot(), "macos-oobe.json");

    public static bool IsCompleted()
    {
        try { return File.Exists(MarkerPath()); }
        catch { return false; }
    }

    public static void MarkCompleted()
    {
        var marker = MarkerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            completed = true,
            completedAtUtc = DateTime.UtcNow,
        }));
    }
}

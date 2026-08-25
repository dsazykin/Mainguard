using System;
using System.IO;

namespace Mainguard.Tests.TestTools;

/// <summary>
/// Builds the quoted command prefix that spawns an exe head COPIED into the test output
/// (Mainguard.Client.App next to the test assembly, via its ProjectReference).
///
/// On macOS the copied apphost must not be spawned directly: current macOS pins an
/// executable's identity to the location it first ran from and SIGKILLs a same-named
/// apphost at any other path ("died of signal 9" from git when the head runs as
/// GIT_SEQUENCE_EDITOR) — re-signing, fresh inodes, and byte-identical content all
/// stay killed, while `dotnet &lt;app&gt;.dll` through Apple's notarized host always runs.
/// Production is unaffected: the app re-invokes its OWN running path, which is by
/// definition executable. So: Windows/Linux prefer the apphost (absolute path, no PATH
/// dependency), macOS always takes the dotnet-host form.
/// </summary>
internal static class SelfInvocation
{
    internal static string ClientAppPrefix()
    {
        var baseDir = AppContext.BaseDirectory;
        var apphost = Path.Combine(baseDir,
            OperatingSystem.IsWindows() ? "Mainguard.Client.App.exe" : "Mainguard.Client.App");

        if (!OperatingSystem.IsMacOS() && File.Exists(apphost))
            return $"\"{apphost}\"";

        return $"\"dotnet\" \"{Path.Combine(baseDir, "Mainguard.Client.App.dll")}\"";
    }
}

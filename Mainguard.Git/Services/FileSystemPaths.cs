using System;

namespace Mainguard.Git.Services;

/// <summary>
/// The path-comparison policy for the filesystems Mainguard runs on. NTFS and APFS are
/// case-insensitive (case-preserving) by default, so two spellings of one path must compare
/// equal there; Linux filesystems are case-sensitive. Purely textual — callers that also need
/// symlink identity (macOS's /var → /private/var, any symlinked checkout) must compare within
/// one namespace or canonicalize first; no comparison mode can paper over a symlink.
/// </summary>
public static class FileSystemPaths
{
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

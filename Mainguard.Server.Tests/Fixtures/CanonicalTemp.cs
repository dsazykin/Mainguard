using System;
using System.IO;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// The canonical (symlink-resolved) temp root for paths that CROSS the container boundary.
/// macOS serves <see cref="Path.GetTempPath"/> from behind the <c>/var → /private/var</c>
/// symlink, and host git canonicalizes: a worktree's <c>.git</c> pointer and its alternates are
/// written in the <c>/private/var</c> spelling. Inside a jail only the MOUNTED spelling exists
/// (there is no <c>/var</c> symlink in the container), so a fixture that provisions its VM root
/// from the raw temp path hands in-jail git a dangling gitdir — "not a git repository" — while
/// the very same flow is green on Linux. Starting canonical keeps host git, the mount source,
/// the mount target and the in-jail metadata in ONE namespace. Production is untouched: the real
/// substrate root is <c>~/mainguard</c>, which no symlink serves.
/// </summary>
internal static class CanonicalTemp
{
    internal static string Root { get; } =
        OperatingSystem.IsMacOS()
            && Path.GetTempPath().StartsWith("/var/", StringComparison.Ordinal)
            && Directory.Exists("/private" + Path.GetTempPath())
        ? "/private" + Path.GetTempPath()
        : Path.GetTempPath();
}

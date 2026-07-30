using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mainguard.Git;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The teeth behind <see cref="TestDataRootIsolation"/>: proves this run is NOT pointed at the
/// developer's real <c>~/.mainguard</c> (<c>%LocalAppData%\Mainguard</c> on Windows), and — the part
/// specific to the daemon suite — that <see cref="DaemonHost"/>'s store-path fallbacks land inside the
/// sandbox rather than in the user's data root.
///
/// <para>Isolating the data root is invisible while it works, so it needs a test that fails the moment
/// the redirect stops happening rather than a comment asking people to remember. To confirm these
/// assertions have teeth, delete the <c>[ModuleInitializer]</c> body in
/// <see cref="TestDataRootIsolation"/> and run this class: every fact here goes red.</para>
/// </summary>
public class DataRootIsolationTests
{
    [Fact]
    public void Data_root_under_test_is_not_the_real_user_data_root() => AssertIsolated();

    /// <summary>
    /// The isolation assertion itself, factored out so anything that is about to TOUCH the data root can
    /// check first. Without that ordering a broken redirect would hang rather than fail: the daemon
    /// database is migrated on host startup and blocks on an orphaned <c>__EFMigrationsLock</c> row, and
    /// a hang reports nothing.
    /// </summary>
    private static void AssertIsolated()
    {
        var real = MainguardPaths.RealUserDataRoot();
        var underTest = MainguardPaths.DataRoot();

        Assert.True(Path.IsPathRooted(underTest), $"Data root must be absolute; got '{underTest}'.");
        Assert.False(
            PathsEqual(underTest, real),
            $"The daemon test run resolved Mainguard's data root to the REAL user root ('{real}'). Tests "
            + "must never read or write the developer's own daemon database, session token, mTLS identity "
            + "or agent-IPC sockets — see TestDataRootIsolation.");
        Assert.False(
            IsUnder(underTest, real),
            $"The test data root '{underTest}' sits INSIDE the real user root '{real}'. A subdirectory is "
            + "still the user's directory: it survives the run, and its SQLite lock is the same lock.");
    }

    [Fact]
    public void Data_root_under_test_comes_from_the_relocation_seam()
    {
        var configured = Environment.GetEnvironmentVariable(MainguardPaths.DataRootOverrideVariable);
        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{MainguardPaths.DataRootOverrideVariable} is unset, so MainguardPaths.DataRoot() falls back "
            + "to the real per-user root. TestDataRootIsolation's module initializer must set it.");
        Assert.Equal(Path.GetFullPath(configured!.Trim()), MainguardPaths.DataRoot());
    }

    /// <summary>
    /// The consumers that actually caused the damage. Every <c>DaemonHost.Resolve*</c> store path prefers
    /// sitting next to the (already temp-isolated) session token, and FALLS BACK to
    /// <see cref="MainguardPaths.DataRoot"/> when no token path is supplied. That fallback is what wrote
    /// the real <c>mainguard-daemon.db</c>, <c>mainguard-plans.json</c>, <c>mainguard-leader-sessions.json</c>,
    /// <c>logs/</c> and <c>agent-ipc/</c>, so it is exercised here with <c>tokenPath: null</c> — the exact
    /// branch — and required to land in the sandbox.
    /// </summary>
    [Fact]
    public void Daemon_store_path_fallbacks_land_inside_the_isolated_root()
    {
        AssertIsolated(); // never resolve against the real root, not even to fail
        var root = MainguardPaths.DataRoot();

        foreach (var (name, resolved) in ResolveEveryFallbackPath())
        {
            Assert.True(
                IsUnder(resolved, root),
                $"DaemonHost.{name} fell back to '{resolved}', outside the isolated root '{root}'. "
                + "That path is the developer's real data root.");
        }
    }

    /// <summary>
    /// Guards the reflection above against going vacuous. These resolvers are private, so a rename or a
    /// newly added store path would otherwise silently drop out of the loop and leave the test passing
    /// while asserting nothing about it. Pinning the expected set means a change to
    /// <see cref="DaemonHost"/>'s store paths fails HERE, with the new name in the message, and has to be
    /// looked at.
    /// </summary>
    [Fact]
    public void Every_daemon_store_path_resolver_is_covered_by_the_fallback_test()
    {
        var expected = new[]
        {
            "ResolveAgentIpcRoot",
            "ResolveDataPath",
            "ResolveLeaderRegistryPath",
            "ResolveLogsDirectory",
            "ResolvePlanStorePath",
        };

        Assert.Equal(expected, FallbackResolvers().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>Every private static <c>Resolve*</c> on <see cref="DaemonHost"/> that yields a path.</summary>
    private static IEnumerable<MethodInfo> FallbackResolvers()
        => typeof(DaemonHost)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Resolve", StringComparison.Ordinal) && m.ReturnType == typeof(string));

    /// <summary>
    /// Invokes each resolver on its no-token-path branch. Arguments are supplied by parameter type so the
    /// differing signatures need no per-method table; a <c>string</c> parameter is the token path (or the
    /// explicit override) and is deliberately null, which is what selects the fallback.
    /// </summary>
    private static IEnumerable<(string Name, string Resolved)> ResolveEveryFallbackPath()
    {
        var emptyConfig = new ConfigurationBuilder().Build();

        foreach (var method in FallbackResolvers())
        {
            var args = method.GetParameters().Select(p =>
                p.ParameterType == typeof(DaemonOptions) ? new DaemonOptions()
                : typeof(IConfiguration).IsAssignableFrom(p.ParameterType) ? emptyConfig
                : (object?)null).ToArray();

            var resolved = (string)method.Invoke(null, args)!;
            yield return (method.Name, resolved);
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), PathComparison);

    private static bool IsUnder(string candidate, string root)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison)
            || string.Equals(normalizedCandidate, normalizedRoot, PathComparison);
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

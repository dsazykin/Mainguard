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
    /// The isolated root must leave room for a socket to be BOUND inside it, not merely to exist.
    ///
    /// <para>Without this the budget holds by accident. The default temp root comes to 105 characters on
    /// Linux against a 108-byte <c>sun_path</c> — three characters of margin nobody chose — and on macOS
    /// the per-user <c>/var/folders/&lt;..&gt;/T/</c> prefix blows straight past 104, which took out every
    /// socket-binding test in this assembly at once. Neither failure names the data root: both surface as
    /// <c>ArgumentOutOfRangeException</c> from <c>UnixDomainSocketEndPoint</c>, far from the code that
    /// chose the path. Asserting the budget fails one test with the reason instead.</para>
    ///
    /// <para>This checks whatever root is actually in force, so a deliberately pinned
    /// <c>MAINGUARD_DATA_ROOT</c> is held to the same limit.</para>
    /// </summary>
    [Fact]
    public void The_isolated_root_leaves_room_to_bind_an_agent_ipc_socket()
    {
        var root = MainguardPaths.DataRoot();
        var longest = Path.Combine(root, "agent-ipc", new string('a', 12), "daemon.sock");
        var limit = TestDataRootIsolation.MaxUnixSocketPathLength;

        Assert.True(
            longest.Length <= limit,
            $"The data root '{root}' leaves no room to bind an agent-IPC socket: the longest path the "
            + $"daemon builds under it is {longest.Length} characters ('{longest}') and this platform's "
            + $"sun_path holds {limit}. Every socket-binding test in this assembly would fail with an "
            + "ArgumentOutOfRangeException that never mentions the root. See TestDataRootIsolation.");
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
    ///
    /// <para><b>This list is a tripwire, not a manifest, and editing it is the point.</b> Its whole value
    /// is that it stops someone — and it is supposed to keep stopping people. So when it trips, widen it
    /// only after satisfying
    /// <see cref="Every_daemon_store_path_resolver_follows_the_session_token"/> below, which states the
    /// property this list exists to protect and checks it against the SHIPPED set rather than against the
    /// literal here. A list that is widened on reflex eventually admits a resolver that does not isolate;
    /// the assertion below is what catches that one, whatever the literal says.</para>
    ///
    /// <para><b>Why the set last grew:</b> <c>ResolveKillJournalPath</c> was added for the durable
    /// <c>JsonKillJournal</c>. The P2-14 kill switch previously got no journal at all, so its
    /// constructor's <c>?? new InMemoryKillJournal()</c> built a sink nothing held a reference to and
    /// step 3's "snapshot written BEFORE returning" wrote into an object that died with the process —
    /// which is the one process an emergency stop is followed by restarting. Giving it a file made it a
    /// store, and every daemon store belongs under this test.</para>
    /// </summary>
    [Fact]
    public void Every_daemon_store_path_resolver_is_covered_by_the_fallback_test()
    {
        var expected = new[]
        {
            "ResolveAgentIpcRoot",
            "ResolveDataPath",
            "ResolveKillJournalPath",
            "ResolveLeaderRegistryPath",
            "ResolveLogsDirectory",
            "ResolvePlanStorePath",
        };

        Assert.Equal(expected, FallbackResolvers().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// The property the exact-set list above exists to protect, stated directly and checked against the
    /// SHIPPED resolvers — so it keeps holding after the next person edits that list.
    ///
    /// <para><b>The rule: every daemon store path FOLLOWS THE SESSION TOKEN.</b> Given a token path, the
    /// store must land beside that token; the data-root fallback
    /// (<see cref="Daemon_store_path_fallbacks_land_inside_the_isolated_root"/>) is only the second line
    /// of defence, for when there is no token at all. Following the token is the FIRST line, and it is
    /// the mechanism the whole in-proc tier rests on: <c>DaemonFixture</c> isolates a host by giving it
    /// its own temp token directory, and every store has to go there or the isolation is partial.</para>
    ///
    /// <para><b>And that is precisely what a widened list would let through.</b> A resolver that ignored
    /// its <c>tokenPath</c> and always returned the data-root path would satisfy the fallback test (it
    /// does land inside the isolated root) and satisfy an exact-set list somebody had just extended —
    /// while making every concurrent in-proc host share one file, and, outside the test module
    /// initializer's redirect, writing the developer's real store. Two facts are asserted because both
    /// are ways to fail it: a resolver must TAKE a token path, and it must USE it.</para>
    /// </summary>
    [Fact]
    public void Every_daemon_store_path_resolver_follows_the_session_token()
    {
        AssertIsolated(); // never resolve against the real root, not even to fail

        // A plausible per-host token directory. Kept under the isolated root and never created: these
        // resolvers are pure string composition, so nothing is written, and naming a path outside the
        // sandbox — even one we only ever print — is the habit this file exists to break.
        var hostDir = Path.Combine(MainguardPaths.DataRoot(), "host-" + Guid.NewGuid().ToString("N"));
        var tokenPath = Path.Combine(hostDir, "daemon.token");

        var resolvers = FallbackResolvers().ToList();
        Assert.NotEmpty(resolvers); // a filter that matched nothing would make every loop here vacuous

        foreach (var method in resolvers)
        {
            Assert.True(
                method.GetParameters().Any(p => p.ParameterType == typeof(string)),
                $"DaemonHost.{method.Name} takes no token path, so it cannot sit beside the session "
                + "token and every in-proc host would share whatever it resolves to. Give it a "
                + "`string? tokenPath` parameter, or it is not a per-host store.");

            var resolved = Resolve(method, tokenPath);

            Assert.True(
                IsUnder(resolved, hostDir),
                $"DaemonHost.{method.Name} resolved to '{resolved}' when handed the session token at "
                + $"'{tokenPath}', instead of a path under '{hostDir}'. Every daemon store sits beside "
                + "the token: that is what gives each in-proc host its own copy, and what keeps a test "
                + "run out of the real daemon's stores. A resolver that ignores the token passes the "
                + "data-root fallback test and still shares one file across every host.");
        }
    }

    /// <summary>Every private static <c>Resolve*</c> on <see cref="DaemonHost"/> that yields a path.</summary>
    private static IEnumerable<MethodInfo> FallbackResolvers()
        => typeof(DaemonHost)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Resolve", StringComparison.Ordinal) && m.ReturnType == typeof(string));

    /// <summary>Invokes each resolver on its no-token-path branch — the fallback the damage came from.</summary>
    private static IEnumerable<(string Name, string Resolved)> ResolveEveryFallbackPath()
        => FallbackResolvers().Select(m => (m.Name, Resolve(m, tokenPath: null)));

    /// <summary>
    /// Invokes one resolver with <paramref name="tokenPath"/>. Arguments are supplied BY PARAMETER TYPE so
    /// the differing signatures need no per-method table — which is what lets a newly added resolver be
    /// covered without anyone remembering to wire it up. A <c>string</c> parameter is the token path (null
    /// selects the data-root fallback); the explicit-override inputs are left at their empty defaults so
    /// they never win over the branch under test.
    /// </summary>
    private static string Resolve(MethodInfo method, string? tokenPath)
    {
        var emptyConfig = new ConfigurationBuilder().Build();
        var args = method.GetParameters().Select(p =>
            p.ParameterType == typeof(DaemonOptions) ? new DaemonOptions()
            : typeof(IConfiguration).IsAssignableFrom(p.ParameterType) ? emptyConfig
            : p.ParameterType == typeof(string) ? tokenPath
            : (object?)null).ToArray();

        return (string)method.Invoke(null, args)!;
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

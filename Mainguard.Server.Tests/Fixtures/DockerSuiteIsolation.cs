using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// A host-global, cross-process advisory lock that serialises whole <c>RequiresDocker</c> RUNS against
/// one Docker daemon.
///
/// <para><b>Why a lock and not per-run resource names.</b> The suite's Docker state is shared BY
/// CONSTRUCTION, not by accident. The egress proxy is a singleton per daemon — one container called
/// <c>mainguard-egress-proxy</c>, whose whole design (PR #242) is that <c>EnsureReadyAsync</c> does NOT own
/// it and must tolerate it being stopped or removed at any moment — and the default-deny topology it
/// homes on (<c>mainguard-agents</c> / <c>mainguard-egress</c>) is addressed by literal name from the
/// static gates <c>EgressProxyConfigurator.IsDefaultDenyAgentNetwork</c> and
/// <c>ContainerSpecBuilder</c> apply to every jail (MG-7's fail-closed resolver pin, MG-18's posture
/// check). Giving a test run its own proxy therefore means renaming what those gates key on, which
/// either turns the gates off for the renamed topology or forces them to become instance-aware — a
/// production change on the security-critical path, to fix a testing defect. Several suites go further
/// and are ABOUT the singleton at its real name: the MG-18 drift test plants a deliberately-wrong
/// network under <c>mainguard-agents</c>, and the adoption tests stop and remove the proxy underneath a
/// concurrent spawn on purpose. Renaming them would delete the thing they prove.</para>
///
/// <para>So the resources stay shared and exactly as production names them, and what is isolated is the
/// RUN: two <c>dotnet test</c> invocations on one daemon take turns instead of fighting. That costs wall
/// time and weakens nothing — every assertion still runs against a real daemon, a real jail and the real
/// shared proxy.</para>
///
/// <para><b>Why a file lock.</b> It is the only primitive that survives the holder being killed:
/// <c>FileShare.None</c> is implemented on Unix as an <c>flock</c> on the open file description, which
/// the kernel drops when the process dies however it dies. A lock file holding a pid, or a named mutex,
/// would leave a crashed run's lock held forever. Measured on this box (.NET 10, Linux): a second
/// process opening the same path with <c>FileShare.None</c> gets <c>IOException</c> while the first
/// holds it, and acquires immediately once the first exits — including when the first is SIGKILLed. The
/// exclusion also holds between two opens in the SAME process, which is what lets
/// <c>FixtureAcceptanceTests.DockerSuiteLock_ExcludesASecondHolder_AndReleasesWhenDisposed</c> prove it
/// without spawning a child.</para>
/// </summary>
internal sealed class DockerSuiteLock : IDisposable
{
    /// <summary>Overrides the lock path. Point two runs at DIFFERENT paths only if they are also
    /// pointed at different Docker daemons — the lock is a stand-in for "one daemon".</summary>
    public const string PathVariable = "MAINGUARD_DOCKER_SUITE_LOCK";

    /// <summary>Overrides how long a run waits for its turn, in seconds.</summary>
    public const string TimeoutVariable = "MAINGUARD_DOCKER_SUITE_LOCK_TIMEOUT_SECONDS";

    /// <summary>The default wait. Generous on purpose: the whole point is that the second run WAITS
    /// rather than failing, and a full RequiresDocker pass is minutes, not seconds. It is bounded at all
    /// only so a wedged holder eventually produces a diagnosis instead of an eternal hang.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(60);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly FileStream _handle;

    private DockerSuiteLock(FileStream handle, string path, TimeSpan waited)
    {
        _handle = handle;
        Path = path;
        Waited = waited;
    }

    /// <summary>The lock file this run holds.</summary>
    public string Path { get; }

    /// <summary>How long this run queued behind another one. Zero on an idle daemon.</summary>
    public TimeSpan Waited { get; }

    /// <summary>The lock path for this machine (one per daemon — see <see cref="PathVariable"/>).</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured
            ? configured
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mainguard-docker-suite.lock");

    /// <summary>The configured wait, or <see cref="DefaultTimeout"/>.</summary>
    public static TimeSpan ConfiguredTimeout =>
        double.TryParse(
            Environment.GetEnvironmentVariable(TimeoutVariable),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultTimeout;

    /// <summary>
    /// Waits for exclusive use of the daemon and takes it. Throws only if the wait expires, and says so
    /// in terms that name the actual situation — a run that fails here failed because ANOTHER run held
    /// the daemon far too long, which is a different problem from anything the suite asserts.
    /// </summary>
    public static DockerSuiteLock Acquire(string? path = null, TimeSpan? timeout = null)
    {
        var lockPath = path ?? DefaultPath;
        var limit = timeout ?? ConfiguredTimeout;
        var clock = Stopwatch.StartNew();

        while (true)
        {
            if (TryOpen(lockPath) is { } handle)
            {
                return new DockerSuiteLock(handle, lockPath, clock.Elapsed);
            }

            if (clock.Elapsed >= limit)
            {
                throw new TimeoutException(
                    $"Waited {clock.Elapsed.TotalMinutes:F1} min for exclusive use of the Docker daemon "
                    + $"('{lockPath}') and never got it. Another RequiresDocker run is holding it — the "
                    + "suites share the egress proxy singleton and the mainguard networks by design, so "
                    + "they take turns. Let the other run finish, or raise "
                    + $"{TimeoutVariable}.");
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>
    /// One non-blocking attempt. Null means somebody else holds it. Separated out (and internal) so the
    /// exclusion itself is directly assertable — a lock that silently shared would make every claim in
    /// this file false while every test stayed green.
    /// </summary>
    internal static FileStream? TryOpen(string path)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null; // held by another run
        }
        catch (UnauthorizedAccessException)
        {
            return null; // held by another run under a stricter umask
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>
/// The collection fixture every <c>RequiresDocker</c> class hangs off. It holds
/// <see cref="DockerSuiteLock"/> for as long as the run's Docker tests are executing, and sweeps the
/// shared mainguard Docker state at both ends of that window.
///
/// <para><b>The startup sweep is not tidiness.</b> Teardown already removed the proxy and the networks
/// after every test, but only on the paths that ran to completion — a run killed mid-test (Ctrl-C, a CI
/// timeout, an agent harness reaping a process) leaves the proxy behind, and worse leaves per-agent
/// segments behind. Docker's default local bridge pool is only about 32 networks deep (#270 had to
/// reclaim segments after exhausting it), so leaked segments do not fail the run that leaked them: they
/// fail some later run's spawn with an address-pool error that has nothing to do with the test that
/// hits it. Sweeping on the way IN is what makes each run start from a known state regardless of how the
/// previous one ended.</para>
///
/// <para>The sweep is scoped to the resources mainguard names — the proxy container, the two topology
/// networks and the <c>mainguard-agent-</c> segments — and is exactly the operation the per-test
/// teardown already performs, moved earlier. It is best-effort throughout: a cleanup that throws would
/// replace a real failure with a useless one.</para>
/// </summary>
public sealed class DockerSuiteFixture : IDisposable
{
    private readonly DockerSuiteLock _lock;

    public DockerSuiteFixture()
    {
        var lockPath = DockerSuiteLock.DefaultPath;

        // A run that queues behind another one is otherwise indistinguishable from a hung run, and "the
        // suite hangs at the first Docker test" is exactly the kind of unexplained symptom this change
        // exists to stop costing people an investigation. So it is announced — on BOTH streams, though
        // be warned that `dotnet test` swallows the test host's stdout AND stderr (measured: neither
        // line reaches the console under the VSTest runner). The reliable live answer to "is it waiting
        // or wedged?" is therefore who holds the lock file named here: `fuser <path>` on Linux names the
        // holding process, and the queued run's own wall time exceeding its reported test Duration is
        // the same fact after the fact.
        Announce($"[docker-suite] waiting for exclusive use of the Docker daemon ({lockPath})…");
        _lock = DockerSuiteLock.Acquire(lockPath);
        Announce(
            $"[docker-suite] acquired after {_lock.Waited.TotalSeconds:F1}s — this run now owns the "
            + "shared egress proxy and the mainguard networks.");

        SweepAsync().GetAwaiter().GetResult();
    }

    private static void Announce(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
        Console.Error.WriteLine(line);
        Console.Error.Flush();
    }

    /// <summary>How long this run queued behind another one (0 on an idle daemon).</summary>
    public TimeSpan WaitedForDaemon => _lock.Waited;

    public void Dispose()
    {
        try
        {
            SweepAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _lock.Dispose();
        }
    }

    /// <summary>
    /// Removes the shared proxy, the two topology networks and every per-agent segment, if a daemon is
    /// reachable at all. No daemon means every test in the collection is skipping anyway, so there is
    /// nothing to sweep and nothing to report.
    /// </summary>
    private static async Task SweepAsync()
    {
        IDockerClient docker;
        try
        {
            docker = new DockerClientConfiguration().CreateClient();
        }
        catch
        {
            return; // no client could even be built — the suite skips
        }

        try
        {
            using var ping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await docker.System.PingAsync(ping.Token).ConfigureAwait(false);
        }
        catch
        {
            docker.Dispose();
            return; // no daemon — the suite skips
        }

        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await docker.Containers.RemoveContainerAsync(
                    EgressProxyConfigurator.ProxyContainerName,
                    new ContainerRemoveParameters { Force = true }, ct.Token).ConfigureAwait(false);
            }
            catch { /* already gone — the state we wanted */ }

            var networks = await docker.Networks
                .ListNetworksAsync(new NetworksListParameters(), ct.Token).ConfigureAwait(false);
            foreach (var net in networks)
            {
                if (net.Name is null || !IsMainguardNetwork(net.Name))
                {
                    continue;
                }

                try
                {
                    // Docker refuses to delete a network that still holds an endpoint, so a jail a
                    // crashed run left behind would silently pin the segment (and its bridge-pool slot)
                    // in place. Evict first, then delete.
                    var inspect = await docker.Networks.InspectNetworkAsync(net.ID, ct.Token).ConfigureAwait(false);
                    foreach (var endpoint in inspect.Containers ?? new Dictionary<string, EndpointResource>())
                    {
                        try
                        {
                            await docker.Networks.DisconnectNetworkAsync(net.ID,
                                new NetworkDisconnectParameters { Container = endpoint.Key, Force = true },
                                ct.Token).ConfigureAwait(false);
                        }
                        catch { /* best effort — the delete below reports the real problem */ }
                    }

                    await docker.Networks.DeleteNetworkAsync(net.ID, ct.Token).ConfigureAwait(false);
                }
                catch { /* best effort */ }
            }
        }
        catch { /* never fail a run from a sweep */ }
        finally
        {
            docker.Dispose();
        }
    }

    /// <summary>
    /// Is this one of the networks mainguard's egress topology owns? The two fixed names plus every
    /// per-agent segment. Pure so the scope of the sweep is assertable without a daemon — a predicate
    /// that quietly matched nothing would turn the whole sweep into a no-op with no visible symptom
    /// until a run failed on an exhausted address pool.
    /// </summary>
    internal static bool IsMainguardNetwork(string name) =>
        string.Equals(name, EgressProxyConfigurator.AgentNetworkName, StringComparison.Ordinal)
        || string.Equals(name, EgressProxyConfigurator.EgressNetworkName, StringComparison.Ordinal)
        || name.StartsWith(EgressProxyConfigurator.AgentSegmentPrefix, StringComparison.Ordinal);
}

/// <summary>
/// The xunit collection every <c>RequiresDocker</c> class joins. Membership is what puts a class inside
/// the daemon lock — a Docker test class WITHOUT
/// <c>[Collection(DockerSuiteCollection.Name)]</c> runs outside it and can still collide with a
/// concurrent run, so the attribute belongs on every class carrying
/// <c>[Trait("Category","RequiresDocker")]</c>. <c>FixtureAcceptanceTests</c> asserts that pairing
/// holds, by reflection, rather than leaving it to reviewer memory.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DockerSuiteCollection : ICollectionFixture<DockerSuiteFixture>
{
    public const string Name = "RequiresDocker";
}

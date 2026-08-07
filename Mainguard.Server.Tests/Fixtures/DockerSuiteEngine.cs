using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Agents.Sandbox;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// Which Docker engine the RequiresDocker run is about to modify, in words an operator can act on.
///
/// <para><b>Why this exists.</b> On a Windows workstation there are TWO dockerds, and mainguard names its
/// containers and networks identically on both:</para>
/// <list type="bullet">
///   <item><b>The app's engine</b> — a dockerd running INSIDE the <c>MainguardEnv</c> WSL distro.
///   <c>mainguardd</c> runs there as a systemd unit (<c>StartDaemonStep</c>) and builds its Docker client
///   with a bare <c>new DockerClientConfiguration()</c>, so "the local socket" for it is MainguardEnv's.
///   The owner's live jails, <c>mainguard-egress-proxy</c> and the <c>mainguard-agents</c>/
///   <c>mainguard-egress</c> topology live here.</item>
///   <item><b>The test engine</b> — whatever dockerd the shell running <c>dotnet test</c> sees, which on
///   this setup is Docker Desktop via WSL integration. The suite's jails, its proxy and its networks live
///   here, under the SAME names.</item>
/// </list>
///
/// <para>Nothing in a <c>docker ps</c> line distinguishes the two, which is exactly how a swept test jail
/// (<c>mainguard-&lt;repo&gt;-&lt;agent&gt;</c>, up, <c>Networks</c> empty) came to be read as the owner's
/// destroyed jail — by three readers in a row, on 2026-08-07. Naming the engine is therefore not decoration:
/// it is the one fact that makes the two situations tellable apart, and it belongs on every line the suite
/// emits about a destructive operation.</para>
/// </summary>
/// <param name="Endpoint">The socket/URI this client is actually talking to.</param>
/// <param name="Name">The engine's own name (<c>docker info</c> <c>Name</c> — the Docker host's hostname;
/// Docker Desktop reports <c>docker-desktop</c>).</param>
/// <param name="ServerVersion">The daemon version (<c>29.4.3</c> on Docker Desktop here, <c>20.10.24</c>
/// inside MainguardEnv — often the quickest tell).</param>
/// <param name="OperatingSystem">The engine host's OS string.</param>
/// <param name="DockerHost">The <c>DOCKER_HOST</c> value when one is set, else null. Named because it is
/// the one way a run can be pointed at a daemon other than the local socket, and the suite cannot tell
/// from the far side WHOSE daemon that is.</param>
internal sealed record DockerEngineIdentity(
    string Endpoint,
    string Name,
    string ServerVersion,
    string OperatingSystem,
    string? DockerHost)
{
    public const string DockerHostVariable = "DOCKER_HOST";

    /// <summary>Used when <c>docker info</c> could not be read. The endpoint is still known — a
    /// description that degraded to "unknown engine" and dropped the endpoint would leave the operator
    /// with exactly the ambiguity this type exists to remove.</summary>
    public static DockerEngineIdentity Unknown(string endpoint, string why) =>
        new(endpoint, "unknown", "unknown", $"docker info unavailable: {why}",
            Environment.GetEnvironmentVariable(DockerHostVariable) is { Length: > 0 } h ? h : null);

    /// <summary>Asks the daemon who it is. Never throws: an engine that cannot describe itself must not
    /// take down the run that was only trying to say which engine it is.</summary>
    public static async Task<DockerEngineIdentity> ResolveAsync(IDockerClient docker, CancellationToken ct)
    {
        var endpoint = SafeEndpoint(docker);
        try
        {
            var info = await docker.System.GetSystemInfoAsync(ct).ConfigureAwait(false);
            return new DockerEngineIdentity(
                endpoint,
                string.IsNullOrEmpty(info.Name) ? "unnamed" : info.Name,
                string.IsNullOrEmpty(info.ServerVersion) ? "unknown" : info.ServerVersion,
                string.IsNullOrEmpty(info.OperatingSystem) ? "unknown" : info.OperatingSystem,
                Environment.GetEnvironmentVariable(DockerHostVariable) is { Length: > 0 } h ? h : null);
        }
        catch (Exception ex)
        {
            return Unknown(endpoint, ex.GetType().Name);
        }
    }

    private static string SafeEndpoint(IDockerClient docker)
    {
        try { return docker.Configuration.EndpointBaseUri?.ToString() ?? "unknown"; }
        catch { return "unknown"; }
    }

    /// <summary>One line, always containing the endpoint — the part that says WHICH dockerd.</summary>
    public string Describe()
    {
        var line = $"{Name} — Docker {ServerVersion} ({OperatingSystem}) at {Endpoint}";
        return DockerHost is null ? line : $"{line} [{DockerHostVariable}={DockerHost}]";
    }
}

/// <summary>
/// Is this process running inside Mainguard OS itself?
///
/// <para>This is the one signal that is both cheap and honest. Inside the <c>MainguardEnv</c> distro the
/// local Docker socket IS the engine <c>mainguardd</c> spawns the owner's jails on, so a sweep there is
/// not a test cleanup — it is production damage. The marker is the release stamp the payload installs
/// (<see cref="VmUpgradeCommands.InstalledReleaseStampPath"/>), the same file the daemon's own version probe
/// reads; measured present in MainguardEnv and absent in a normal WSL distro.</para>
///
/// <para><b>What it does NOT prove.</b> A run whose <c>DOCKER_HOST</c> points into the VM from outside is
/// not detectable from the client side: the API exposes no field that says "this daemon hosts mainguardd",
/// and inventing one would mean either fingerprinting containers (which CI creates under the same names,
/// so it would fire on CI) or stamping the VM's dockerd — a payload change to catch a setup nobody has.
/// So the honest split is: refuse on the case that is provable, and NAME the endpoint on every other case
/// so the ambiguous one is at least readable. <see cref="DockerEngineIdentity.DockerHost"/> carries that.</para>
/// </summary>
internal static class MainguardOsHost
{
    /// <summary>The marker file, exposed so the detector can be tested against a real file in both
    /// directions — a probe hard-wired to an absolute path can only ever be tested one way, and the
    /// "always false" failure is invisible.</summary>
    public const string MarkerPath = VmUpgradeCommands.InstalledReleaseStampPath;

    public static bool IsInside() => IsInside(MarkerPath);

    public static bool IsInside(string markerPath)
    {
        try { return File.Exists(markerPath); }
        catch { return false; }
    }
}

/// <summary>The refusal, as its own type: the sweep swallows everything else by design, and the one
/// failure that must stop the run needs to be distinguishable from a flaky daemon call.</summary>
internal sealed class DockerSuiteRefusedException : Exception
{
    public DockerSuiteRefusedException(string message) : base(message) { }
}

/// <summary>
/// The pre-sweep invariant: the RequiresDocker suite may only sweep an engine whose mainguard state is
/// disposable. Pure, so both directions are assertable without a daemon — a guard that always fired would
/// take CI's whole security leg down, and one that never fired would be decoration.
/// </summary>
internal static class DockerSuiteSweepGuard
{
    /// <summary>Why this run must not sweep, or null when it may. The message names the engine, the
    /// evidence, what the sweep would have destroyed and where to run instead — a refusal that only said
    /// "refusing to sweep" would replace one unexplained symptom with another.</summary>
    public static string? RefusalFor(bool insideMainguardOs, DockerEngineIdentity engine)
    {
        if (!insideMainguardOs)
        {
            return null;
        }

        return
            "The RequiresDocker suite refuses to run against this Docker engine.\n"
            + $"  Engine  : {engine.Describe()}\n"
            + $"  Evidence: this process is running INSIDE Mainguard OS ('{MainguardOsHost.MarkerPath}' "
            + "exists), so the local Docker socket is the same dockerd mainguardd spawns the owner's agent "
            + "jails on.\n"
            + "\n"
            + $"The suite opens and closes by force-removing the '{EgressProxyConfigurator.ProxyContainerName}' "
            + $"container and deleting every mainguard network ('{EgressProxyConfigurator.AgentNetworkName}', "
            + $"'{EgressProxyConfigurator.EgressNetworkName}' and every '{EgressProxyConfigurator.AgentSegmentPrefix}' "
            + "segment), evicting whatever is attached to them first. Those are production's literal names and "
            + "cannot be renamed per run — MG-7's resolver pin and MG-18's posture gate key on them (PR #278) — "
            + "so on THIS engine the sweep would leave live jails running with no networks: unable to reach the "
            + "model API or any package registry, and with nothing in the symptom pointing back at a test run.\n"
            + "\n"
            + "Run the suite from outside Mainguard OS, on an engine whose mainguard-* resources are the "
            + "suite's own — Linux CI, or a normal WSL distro/host whose Docker engine the app does not use. "
            + $"If this run was pointed at a daemon deliberately, check {DockerEngineIdentity.DockerHostVariable}.";
    }
}

/// <summary>What one sweep actually did, so it can be reported (and asserted) as a fact rather than
/// assumed. Kept separate from the Docker calls: the reporting is the part that was missing, and the part
/// that must be provable without a daemon.</summary>
internal sealed record SweepOutcome(
    string ProxyContainer,
    bool ProxyRemoved,
    IReadOnlyList<SweptNetwork> Networks)
{
    public static SweepOutcome Nothing(string proxyContainer) =>
        new(proxyContainer, false, Array.Empty<SweptNetwork>());

    /// <summary>A human-readable account naming every resource removed, and saying so explicitly when
    /// nothing was — "swept" with no list is the shape that let a destructive step look like a no-op.</summary>
    public string Describe(DockerEngineIdentity engine)
    {
        var sb = new StringBuilder();
        sb.Append("swept ").Append(engine.Describe()).Append(": ");

        if (!ProxyRemoved && Networks.Count == 0)
        {
            sb.Append("nothing to remove (no ").Append(ProxyContainer).Append(", no mainguard networks).");
            return sb.ToString();
        }

        sb.Append(ProxyRemoved ? $"removed container {ProxyContainer}" : $"no {ProxyContainer} to remove");
        sb.Append("; ");
        sb.Append(Networks.Count.ToString(CultureInfo.InvariantCulture)).Append(" network(s) deleted");
        if (Networks.Count > 0)
        {
            sb.Append(": ").Append(string.Join(", ", Networks.Select(n => n.Describe())));
        }

        sb.Append('.');
        return sb.ToString();
    }
}

/// <summary>One deleted network and the container endpoints that were evicted from it to make the delete
/// possible. The evicted names are the load-bearing half: "your container lost its networks" is answered
/// by this list and by nothing else on the box.</summary>
internal sealed record SweptNetwork(string Name, string Id, IReadOnlyList<string> EvictedContainers)
{
    public string Describe()
    {
        var id = Id.Length > 12 ? Id[..12] : Id;
        return EvictedContainers.Count == 0
            ? $"{Name} ({id})"
            : $"{Name} ({id}, evicted {string.Join(" + ", EvictedContainers)})";
    }
}

/// <summary>
/// An append-only record of every destructive thing the RequiresDocker suite did, on which engine, and
/// when.
///
/// <para><b>Why a file.</b> The console is not a reliable channel here (measured: a fixture's
/// <c>Console.Out</c>/<c>Console.Error</c> reach <c>dotnet test</c> only at <c>--verbosity normal</c>, and
/// nothing at all reaches a run whose output was captured and discarded), and the question this record
/// answers is asked LATER: someone finds a <c>mainguard-*</c> container up with no networks, hours after
/// the run that detached it, and has to work out whether that was a test or a real fault. The container
/// carries no evidence either way. This file does: engine, timestamp, pid, and the name of every resource
/// removed.</para>
/// </summary>
internal sealed class DockerSuiteJournal
{
    /// <summary>Overrides the journal path (a run pointed at a different daemon should point this
    /// somewhere else too).</summary>
    public const string PathVariable = "MAINGUARD_DOCKER_SUITE_JOURNAL";

    public DockerSuiteJournal(string? path = null) =>
        Path = path ?? DefaultPath;

    public string Path { get; }

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured
            ? configured
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mainguard-docker-suite.log");

    /// <summary>Appends one stamped line. Never throws — a diagnostic that can fail a run would be worse
    /// than no diagnostic, and an unwritable journal must not be the reason a security suite goes red.</summary>
    public void Record(string line)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(Path, Stamp(line) + Environment.NewLine);
        }
        catch
        {
            // best effort by design
        }
    }

    /// <summary>The line shape, pure so its content is assertable: UTC instant, pid, then the text.</summary>
    public static string Stamp(string line, DateTimeOffset? at = null, int? pid = null) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd'T'HH:mm:ss'Z'} pid {1} [docker-suite] {2}",
            (at ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            pid ?? Environment.ProcessId,
            line);
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>Why one candidate segment was, or was not, reaped.</summary>
public enum SegmentReapDecision
{
    /// <summary>Nothing on this segment and no jail that could come back to it — delete it.</summary>
    Reap,

    /// <summary>A container with this segment's jail name exists (running or stopped). Its jail can be
    /// reused, and reuse needs the segment, so the segment stays.</summary>
    KeptJailExists,

    /// <summary>Created too recently to judge. A spawn creates the segment BEFORE the container, so a
    /// young empty segment is far more likely to be a spawn in flight than a leak.</summary>
    KeptTooYoung,

    /// <summary>Something other than the egress proxy is attached. Whatever it is, it is not ours to
    /// take a network away from.</summary>
    KeptOccupied,

    /// <summary>Not a Mainguard-stamped agent segment — wrong name shape, or missing the role label
    /// <see cref="EgressProxyConfigurator.NetworkRoleLabel"/> that says Mainguard created it.</summary>
    KeptNotOurs,
}

/// <summary>One segment's verdict, with the sentence explaining it.</summary>
public sealed record SegmentReapVerdict(string Segment, SegmentReapDecision Decision, string Reason)
{
    public bool Reap => Decision == SegmentReapDecision.Reap;
}

/// <summary>
/// The pure decision half of <see cref="SandboxSegmentReaper"/> (audit F27). Separated so every
/// refusal is unit-assertable without a Docker daemon — the value of a reaper is entirely in what it
/// refuses to delete, and that is the part a Docker-gated test would exercise least.
/// </summary>
public static class SandboxSegmentReapPolicy
{
    /// <summary>
    /// How old an empty segment must be before it counts as leaked.
    ///
    /// <para>A spawn creates the segment first and the container second, so between those two calls a
    /// perfectly healthy segment has no jail on it. Ten minutes is far longer than that window (which
    /// is seconds, or minutes when a toolchain layer builds in between) and far shorter than the time
    /// it takes leaked segments to matter — Docker's default address pool is exhausted by a few dozen,
    /// which takes a person, not a background loop, to produce.</para>
    /// </summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The jail name a segment belongs to, or null when the name is not a segment name at all.
    ///
    /// <para>The exact inverse of <see cref="EgressProxyConfigurator.AgentSegmentName"/>, which is
    /// <c>"mainguard-agent-" + containerName["mainguard-".Length..]</c>. That correlation is the whole
    /// reason the reaper can be conservative without a session store: it can ask Docker directly
    /// whether the jail this segment exists for is still on the machine.</para>
    /// </summary>
    public static string? JailNameFor(string? segmentName)
    {
        if (string.IsNullOrEmpty(segmentName)
            || !segmentName.StartsWith(EgressProxyConfigurator.AgentSegmentPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var suffix = segmentName[EgressProxyConfigurator.AgentSegmentPrefix.Length..];
        return suffix.Length == 0 ? null : "mainguard-" + suffix;
    }

    /// <param name="roleLabel">The segment's <see cref="EgressProxyConfigurator.NetworkRoleLabel"/>
    /// value, or null when it carries none.</param>
    /// <param name="createdUtc">The network's creation time, per the engine.</param>
    /// <param name="liveJailNames">Every container name on the engine, INCLUDING stopped ones. A
    /// stopped jail is a reusable jail, so its segment is not garbage.</param>
    /// <param name="attachedNames">Container names attached to the segment, with the egress proxy
    /// already excluded by the caller — the proxy is on every segment by construction, so counting it
    /// would mean no segment is ever reapable.</param>
    public static SegmentReapVerdict Decide(
        string segmentName,
        string? roleLabel,
        DateTimeOffset createdUtc,
        DateTimeOffset now,
        TimeSpan grace,
        IReadOnlyCollection<string> liveJailNames,
        IReadOnlyCollection<string> attachedNames)
    {
        ArgumentNullException.ThrowIfNull(liveJailNames);
        ArgumentNullException.ThrowIfNull(attachedNames);

        var jail = JailNameFor(segmentName);
        if (jail is null)
        {
            return new SegmentReapVerdict(segmentName ?? string.Empty, SegmentReapDecision.KeptNotOurs,
                "the name is not a per-agent segment name");
        }

        // The stamp Mainguard puts on every network it creates. Refusing to touch an unstamped network
        // is the same rule EnsureNetworkAsync already applies to REUSE, for the same reason: a network
        // that merely shares our prefix was created by something else, and deleting it is destructive
        // on the strength of a name.
        var expectedRole = EgressProxyConfigurator.RoleFor(isInternal: true);
        if (!string.Equals(roleLabel, expectedRole, StringComparison.Ordinal))
        {
            return new SegmentReapVerdict(segmentName, SegmentReapDecision.KeptNotOurs,
                $"missing the {EgressProxyConfigurator.NetworkRoleLabel}={expectedRole} stamp "
                + $"(found {(roleLabel is null ? "no such label" : $"'{roleLabel}'")})");
        }

        if (attachedNames.Count > 0)
        {
            return new SegmentReapVerdict(segmentName, SegmentReapDecision.KeptOccupied,
                $"{attachedNames.Count} container(s) still attached: {string.Join(", ", attachedNames.OrderBy(n => n, StringComparer.Ordinal))}");
        }

        // Ordinal, because container names are produced by ContainerSpecBuilder.ContainerName and are
        // compared to themselves — a case-insensitive match here would let two different repo hashes
        // shadow each other.
        if (liveJailNames.Contains(jail, StringComparer.Ordinal))
        {
            return new SegmentReapVerdict(segmentName, SegmentReapDecision.KeptJailExists,
                $"jail '{jail}' still exists on this engine, so a reuse spawn would need this segment");
        }

        var age = now - createdUtc;
        if (age < grace)
        {
            return new SegmentReapVerdict(segmentName, SegmentReapDecision.KeptTooYoung,
                $"created {FormatAge(age)} ago, inside the {FormatAge(grace)} grace — a spawn creates the "
                + "segment before the jail, so this is more likely in flight than leaked");
        }

        return new SegmentReapVerdict(segmentName, SegmentReapDecision.Reap,
            $"empty, stamped, and jail '{jail}' is gone; created {FormatAge(age)} ago");
    }

    private static string FormatAge(TimeSpan age) => age.TotalMinutes < 1
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)age.TotalSeconds)}s")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes}m");
}

/// <summary>
/// <b>Audit F27 — the sweep that removes leaked per-agent network segments.</b>
///
/// <para>A spawn creates its segment (<see cref="EgressProxyConfigurator.EnsureAgentSegmentAsync"/>)
/// BEFORE the container exists, and the spawn's own rollback removes the container and the worktree but
/// not the segment; only a clean teardown calls
/// <see cref="EgressProxyConfigurator.RemoveAgentSegmentAsync"/>. So every spawn that fails after the
/// segment and before the teardown path leaves a <c>/16</c> behind. Docker's default address pool
/// (<c>172.17.0.0/12</c> in <c>/16</c>s) holds a few dozen of those, and once it is exhausted EVERY
/// spawn fails at network creation, on a machine with no running agents — a failure whose cause is
/// invisible from anything Mainguard logs.</para>
///
/// <para><b>This is the sweep half, not the rollback half.</b> The rollback belongs beside the grant in
/// the launcher; a sweep additionally recovers the segments already leaked by daemons that ran before
/// that fix, and the ones a crash leaks past any rollback.</para>
///
/// <para><b>Conservative by construction.</b> Five independent conditions must all hold before a
/// network is deleted, and each of them alone is enough to save it: the name must be a segment name,
/// the network must carry Mainguard's own role label, nothing but the egress proxy may be attached, no
/// container with the segment's jail name may exist (running <i>or stopped</i> — a stopped jail is a
/// reusable one), and the segment must be older than
/// <see cref="SandboxSegmentReapPolicy.DefaultGrace"/>. A per-network failure is swallowed and the
/// sweep continues: a reaper that throws is a reaper someone disables.</para>
/// </summary>
public sealed class SandboxSegmentReaper
{
    private readonly IDockerClient _docker;
    private readonly TimeSpan _grace;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly string _proxyContainerName;

    /// <param name="grace">How old an empty segment must be. Null = <see cref="SandboxSegmentReapPolicy.DefaultGrace"/>.</param>
    /// <param name="clock">Injected so a test drives the grace without waiting it out.</param>
    /// <param name="proxyContainerName">The egress proxy, which is attached to every segment by
    /// construction and therefore never counts as an occupant.</param>
    public SandboxSegmentReaper(
        IDockerClient docker,
        TimeSpan? grace = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null,
        string proxyContainerName = EgressProxyConfigurator.ProxyContainerName)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _grace = grace ?? SandboxSegmentReapPolicy.DefaultGrace;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _proxyContainerName = proxyContainerName;
    }

    /// <summary>
    /// One pass. Returns the segments actually removed; every verdict, including the refusals, is
    /// reported through the log sink so an operator can see what the sweep decided and why.
    /// </summary>
    public async Task<IReadOnlyList<string>> SweepAsync(CancellationToken ct = default)
    {
        IList<NetworkResponse> networks;
        try
        {
            networks = await _docker.Networks.ListNetworksAsync(new NetworksListParameters(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"segment reaper: could not list networks ({ex.GetType().Name}); nothing swept");
            return Array.Empty<string>();
        }

        var candidates = networks
            .Where(n => !string.IsNullOrEmpty(n?.Name)
                && n!.Name.StartsWith(EgressProxyConfigurator.AgentSegmentPrefix, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
        {
            return Array.Empty<string>();
        }

        // ALL containers, not just running ones. A stopped jail is exactly the case the reuse path in
        // DockerSandboxEngine.SpawnAsync exists for; taking its segment away would turn every resume
        // into a recreate.
        HashSet<string> jailNames;
        try
        {
            var containers = await _docker.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }, ct).ConfigureAwait(false);
            jailNames = containers
                .SelectMany(c => c.Names ?? new List<string>())
                .Select(n => n.TrimStart('/'))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: without the container list every "the jail is gone" answer would be a guess,
            // and a wrong one deletes the network out from under a live agent.
            _log?.Invoke($"segment reaper: could not list containers ({ex.GetType().Name}); nothing swept");
            return Array.Empty<string>();
        }

        var now = _clock();
        var reaped = new List<string>();
        foreach (var network in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var verdict = await JudgeAsync(network, jailNames, now, ct).ConfigureAwait(false);
                if (!verdict.Reap)
                {
                    _log?.Invoke($"segment reaper: keeping {verdict.Segment} — {verdict.Reason}");
                    continue;
                }

                await RemoveAsync(network, ct).ConfigureAwait(false);
                reaped.Add(network.Name);
                _log?.Invoke($"segment reaper: removed {verdict.Segment} — {verdict.Reason}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad network never stops the sweep, and a failed delete is not an error: the next
                // sweep tries again, and until then the cost is one address-pool slot.
                _log?.Invoke($"segment reaper: {network.Name} skipped ({ex.GetType().Name}: {ex.Message})");
            }
        }

        return reaped;
    }

    private async Task<SegmentReapVerdict> JudgeAsync(
        NetworkResponse listed, IReadOnlyCollection<string> jailNames, DateTimeOffset now, CancellationToken ct)
    {
        // The LIST response does not populate Containers — only inspect does — so a decision taken on
        // the listing alone would read "empty" for a fully occupied network.
        NetworkResponse inspected;
        try
        {
            inspected = await _docker.Networks.InspectNetworkAsync(listed.ID, ct).ConfigureAwait(false);
        }
        catch (DockerNetworkNotFoundException)
        {
            // Gone between the list and the inspect: nothing to do, and nothing to report as a leak.
            return new SegmentReapVerdict(listed.Name, SegmentReapDecision.KeptNotOurs, "already gone");
        }

        var attached = (inspected.Containers ?? new Dictionary<string, EndpointResource>())
            .Select(kv => kv.Value?.Name ?? kv.Key)
            .Where(n => !string.Equals(n, _proxyContainerName, StringComparison.Ordinal))
            .ToArray();

        var role = inspected.Labels is not null
            && inspected.Labels.TryGetValue(EgressProxyConfigurator.NetworkRoleLabel, out var found)
            ? found
            : null;

        return SandboxSegmentReapPolicy.Decide(
            inspected.Name ?? listed.Name,
            role,
            new DateTimeOffset(DateTime.SpecifyKind(inspected.Created, DateTimeKind.Utc)),
            now,
            _grace,
            jailNames,
            attached);
    }

    private async Task RemoveAsync(NetworkResponse network, CancellationToken ct)
    {
        // Detach the proxy first, exactly as the teardown path does: Docker refuses to delete a network
        // that still holds an endpoint, and by this point the proxy is the only endpoint left.
        try
        {
            await _docker.Networks.DisconnectNetworkAsync(network.ID,
                new NetworkDisconnectParameters { Container = _proxyContainerName, Force = true }, ct)
                .ConfigureAwait(false);
        }
        catch (DockerApiException)
        {
            // Already detached, or the proxy is gone. Both fine.
        }

        await _docker.Networks.DeleteNetworkAsync(network.ID, ct).ConfigureAwait(false);
    }
}

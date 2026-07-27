using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>The verdict for a host against the egress policy.</summary>
public enum EgressVerdict
{
    Allowed,
    Denied,
}

/// <summary>
/// MG-36 — one agent's own default-deny network segment: the internal Docker network only that agent's
/// jail and the shared egress proxy are attached to, plus the proxy's address ON that segment.
/// </summary>
/// <param name="NetworkName">The segment the jail attaches to.</param>
/// <param name="ProxyAddress">The proxy's IPv4 on this segment — the jail's resolver pin AND the host
/// half of its <c>HTTP(S)_PROXY</c>. Null when the policy implementation does not segment (the seam's
/// default, and the substrate-less test fakes), in which case the engine keeps its configured proxy URL.</param>
public sealed record AgentSegment(string NetworkName, string? ProxyAddress)
{
    /// <summary>The <c>HTTP(S)_PROXY</c> URL for this segment, or null when the segment carries no
    /// address (see <see cref="ProxyAddress"/>).</summary>
    public string? ProxyUrl(int port) =>
        string.IsNullOrWhiteSpace(ProxyAddress) ? null : $"http://{ProxyAddress}:{port}";
}

/// <summary>
/// The engine-agnostic default-deny egress seam (P2-07). Fronts the concrete
/// <see cref="EgressProxyConfigurator"/> — an internal Docker network whose only route out is an
/// allowlist-driven proxy, plus pinned DNS and an iptables backstop. No Docker.DotNet types cross
/// this seam so the facade can hold it substrate-agnostically.
/// </summary>
public interface IEgressPolicy
{
    /// <summary>The user-visible, editable, change-logged allowlist (no git-host entry by default — A6).</summary>
    EgressAllowlist Allowlist { get; }

    /// <summary>The internal agent network name (default-deny; the proxy is the only gateway).</summary>
    string NetworkName { get; }

    /// <summary>The proxy URL agents route HTTP(S) egress through (<c>HTTP(S)_PROXY</c>).</summary>
    string ProxyUrl { get; }

    /// <summary>Ensure the internal network + proxy container + pinned DNS + iptables backstop exist (idempotent).</summary>
    Task EnsureReadyAsync(CancellationToken ct = default);

    /// <summary>
    /// MG-36 — ensure this agent's OWN default-deny segment exists, that the (already running) proxy is
    /// attached to it, and that the backstop admits the proxy's address on it. Idempotent, and safe to
    /// call on every spawn.
    ///
    /// <para>Segmentation is what stops east-west traffic: with every jail on one shared
    /// <c>mainguard-agents</c> network, agent A could reach agent B's container IP and ports directly.
    /// A single network cannot express "isolate the tenants but not the proxy" — Docker's
    /// <c>enable_icc=false</c> is all-or-nothing and severs the jail→proxy hop too (measured) — so the
    /// isolation is expressed as one internal network per agent, with the proxy as the only other
    /// member.</para>
    ///
    /// <para>The DEFAULT does not segment: it returns the shared <see cref="NetworkName"/> with no
    /// address, which is the pre-MG-36 topology. It exists so the substrate-less test doubles of this
    /// seam keep compiling; the shipped <see cref="EgressProxyConfigurator"/> always segments.</para>
    /// </summary>
    Task<AgentSegment> EnsureAgentSegmentAsync(string repoHash, string agentId, CancellationToken ct = default) =>
        Task.FromResult(new AgentSegment(NetworkName, null));

    /// <summary>
    /// MG-36 — best-effort teardown of one agent's segment once its jail is gone: detach the proxy and
    /// remove the network, so Docker's finite bridge-address pool is not consumed by dead agents. Never
    /// throws (a stop must not fail because a network lingered); a segment that still has a live
    /// endpoint is simply left in place.
    /// </summary>
    Task RemoveAgentSegmentAsync(string repoHash, string agentId, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>Policy verdict for a hostname (allowlist match) — the proxy enforces it, this reports it.</summary>
    EgressVerdict Evaluate(string host);
}

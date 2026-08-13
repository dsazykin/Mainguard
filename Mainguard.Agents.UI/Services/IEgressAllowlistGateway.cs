using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.UI.Services;

/// <summary>One allowlist row as the App sees it. <see cref="DefeatsA6"/> is computed daemon-side
/// (a git-host entry re-opens a direct route the A6 control removed) and surfaced with a warning.</summary>
public sealed record EgressAllowlistItem(string Name, string HostPattern, string Kind, bool DefeatsA6);

/// <summary>
/// The App's seam to the daemon-owned egress allowlist (P2-07). The App reaches sandboxes/egress
/// <b>only</b> through the daemon (ESC-I2/G-18) — this interface is implemented over
/// <c>DaemonClient</c> in production, so the App never references the container-control library or the
/// sandbox/egress engine seams. Every add/remove is change-logged daemon-side.
///
/// <para><b>Async because the real implementation is a gRPC round trip.</b> This was declared
/// synchronous, which is fine for the in-memory seed and impossible for the daemon: the only gateway
/// that existed was the in-memory one, so the allowlist editor could never be shown against the live
/// allowlist. Blocking on the RPC instead would have put a network call on the UI thread, inside a
/// ViewModel constructor.</para>
/// </summary>
public interface IEgressAllowlistGateway
{
    Task<IReadOnlyList<EgressAllowlistItem>> ListAsync(CancellationToken ct = default);
    Task AddAsync(string name, string hostPattern, string kind, CancellationToken ct = default);
    Task RemoveAsync(string hostPattern, CancellationToken ct = default);
}

/// <summary>
/// A standalone in-memory gateway seeded with the same defaults the daemon ships — used by the render
/// harness / design preview and as a safe default before a live daemon connection. The real gateway
/// forwards to the daemon over gRPC (where the authoritative allowlist + audit log live).
/// </summary>
public sealed class InMemoryEgressAllowlistGateway : IEgressAllowlistGateway
{
    private readonly List<EgressAllowlistItem> _items;

    public InMemoryEgressAllowlistGateway(IEnumerable<EgressAllowlistItem>? seed = null)
        => _items = (seed ?? DefaultSeed).ToList();

    public IReadOnlyList<EgressAllowlistItem> List() => _items.ToArray();

    public void Add(string name, string hostPattern, string kind)
    {
        if (_items.Any(i => string.Equals(i.HostPattern, hostPattern, StringComparison.OrdinalIgnoreCase)))
            return;
        _items.Add(new EgressAllowlistItem(name, hostPattern, kind, LooksLikeGitHost(hostPattern)));
    }

    public void Remove(string hostPattern)
        => _items.RemoveAll(i => string.Equals(i.HostPattern, hostPattern, StringComparison.OrdinalIgnoreCase));

    // The seam is async because the DAEMON implementation is a round trip; this one is a list, so the
    // async members are the sync ones wrapped. The sync API stays public — it is what the seed tests and
    // the render harness use, and making them await a completed task would buy nothing.
    Task<IReadOnlyList<EgressAllowlistItem>> IEgressAllowlistGateway.ListAsync(CancellationToken ct)
        => Task.FromResult(List());

    Task IEgressAllowlistGateway.AddAsync(string name, string hostPattern, string kind, CancellationToken ct)
    {
        Add(name, hostPattern, kind);
        return Task.CompletedTask;
    }

    Task IEgressAllowlistGateway.RemoveAsync(string hostPattern, CancellationToken ct)
    {
        Remove(hostPattern);
        return Task.CompletedTask;
    }

    // The daemon's own git-host heuristic, called rather than re-implemented: this used to be a
    // hand-copied mirror that had already drifted (its Azure arm was a substring `Contains`, so
    // `dev.azure.com.evil.net` rendered as a git host). One implementation cannot drift from itself.
    private static bool LooksLikeGitHost(string host)
        => Mainguard.Agents.Agents.Sandbox.EgressAllowlistEntry.LooksLikeGitHost(host ?? string.Empty);

    private static readonly IReadOnlyList<EgressAllowlistItem> DefaultSeed = new[]
    {
        new EgressAllowlistItem("Anthropic API", "api.anthropic.com", "ModelApi", false),
        new EgressAllowlistItem("OpenAI API", "api.openai.com", "ModelApi", false),
        new EgressAllowlistItem("npm registry", "registry.npmjs.org", "PackageRegistry", false),
        new EgressAllowlistItem("PyPI", "pypi.org", "PackageRegistry", false),
        new EgressAllowlistItem("PyPI files", "files.pythonhosted.org", "PackageRegistry", false),
        new EgressAllowlistItem("NuGet API", "api.nuget.org", "PackageRegistry", false),
        new EgressAllowlistItem("crates.io", "crates.io", "PackageRegistry", false),
        new EgressAllowlistItem("Go module proxy", "proxy.golang.org", "PackageRegistry", false),
    };
}

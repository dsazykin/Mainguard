using System;
using System.Collections.Generic;
using System.Linq;
using Docker.DotNet;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents;

/// <summary>The substrate-neutral collaborators one <see cref="IAgentEnvironment"/> composes.</summary>
internal sealed record ComposedSubstrateParts(
    string Root,
    RepoProvisioner Repos,
    WorktreeManager Worktrees,
    PackageCacheManager PackageCaches,
    EgressProxyConfigurator Egress,
    DockerSandboxEngine Sandboxes,
    DockerToolchainImageBuilder ToolchainImages);

/// <summary>
/// The composition every substrate shares, extracted from <see cref="Wsl2AgentEnvironment"/> when
/// the macos-host substrate arrived: provisioner, worktrees, package caches, egress, hardened
/// sandbox engine, toolchain-image builder. What stays per-substrate is exactly what the ESC says
/// should: the sync-remote resolution, the substrate id/capabilities, and the toolchain install
/// host. The comments below carry the original invariants — they bind BOTH substrates.
/// </summary>
internal static class AgentEnvironmentComposition
{
    internal static ComposedSubstrateParts Compose(
        string? vmRoot,
        IDockerClient? dockerClient,
        IAuditLog? auditLog,
        string? gatewayEndpoint,
        Func<string, string> syncRemoteUrlResolver)
    {
        // The root, resolved HERE rather than left to each collaborator's own default, because the
        // allowlist store below needs the same directory the mirrors and worktrees live in. Identical to
        // what RepoProvisioner/WorktreeManager would resolve for a null vmRoot, so nothing moves.
        var root = string.IsNullOrWhiteSpace(vmRoot)
            ? System.IO.Path.Combine(Mainguard.Git.MainguardPaths.HomeDirectory(), "mainguard")
            : vmRoot;

        // The provisioner's host-facing handle for a hash IS the resolved sync-remote URL.
        var provisioner = new RepoProvisioner(root, syncRemoteUrlResolver);

        // P2-07: hardened sandbox engine + default-deny egress. The Docker client connects lazily —
        // building it here does not require a live daemon (safe for construction/tests). The endpoint
        // comes from DockerEndpointResolver (DOCKER_HOST → CLI context → engine sockets → default);
        // on Windows that is always the library default, so the WSL2 path is unchanged.
        var docker = dockerClient ?? DockerEndpointResolver.CreateClient();
        var audit = auditLog ?? new InMemoryAuditLog();

        // MG-3: the worktree manager owns the mediated publish, so it gets the audit sink — a REFUSED
        // publish (an agent rewriting history the mirror already carries) is a security event and has to
        // leave a durable record, not just a log line.
        // MG-43: the package cache root is a sibling of repos/worktrees/agents under the SAME vmRoot,
        // so it inherits the MG-17 group-share the boot step provisions. The worktree manager is handed
        // the manager (not just the root) because a retired agent's cache is that agent's, and the one
        // teardown path every caller already goes through is RemoveAgentWorktree.
        var packageCaches = new PackageCacheManager(root);
        var worktrees = new WorktreeManager(root, audit: audit, packageCaches: packageCaches);

        // Auto-permit on install: the proxy config also permits the hosts each installed agent CLI
        // declared it needs (read fresh per spawn from the registry markers), so an installed CLI
        // reaches its own service hosts (e.g. claude-code → platform.claude.com) with no hand-editing.
        // A marker written before the egressHosts field (an existing install) has none, so we backfill
        // by adapter id from the bundled channel manifest — the fix then works after a daemon update
        // ALONE, with no CLI re-install.
        var adapters = new InstalledAdapterCatalog();
        var declaredHosts = LoadBundledEgressHosts();

        // The user's SAVED allowlist, not a fresh copy of the defaults. This line used to be
        // `EgressAllowlist.WithDefaults(audit)`, and because it runs on every daemon start, every
        // allowlist edit the user made through EgressGrpcService.Add/RemoveAllowlistHost was reverted by
        // the next restart or WSL idle-stop — audited and re-rendered onto the live proxy, then silently
        // gone. `ToPersistedForm`/`FromPersistedForm` already existed for exactly this and had no
        // production callers on either side. A first run (no file yet) still gets DefaultEntries, so the
        // shipped posture is unchanged.
        var egress = new EgressProxyConfigurator(
            docker, EgressAllowlist.LoadOrDefaults(audit, FileEgressAllowlistStore.UnderVmRoot(root)),
            // NOT the configurator's `gatewayUpstream:` — that emits tinyproxy `upstream` directives for
            // every model host, which would drag OAuth agents' traffic through the gateway and 401 it.
            // Confinement is per-agent and BYOK-only; the proxy just has to be willing to CARRY a
            // confined jail's request to the daemon, which is one allowlist entry.
            gatewayReachableAt: string.IsNullOrWhiteSpace(gatewayEndpoint) ? null : gatewayEndpoint,
            installedAdapterHosts: () => adapters.List()
                .SelectMany(m => m.EgressHosts
                    ?? (declaredHosts.TryGetValue(m.Id, out var fallback) ? fallback : Array.Empty<string>()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

        // MG-17: the userns mode is stated EXPLICITLY on the production substrate rather than left to a
        // positional default. This is the seam the audit found defaulting to a bare "" — the value is
        // still the empty string (Docker has no "definitely remap" per-container value; see
        // UsernsRemapPolicy.InheritDaemonRemap), but it now names the daemon-level remap it inherits, and
        // ContainerSpecBuilder refuses the "host" opt-out on the way out.
        var sandboxes = new DockerSandboxEngine(docker, new SandboxEngineOptions(
            egress.NetworkName, egress.ProxyUrl, UsernsRemapPolicy.InheritDaemonRemap));

        // The per-repo toolchain layer is built through the SAME Docker client, on the VM's network —
        // deliberately not through the jail's default-deny segment, and touching no allowlist.
        var toolchainImages = new DockerToolchainImageBuilder(docker);

        return new ComposedSubstrateParts(
            root, provisioner, worktrees, packageCaches, egress, sandboxes, toolchainImages);
    }

    /// <summary>Adapter id → declared egress hosts, from the bundled starter channel manifest — the
    /// fallback used to backfill install markers written before the <c>egressHosts</c> field existed,
    /// so a daemon update ALONE auto-permits an already-installed CLI's hosts (no re-install). Best
    /// effort: a manifest that cannot parse yields an empty map (auto-permit simply adds nothing).</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadBundledEgressHosts()
    {
        try
        {
            var manifest = AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson());
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var a in manifest.Adapters)
            {
                if (a.EgressHosts is { Count: > 0 } hosts)
                {
                    map[a.Id] = hosts;
                }
            }

            return map;
        }
        catch
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Docker.DotNet;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents;

/// <summary>
/// The WSL2 substrate implementation of <see cref="IAgentEnvironment"/>. Holds the real
/// P2-06 provisioner and worktree manager and resolves the host-side sync remote to a
/// <c>\\wsl.localhost\...</c> UNC handle. The <c>"mainguard-vm"</c> sync-remote name appears
/// in this method and NOWHERE else in the codebase (SC-2): every other layer registers
/// whatever <see cref="ResolveSyncRemote"/> returns, so the P2-25 cloud substrate can
/// resolve <c>mainguard-cloud</c> through the same seam.
/// </summary>
public sealed class Wsl2AgentEnvironment : IAgentEnvironment
{
    /// <summary>The default WSL2 sync-remote name (SC-2). Substrate-local by design.</summary>
    private const string Wsl2SyncRemoteName = "mainguard-vm";

    private readonly string _uncPrefix;

    /// <param name="vmRoot">The daemon-side ext4 base dir for mirrors/worktrees (defaults to <c>~/mainguard</c>).</param>
    /// <param name="userName">The Linux user whose home holds <c>mainguard/</c> (defaults to <c>USER</c>/<c>USERNAME</c>).</param>
    /// <param name="distroName">The WSL distro name in the UNC path (defaults to <c>MainguardEnv</c>).</param>
    /// <param name="dockerClient">The daemon-side Docker client (defaults to the local socket; connects lazily).</param>
    /// <param name="auditLog">Audit sink for allowlist-change events (defaults to the in-memory journal).</param>
    public Wsl2AgentEnvironment(
        string? vmRoot = null, string? userName = null, string? distroName = null,
        IDockerClient? dockerClient = null, IAuditLog? auditLog = null)
    {
        var user = string.IsNullOrEmpty(userName)
            ? Environment.GetEnvironmentVariable("USER") ?? Environment.GetEnvironmentVariable("USERNAME") ?? "mainguard"
            : userName;
        var distro = string.IsNullOrEmpty(distroName) ? "MainguardEnv" : distroName;

        // The Windows-facing UNC root of the VM's ~/<user>/mainguard/repos directory.
        _uncPrefix = $@"\\wsl.localhost\{distro}\home\{user}\mainguard\repos";

        // The provisioner's Windows-facing handle for a hash IS the resolved sync-remote URL.
        var provisioner = new RepoProvisioner(vmRoot, hash => ResolveSyncRemote(hash).Url);
        Repos = provisioner;

        // P2-07: hardened sandbox engine + default-deny egress. The Docker client connects lazily —
        // building it here does not require a live daemon (safe for construction/tests).
        var docker = dockerClient ?? new DockerClientConfiguration().CreateClient();
        var audit = auditLog ?? new InMemoryAuditLog();

        // MG-3: the worktree manager owns the mediated publish, so it gets the audit sink — a REFUSED
        // publish (an agent rewriting history the mirror already carries) is a security event and has to
        // leave a durable record, not just a log line.
        Worktrees = new WorktreeManager(vmRoot, audit: audit);
        // Auto-permit on install: the proxy config also permits the hosts each installed agent CLI
        // declared it needs (read fresh per spawn from the registry markers), so an installed CLI
        // reaches its own service hosts (e.g. claude-code → platform.claude.com) with no hand-editing.
        // A marker written before the egressHosts field (an existing install) has none, so we backfill
        // by adapter id from the bundled channel manifest — the fix then works after a daemon update
        // ALONE, with no CLI re-install.
        var adapters = new InstalledAdapterCatalog();
        var declaredHosts = LoadBundledEgressHosts();
        var egress = new EgressProxyConfigurator(
            docker, EgressAllowlist.WithDefaults(audit),
            installedAdapterHosts: () => adapters.List()
                .SelectMany(m => m.EgressHosts
                    ?? (declaredHosts.TryGetValue(m.Id, out var fallback) ? fallback : Array.Empty<string>()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        Egress = egress;
        // MG-17: the userns mode is stated EXPLICITLY on the production substrate rather than left to a
        // positional default. This is the seam the audit found defaulting to a bare "" — the value is
        // still the empty string (Docker has no "definitely remap" per-container value; see
        // UsernsRemapPolicy.InheritDaemonRemap), but it now names the daemon-level remap it inherits, and
        // ContainerSpecBuilder refuses the "host" opt-out on the way out.
        Sandboxes = new DockerSandboxEngine(docker, new SandboxEngineOptions(
            egress.NetworkName, egress.ProxyUrl, UsernsRemapPolicy.InheritDaemonRemap));

        // The per-repo toolchain layer is built through the SAME Docker client, on the VM's network —
        // deliberately not through the jail's default-deny segment, and touching no allowlist.
        ToolchainImages = new DockerToolchainImageBuilder(docker);
    }

    public string SubstrateId => "wsl2";

    public SubstrateCapabilities Capabilities { get; } =
        new(SupportsMaxIsolationBackend: false, SupportsWarmPoolPrestart: false,
            FilesystemTransport: "9p", LifecycleDialect: "wsl");

    public IRepoProvisioner Repos { get; }

    public IAgentWorktreeManager Worktrees { get; }

    public ISandboxEngine Sandboxes { get; }

    public IEgressPolicy Egress { get; }

    public IToolchainImageBuilder? ToolchainImages { get; }

    public SyncRemote ResolveSyncRemote(string repoHash)
        => new(Wsl2SyncRemoteName, $@"{_uncPrefix}\{repoHash}.git");

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

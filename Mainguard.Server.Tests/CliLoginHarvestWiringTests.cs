using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Agents.UI.Services;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The CALLER of the login harvest — the half of the credential round-trip that did not exist.
///
/// <para><b>The defect.</b> The design is: a CLI is signed into interactively inside its jail, its
/// declared <c>credentialPaths</c> are harvested back to the host OS keychain, and the next jail gets
/// them restored at boot. Restore was wired (<c>StartCoordinatorAsync</c> reads the vault and
/// <c>DockerSandboxEngine.RestoreCliCredentialsAsync</c> writes it into the tmpfs <c>$HOME</c>).
/// Harvest was not: <c>DaemonBackedOrchestrator.PersistLiveAgentLoginsAsync</c> — the sweep that pulls
/// every LIVE agent's login into the keychain — had <b>no callers anywhere in the repository</b>. The
/// only thing that ever wrote a <c>cli_login_*</c> entry was an explicit in-app Stop. So closing the
/// app, restarting the daemon, stopping the VM, or crashing all lost the login outright: the jail's
/// <c>$HOME</c> is tmpfs and dies with the container, and the user had to sign in again every session.
/// The RPC, the vault format and the restore were all present and correct — nothing drove them.</para>
///
/// <para><b>Why these tests are at this seam.</b> Both legs of the new wiring are asserted through the
/// SHIPPED <see cref="DaemonBackedOrchestrator"/> against the real in-proc daemon and the real
/// <c>HarvestAgentCredentials</c> RPC, and neither test calls <c>PersistLiveAgentLoginsAsync</c>
/// itself — that would re-create the exact blind spot (a harvest that works when a test invokes it and
/// never runs in the app). The keychain is an injected dictionary, so no test touches a real keyring.
/// The end-to-end jail→keychain→jail leg lives in <c>CliLoginRoundTripDockerTests</c>.</para>
/// </summary>
public sealed class CliLoginHarvestWiringTests
{
    /// <summary>The adapter kind under test. It declares ONE login file, so "the vault holds exactly
    /// this path with exactly these bytes" is a complete statement about the harvest.</summary>
    private const string AgentKind = "probe-cli";

    /// <summary>The declared <c>credentialPaths</c> entry — the only file the daemon may harvest.</summary>
    private const string LoginPath = ".probe/login.json";

    /// <summary>A per-run nonce standing in for the token an interactive login writes. Random so a
    /// stale vault entry from an earlier run could never satisfy the assertion.</summary>
    private static string NewLogin() => "{\"token\":\"" + Guid.NewGuid().ToString("N") + "\"}";

    [Fact]
    public async Task StartedOrchestrator_SweepsALiveAgentsLogin_IntoTheHostKeychain()
    {
        var login = NewLogin();
        using var rig = HarvestRig.Create(login);
        var vault = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        using var client = rig.NewClient();
        using var adapter = rig.NewAdapter(client, vault, TimeSpan.FromMilliseconds(200));

        // A live agent with a jail whose CLI has been signed into. Nothing has stopped it — which is
        // precisely the situation the old code never harvested in.
        var spawn = await rig.SpawnAsync();
        Assert.False(string.IsNullOrEmpty(spawn.AgentId));

        adapter.Start();

        var stored = await WaitForVaultAsync(vault, CliLoginVault.KeystoreKeyFor(AgentKind));
        Assert.NotNull(stored);

        var file = Assert.Single(CliLoginVault.Parse(stored));
        Assert.Equal(LoginPath, file.Path);
        Assert.Equal(login, Encoding.UTF8.GetString(file.Content));
    }

    [Fact]
    public async Task Dispose_HarvestsOneLastTime_SoClosingTheAppKeepsTheLogin()
    {
        var login = NewLogin();
        using var rig = HarvestRig.Create(login);
        var vault = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var key = CliLoginVault.KeystoreKeyFor(AgentKind);

        using var client = rig.NewClient();
        // A sweep interval no test run can reach, so anything that lands in the vault below came from
        // the shutdown harvest and from nothing else.
        var adapter = rig.NewAdapter(client, vault, TimeSpan.FromMinutes(30));

        var spawn = await rig.SpawnAsync();
        Assert.False(string.IsNullOrEmpty(spawn.AgentId));

        adapter.Start();
        Assert.False(vault.ContainsKey(key), "the periodic sweep must not have run — its interval is 30 minutes");

        adapter.Dispose();

        Assert.True(vault.TryGetValue(key, out var stored),
            "closing the app must harvest the live agent's login before the client is torn down");
        var file = Assert.Single(CliLoginVault.Parse(stored));
        Assert.Equal(login, Encoding.UTF8.GetString(file.Content));
    }

    private static async Task<string?> WaitForVaultAsync(
        ConcurrentDictionary<string, string> vault, string key)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (vault.TryGetValue(key, out var stored))
            {
                return stored;
            }

            await Task.Delay(50);
        }

        return null;
    }

    /// <summary>
    /// An in-proc daemon whose substrate is a fake (no Docker) but whose SPAWN, HARVEST and RPC path
    /// are the production ones: a temp adapter registry declaring <see cref="LoginPath"/>, and a
    /// sandbox engine that answers the daemon's own harvest exec with the login bytes — i.e. a jail
    /// whose CLI is signed in.
    /// </summary>
    private sealed class HarvestRig : IDisposable
    {
        private const string RepoHandle = "harvest-rig-repo";

        private readonly string _root;

        private HarvestRig(string root) => _root = root;

        public required WebApplicationFactory<Program> Host { get; init; }

        public static HarvestRig Create(string login)
        {
            var root = Path.Combine(Path.GetTempPath(), "mg-login-harvest-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(root, "repos", RepoHandle)); // "provisioned"

            // The marker shape the VM installer writes — the ONLY declaration of what may be harvested.
            var registry = Path.Combine(root, "registry");
            Directory.CreateDirectory(registry);
            File.WriteAllText(
                Path.Combine(registry, AgentKind + ".json"),
                InstalledAdapterMarker.Serialize(new InstalledAdapterMarker(
                    AgentKind, "1.0.0", new[] { "/bin/true" },
                    ApiKeyEnvVar: null, EgressHosts: null, CredentialPaths: new[] { LoginPath })));

            var host = new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentEnvironment>(new SignedInAgentEnvironment(root, login));
                services.AddSingleton(new InstalledAdapterCatalog(registry));
                // The CLI bind would otherwise try a real `docker exec` PTY against a fake container id.
                services.AddSingleton(sp => new AgentCliBinder(
                    sp.GetRequiredService<TerminalSessionManager>(),
                    sp.GetRequiredService<Mainguard.Agents.Agents.Orchestrator.SessionLeader>(),
                    sp.GetRequiredService<AgentSessionStore>(),
                    sp.GetRequiredService<Mainguard.Git.Audit.IAuditLog>(),
                    _ => new InertTerminalSession()));
            }));

            return new HarvestRig(root) { Host = host };
        }

        public Metadata Auth => new()
        {
            { "authorization", $"bearer {Host.Services.GetRequiredService<SessionTokenFile>().Token}" },
        };

        public DaemonClient NewClient() => new(
            () => GrpcChannel.ForAddress(
                Host.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Host.Server.CreateHandler() }),
            () => Host.Services.GetRequiredService<SessionTokenFile>().Token);

        public DaemonBackedOrchestrator NewAdapter(
            DaemonClient client, ConcurrentDictionary<string, string> vault, TimeSpan harvestInterval) =>
            new(client, ownsClient: false,
                keystoreLookup: k => vault.TryGetValue(k, out var v) ? v : null,
                keystoreList: _ => Array.Empty<string>(),
                keystoreSave: (k, v) => vault[k] = v,
                loginHarvestInterval: harvestInterval);

        public Task<SpawnAgentResponse> SpawnAsync()
        {
            var agents = new AgentService.AgentServiceClient(GrpcChannel.ForAddress(
                Host.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Host.Server.CreateHandler() }));
            return agents.SpawnAgentAsync(
                new SpawnAgentRequest { RepoHandle = RepoHandle, AgentKind = AgentKind }, Auth).ResponseAsync;
        }

        public void Dispose()
        {
            Host.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* never fail a test from cleanup */ }
        }

        /// <summary>A substrate whose jails are strings and whose ONE interesting behaviour is that the
        /// declared login file exists inside the jail — i.e. the CLI has been signed into.</summary>
        private sealed class SignedInAgentEnvironment : IAgentEnvironment
        {
            public SignedInAgentEnvironment(string root, string login)
            {
                Repos = new FakeProvisioner(root);
                Worktrees = new FakeWorktrees(root);
                Sandboxes = new SignedInSandboxEngine(login);
            }

            public string SubstrateId => "fake";

            public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

            public IRepoProvisioner Repos { get; }

            public IAgentWorktreeManager Worktrees { get; }

            public ISandboxEngine Sandboxes { get; }

            public IEgressPolicy Egress { get; } = new FakeEgress();

            public SyncRemote ResolveSyncRemote(string repoHash) => new("fake-remote", $"fake://{repoHash}");

            private sealed class FakeProvisioner : IRepoProvisioner
            {
                private readonly string _root;

                public FakeProvisioner(string root) => _root = root;

                public ProvisionResult Provision(string windowsRepoPathNormalized) =>
                    throw new NotSupportedException("not exercised");

                public string BareRepoPathFor(string repoHash) => Path.Combine(_root, "repos", repoHash);
            }

            private sealed class FakeWorktrees : IAgentWorktreeManager
            {
                private readonly string _root;

                public FakeWorktrees(string root) => _root = root;

                public string CreateAgentWorktree(string repoHash, string agentId)
                {
                    var path = Path.Combine(_root, "wt", repoHash, agentId);
                    Directory.CreateDirectory(path);
                    return path;
                }

                public void RemoveAgentWorktree(string repoHash, string agentId, bool force)
                {
                    try { Directory.Delete(Path.Combine(_root, "wt", repoHash, agentId), recursive: true); }
                    catch (DirectoryNotFoundException) { }
                }

                public void Prune(string repoHash) { }

                public IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
                    Array.Empty<Mainguard.Git.Models.WorktreeItem>();
            }

            private sealed class FakeEgress : IEgressPolicy
            {
                public EgressAllowlist Allowlist { get; } =
                    EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog());

                public string NetworkName => "fake-net";

                public string ProxyUrl => "http://fake-proxy:3128";

                public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

                public EgressVerdict Evaluate(string host) => EgressVerdict.Denied;
            }
        }

        /// <summary>
        /// Answers the daemon's OWN harvest exec — <c>sh -c '[ -f "$1" ] &amp;&amp; base64 "$1"' sh
        /// &lt;path&gt;</c> — with the login bytes, and only for the declared path. Any other path
        /// answers "absent" exactly as a real jail would, so a harvest that read the wrong file, or read
        /// every file, would not pass.
        /// </summary>
        private sealed class SignedInSandboxEngine : ISandboxEngine
        {
            private static readonly string DeclaredJailPath = ContainerSpecBuilder.AgentHome + "/" + LoginPath;

            private readonly string _login;

            public SignedInSandboxEngine(string login) => _login = login;

            public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) =>
                Task.FromResult(new SandboxHandle($"ctr-{request.AgentId}", Reused: false));

            public Task<SandboxExecResult> ExecAsync(
                string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
            {
                var path = command.LastOrDefault();
                return Task.FromResult(string.Equals(path, DeclaredJailPath, StringComparison.Ordinal)
                    ? new SandboxExecResult(0, Convert.ToBase64String(Encoding.UTF8.GetBytes(_login)), string.Empty)
                    : new SandboxExecResult(1, string.Empty, string.Empty));
            }

            public Task PauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task UnpauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
        }

        /// <summary>A terminal that never starts a process — the CLI bind is not what these tests are
        /// about, and the production factory would try a real <c>docker exec</c> on a fake jail.</summary>
        private sealed class InertTerminalSession : ITerminalSession
        {
            private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Stream IO { get; } = new MemoryStream();

            public Task<int> ExitCode => _exit.Task;

            public void Resize(int cols, int rows) { }

            public void Kill() => _exit.TrySetResult(0);

            public void Dispose() => Kill();
        }
    }
}
